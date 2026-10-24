// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.PostFx.Passes;

/// <summary>
/// The temporal accumulation of a view: its jitter, its history and the views' matrices it reprojects with.
/// </summary>
internal sealed class TemporalHistory : IDisposable
{
    private const int JitterCount = 8;

    public readonly Texture[] Textures = new Texture[2];
    public int Index;
    public bool Valid;
    public int JitterIndex;

    // This frame's jitter (pixels, +y down) and the previous frame's, in normalized device coordinates
    public Vector2 Jitter;
    public Vector2 JitterNdc;
    public Vector2 PreviousJitterNdc;

    public Matrix ViewProjection;
    public Matrix UnjitteredViewProjection;
    public Matrix PreviousUnjitteredViewProjection;
    public bool HasPrevious;
    public Vector3 Eye;
    public Vector3 PreviousEye;

    /// <summary>
    /// Keeps the view's matrices and offsets its projection by the next sub-pixel jitter (from the Halton 2, 3 sequence,
    /// when jittering).
    /// </summary>
    public void ApplyJitter(RenderView view, bool jitter = true)
    {
        PreviousJitterNdc = JitterNdc;
        PreviousUnjitteredViewProjection = UnjitteredViewProjection;
        PreviousEye = Eye;
        HasPrevious = UnjitteredViewProjection != default;

        Matrix.Invert(ref view.View, out var viewInverse);
        Eye = viewInverse.TranslationVector;
        UnjitteredViewProjection = view.ViewProjection;

        JitterIndex = (JitterIndex + 1) % JitterCount;
        Jitter = jitter ? new Vector2(Halton(JitterIndex + 1, 2) - 0.5f, Halton(JitterIndex + 1, 3) - 0.5f) : Vector2.Zero;
        var size = new Vector2(Math.Max(view.ViewSize.X, 1.0f), Math.Max(view.ViewSize.Y, 1.0f));
        JitterNdc = new Vector2(Jitter.X * 2.0f / size.X, -Jitter.Y * 2.0f / size.Y);

        // Row vectors: x_clip = x M11 + z M31 + M41, w = z M34 + M44 (w = -z in perspective, 1 in orthographic)
        if (view.Projection.M44 == 1.0f)
        {
            view.Projection.M41 += JitterNdc.X;
            view.Projection.M42 += JitterNdc.Y;
        }
        else
        {
            view.Projection.M31 -= JitterNdc.X * -view.Projection.M34;
            view.Projection.M32 -= JitterNdc.Y * -view.Projection.M34;
        }
        Matrix.Multiply(ref view.View, ref view.Projection, out view.ViewProjection);
        ViewProjection = view.ViewProjection;
    }

    public void Dispose()
    {
        for (int i = 0; i < Textures.Length; i++)
        {
            Textures[i]?.Dispose();
            Textures[i] = null;
        }
        Valid = false;
    }

    private static float Halton(int index, int radix)
    {
        var result = 0.0f;
        var fraction = 1.0f / radix;
        while (index > 0)
        {
            result += index % radix * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }
}

/// <summary>
/// Temporal anti-aliasing: resolves the jittered scene with the reprojected history into the view's next history.
/// </summary>
internal sealed class TemporalPass
{
    private readonly ImageEffectShader shader;

    public TemporalPass(ImageEffectShader shader)
    {
        this.shader = shader;
    }

    /// <summary>
    /// The anti-aliased scene (the view's new history).
    /// </summary>
    public Texture Draw(RenderDrawContext context, TemporalHistory history, Texture scene, Texture depth, Texture motion, Buffer autoExposure, float exposure, float blend)
    {
        var device = context.GraphicsDevice;
        var width = scene.ViewWidth;
        var height = scene.ViewHeight;
        for (int i = 0; i < 2; i++)
        {
            var texture = history.Textures[i];
            if (texture == null || texture.Width != width || texture.Height != height)
            {
                texture?.Dispose();
                history.Textures[i] = Texture.New2D(device, width, height, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                history.Textures[i].Name = "PostFxHistory";
                history.Valid = false;
            }
        }

        // A camera cut (a teleport, a new area) starts the history over
        if (history.HasPrevious && Vector3.Distance(history.Eye, history.PreviousEye) > 50.0f)
            history.Valid = false;

        var previous = history.Textures[history.Index];
        var output = history.Textures[1 - history.Index];

        Matrix.Invert(ref history.ViewProjection, out var inverseViewProjection);
        var parameters = shader.Parameters;
        parameters.Set(PostFxTemporalKeys.PostFxTemporalAutoExposure, autoExposure);
        parameters.Set(PostFxTemporalKeys.TemporalInputTexel, new Vector2(1.0f / width, 1.0f / height));
        parameters.Set(PostFxTemporalKeys.TemporalOutputTexel, new Vector2(1.0f / output.Width, 1.0f / output.Height));
        parameters.Set(PostFxTemporalKeys.TemporalJitter, history.Jitter);
        parameters.Set(PostFxTemporalKeys.TemporalJitterDelta, history.JitterNdc - history.PreviousJitterNdc);
        parameters.Set(PostFxTemporalKeys.TemporalInverseViewProjection, inverseViewProjection);
        parameters.Set(PostFxTemporalKeys.TemporalPreviousViewProjection, history.HasPrevious ? history.PreviousUnjitteredViewProjection : history.UnjitteredViewProjection);
        parameters.Set(PostFxTemporalKeys.TemporalExposure, exposure);
        parameters.Set(PostFxTemporalKeys.TemporalBlend, blend);
        parameters.Set(PostFxTemporalKeys.TemporalHistoryValid, history.Valid ? 1.0f : 0.0f);

        shader.SetInput(0, scene);
        shader.SetInput(1, depth);
        shader.SetInput(2, motion);
        shader.SetInput(3, previous);
        shader.SetOutput(output);
        shader.Draw(context, "PostFx.Temporal");

        history.Index = 1 - history.Index;
        history.Valid = true;
        return output;
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.PostFx.Passes;

/// <summary>
/// The automatic exposure of a view, all on the GPU: a histogram of the scene's luminance, then the adapted exposure in a
/// buffer the final pass reads (no read back).
/// </summary>
internal sealed class ExposurePass : IDisposable
{
    // The luminance range of the histogram (log2): from moonless night to the sun's surroundings
    private static readonly Vector2 Range = new(-14.0f, 18.0f);

    private readonly ComputeEffectShader histogram;
    private readonly ComputeEffectShader adapt;
    private Buffer bins;

    public ExposurePass(ComputeEffectShader histogram, ComputeEffectShader adapt)
    {
        this.histogram = histogram;
        this.adapt = adapt;
    }

    /// <summary>
    /// A buffer holding a linear exposure of 1 (fixed exposure).
    /// </summary>
    public Buffer Unit { get; private set; }

    public void Initialize(GraphicsDevice device)
    {
        bins = Buffer.New(device, 128 * sizeof(uint), sizeof(uint), BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32_UInt);
        bins.Name = "PostFxHistogram";
        Unit = Buffer.Structured.New(device, new Vector4[] { new(1.0f, 0.0f, 1.0f, 0.0f) });
        Unit.Name = "PostFxUnitExposure";
    }

    /// <summary>
    /// Creates the adaptation state of a view (x = linear exposure, y = adapted log2 luminance, z = initialized).
    /// </summary>
    public static Buffer CreateState(GraphicsDevice device)
    {
        var state = Buffer.Structured.New(device, new Vector4[] { new(1.0f, 0.0f, 0.0f, 0.0f) }, unorderedAccess: true);
        state.Name = "PostFxExposureState";
        return state;
    }

    public void Draw(RenderDrawContext context, Texture scene, Buffer state, PostFxExposureSettings settings, float elapsed)
    {
        var commandList = context.CommandList;
        commandList.ClearReadWrite(bins, UInt4.Zero);

        histogram.Parameters.Set(PostFxHistogramKeys.PostFxHistogramScene, scene);
        histogram.Parameters.Set(PostFxHistogramKeys.PostFxHistogramBins, bins);
        histogram.Parameters.Set(PostFxHistogramKeys.PostFxHistogramSize, new Int2(scene.ViewWidth, scene.ViewHeight));
        histogram.Parameters.Set(PostFxHistogramKeys.PostFxHistogramRange, Range);
        histogram.ThreadNumbers = new Int3(16, 16, 1);
        histogram.ThreadGroupCounts = new Int3((scene.ViewWidth / 4 + 15) / 16, (scene.ViewHeight / 4 + 15) / 16, 1);
        histogram.Draw(context, "PostFx.Histogram");

        // (the settings' range is that of the scene's average luminance, in EV: log2 of it)
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptBins, bins);
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptExposure, state);
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptRange, Range);
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptClamp, new Vector2(settings.MinimumEV, Math.Max(settings.MinimumEV, settings.MaximumEV)));
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptSpeed, new Vector3(settings.SpeedUp, settings.SpeedDown, Math.Clamp(elapsed, 0.0f, 0.5f)));
        adapt.Parameters.Set(PostFxExposureAdaptKeys.PostFxAdaptPercentiles, new Vector2(0.5f, 0.05f));
        adapt.ThreadNumbers = new Int3(128, 1, 1);
        adapt.ThreadGroupCounts = new Int3(1, 1, 1);
        adapt.Draw(context, "PostFx.ExposureAdapt");
        // For the passes reading it (on Direct3D 11, a buffer still bound for writing would be unbound from them: no exposure)
        commandList.ResourceBarrierTransition(state, BarrierLayout.ShaderResource);
    }

    public void Dispose()
    {
        bins?.Dispose();
        Unit?.Dispose();
    }
}

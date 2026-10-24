// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;

namespace Stride.PostFx.Passes;

/// <summary>
/// The screen-space targets of a view, kept between frames (their size follows the view's).
/// </summary>
internal sealed class ScreenSpaceTargets : IDisposable
{
    public const int PyramidLevels = 5;

    public Texture Pyramid;
    public readonly Texture[] PyramidLevelViews = new Texture[PyramidLevels];
    public Texture AmbientOcclusion;
    public Texture AmbientOcclusionRaw;
    public Texture ContactShadows;

    public void Ensure(GraphicsDevice device, int width, int height)
    {
        if (Pyramid != null && Pyramid.Width == width && Pyramid.Height == height)
            return;
        Dispose();
        Pyramid = Texture.New2D(device, width, height, PyramidLevels, PixelFormat.R32_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        Pyramid.Name = "PostFxDistancePyramid";
        for (int i = 0; i < PyramidLevels; i++)
            PyramidLevelViews[i] = Pyramid.ToTextureView(ViewType.Single, 0, i);
        AmbientOcclusion = Texture.New2D(device, width, height, PixelFormat.R8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        AmbientOcclusion.Name = "PostFxAmbientOcclusion";
        AmbientOcclusionRaw = Texture.New2D(device, width, height, PixelFormat.R8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        AmbientOcclusionRaw.Name = "PostFxAmbientOcclusionRaw";
        ContactShadows = Texture.New2D(device, width, height, PixelFormat.R8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        ContactShadows.Name = "PostFxContactShadows";
    }

    public void Dispose()
    {
        for (int i = 0; i < PyramidLevels; i++)
        {
            PyramidLevelViews[i]?.Dispose();
            PyramidLevelViews[i] = null;
        }
        Pyramid?.Dispose();
        AmbientOcclusion?.Dispose();
        AmbientOcclusionRaw?.Dispose();
        ContactShadows?.Dispose();
        Pyramid = AmbientOcclusion = AmbientOcclusionRaw = ContactShadows = null;
    }
}

/// <summary>
/// The screen-space terms drawn from the depth prepass: the distance pyramid, the ambient occlusion (GTAO, denoised) and
/// the contact shadows.
/// </summary>
internal sealed class ScreenSpacePass
{
    private readonly ComputeEffectShader pyramid;
    private readonly ComputeEffectShader gtao;
    private readonly ComputeEffectShader denoise;
    private readonly ComputeEffectShader contact;

    public ScreenSpacePass(RenderContext context)
    {
        pyramid = new ComputeEffectShader(context) { ShaderSourceName = "PostFxDepthPyramid", ThreadNumbers = new Int3(8, 8, 1) };
        gtao = new ComputeEffectShader(context) { ShaderSourceName = "PostFxGtao", ThreadNumbers = new Int3(8, 8, 1) };
        denoise = new ComputeEffectShader(context) { ShaderSourceName = "PostFxGtaoDenoise", ThreadNumbers = new Int3(8, 8, 1) };
        contact = new ComputeEffectShader(context) { ShaderSourceName = "PostFxContactShadows", ThreadNumbers = new Int3(8, 8, 1) };
    }

    private static void SetView(ParameterCollection parameters, RenderView view, int width, int height)
    {
        var projection = view.Projection;
        parameters.Set(PostFxViewSpaceKeys.ViewProjectionInfo, new Vector4(1.0f / projection.M11, 1.0f / projection.M22, projection.M31, projection.M32));
        parameters.Set(PostFxViewSpaceKeys.ViewDepthInfo, new Vector2(projection.M33, projection.M43));
        parameters.Set(PostFxViewSpaceKeys.ViewSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
    }

    private static Int3 Groups(int width, int height) => new((width + 7) / 8, (height + 7) / 8, 1);

    public void DrawPyramid(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, Texture depth)
    {
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        for (int level = 0; level < ScreenSpaceTargets.PyramidLevels; level++)
        {
            var levelWidth = Math.Max(1, width >> level);
            var levelHeight = Math.Max(1, height >> level);
            var parameters = pyramid.Parameters;
            SetView(parameters, view, width, height);
            parameters.Set(PostFxDepthPyramidKeys.PyramidSource, level == 0 ? depth : targets.PyramidLevelViews[level - 1]);
            parameters.Set(PostFxDepthPyramidKeys.PyramidOutput, targets.PyramidLevelViews[level]);
            parameters.Set(PostFxDepthPyramidKeys.PyramidSize, new Int2(levelWidth, levelHeight));
            parameters.Set(PostFxDepthPyramidKeys.PyramidFromDepth, level == 0);
            pyramid.ThreadGroupCounts = Groups(levelWidth, levelHeight);
            pyramid.Draw(context, "PostFx.DistancePyramid");
        }
    }

    public void DrawAmbientOcclusion(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, PostFxAmbientOcclusionSettings settings, int slices, int steps, int frame)
    {
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        var parameters = gtao.Parameters;
        SetView(parameters, view, width, height);
        parameters.Set(PostFxGtaoKeys.GtaoDistance, targets.Pyramid);
        parameters.Set(PostFxGtaoKeys.GtaoOutput, targets.AmbientOcclusionRaw);
        parameters.Set(PostFxGtaoKeys.GtaoRadius, Math.Max(settings.Radius, 0.05f));
        parameters.Set(PostFxGtaoKeys.GtaoPower, 1.5f);
        parameters.Set(PostFxGtaoKeys.GtaoSlices, slices);
        parameters.Set(PostFxGtaoKeys.GtaoSteps, steps);
        parameters.Set(PostFxGtaoKeys.GtaoFrame, (float)(frame % 1024));
        parameters.Set(PostFxGtaoKeys.GtaoPixelsPerMeter, 0.5f * height * view.Projection.M22);
        gtao.ThreadGroupCounts = Groups(width, height);
        gtao.Draw(context, "PostFx.Gtao");

        var denoiseParameters = denoise.Parameters;
        SetView(denoiseParameters, view, width, height);
        denoiseParameters.Set(PostFxGtaoDenoiseKeys.DenoiseSource, targets.AmbientOcclusionRaw);
        denoiseParameters.Set(PostFxGtaoDenoiseKeys.DenoiseDistance, targets.PyramidLevelViews[0]);
        denoiseParameters.Set(PostFxGtaoDenoiseKeys.DenoiseOutput, targets.AmbientOcclusion);
        denoise.ThreadGroupCounts = Groups(width, height);
        denoise.Draw(context, "PostFx.GtaoDenoise");
    }

    public void DrawContactShadows(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, PostFxContactShadowSettings settings, Vector3 lightDirectionWorld, int steps, int frame)
    {
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        // Towards the light, in the passes' view space (x right, y up, z forward)
        var lightView = Vector3.TransformNormal(lightDirectionWorld, view.View);
        lightView = Vector3.Normalize(new Vector3(lightView.X, lightView.Y, -lightView.Z));

        var parameters = contact.Parameters;
        SetView(parameters, view, width, height);
        parameters.Set(PostFxContactShadowsKeys.ContactDistance, targets.PyramidLevelViews[0]);
        parameters.Set(PostFxContactShadowsKeys.ContactOutput, targets.ContactShadows);
        parameters.Set(PostFxContactShadowsKeys.ContactLightDirection, lightView);
        parameters.Set(PostFxContactShadowsKeys.ContactLength, Math.Max(settings.Length, 0.01f));
        parameters.Set(PostFxContactShadowsKeys.ContactThickness, 0.02f);
        parameters.Set(PostFxContactShadowsKeys.ContactFadeDistance, 60.0f);
        parameters.Set(PostFxContactShadowsKeys.ContactSteps, steps);
        parameters.Set(PostFxContactShadowsKeys.ContactFrame, (float)(frame % 1024));
        contact.ThreadGroupCounts = Groups(width, height);
        contact.Draw(context, "PostFx.ContactShadows");
    }
}

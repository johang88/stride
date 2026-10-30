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
    public const int ReflectionLevels = 5;

    public Texture Pyramid;
    public readonly Texture[] PyramidLevelViews = new Texture[PyramidLevels];
    public Texture AmbientOcclusion;
    public Texture AmbientOcclusionRaw;
    public Texture ContactShadows;

    // Half resolution light found on screen after the frame, read by the next one
    public Texture Reflections;
    public readonly Texture[] ReflectionLevelViews = new Texture[ReflectionLevels];
    public bool ReflectionsValid;
    public Texture IndirectRaw;
    public Texture IndirectNormal;
    // Accumulated light (premultiplied by the share of hits) and, per pixel, the distance it was traced at and the frames
    // it holds
    public readonly Texture[] IndirectHistory = new Texture[2];
    public readonly Texture[] IndirectHistoryInfo = new Texture[2];
    public readonly Texture[] IndirectFiltered = new Texture[2];
    public int IndirectIndex;
    public bool IndirectValid;

    /// <summary>
    /// The accumulated and filtered indirect light of the last frame: rgb = light x share of the rays that found some, a = that share.
    /// </summary>
    public Texture Indirect => IndirectValid ? IndirectFiltered[0] : null;

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

        var halfWidth = Math.Max(1, width / 2);
        var halfHeight = Math.Max(1, height / 2);
        Reflections = Texture.New2D(device, halfWidth, halfHeight, ReflectionLevels, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        Reflections.Name = "PostFxReflections";
        for (int i = 0; i < ReflectionLevels; i++)
            ReflectionLevelViews[i] = Reflections.ToTextureView(ViewType.Single, 0, i);
        IndirectRaw = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        IndirectRaw.Name = "PostFxIndirectRaw";
        IndirectNormal = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        IndirectNormal.Name = "PostFxIndirectNormal";
        for (int i = 0; i < 2; i++)
        {
            IndirectHistory[i] = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            IndirectHistory[i].Name = "PostFxIndirectHistory";
            IndirectHistoryInfo[i] = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            IndirectHistoryInfo[i].Name = "PostFxIndirectHistoryInfo";
            IndirectFiltered[i] = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            IndirectFiltered[i].Name = "PostFxIndirect";
        }
        ReflectionsValid = false;
        IndirectValid = false;
    }

    /// <summary>
    /// Forgets the light found in the previous frames (a camera cut).
    /// </summary>
    public void Restart()
    {
        ReflectionsValid = false;
        IndirectValid = false;
    }

    public void Dispose()
    {
        for (int i = 0; i < PyramidLevels; i++)
        {
            PyramidLevelViews[i]?.Dispose();
            PyramidLevelViews[i] = null;
        }
        for (int i = 0; i < ReflectionLevels; i++)
        {
            ReflectionLevelViews[i]?.Dispose();
            ReflectionLevelViews[i] = null;
        }
        Reflections?.Dispose();
        IndirectRaw?.Dispose();
        IndirectNormal?.Dispose();
        for (int i = 0; i < 2; i++)
        {
            IndirectHistory[i]?.Dispose();
            IndirectHistory[i] = null;
            IndirectHistoryInfo[i]?.Dispose();
            IndirectHistoryInfo[i] = null;
            IndirectFiltered[i]?.Dispose();
            IndirectFiltered[i] = null;
        }
        Reflections = IndirectRaw = IndirectNormal = null;
        ReflectionsValid = IndirectValid = false;
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
    private readonly ComputeEffectShader reflections;
    private readonly ComputeEffectShader mip;
    private readonly ComputeEffectShader indirect;
    private readonly ComputeEffectShader indirectTemporal;
    private readonly ComputeEffectShader indirectFilter;

    public ScreenSpacePass(RenderContext context)
    {
        pyramid = new ComputeEffectShader(context) { ShaderSourceName = "PostFxDepthPyramid", ThreadNumbers = new Int3(8, 8, 1) };
        gtao = new ComputeEffectShader(context) { ShaderSourceName = "PostFxGtao", ThreadNumbers = new Int3(8, 8, 1) };
        denoise = new ComputeEffectShader(context) { ShaderSourceName = "PostFxGtaoDenoise", ThreadNumbers = new Int3(8, 8, 1) };
        contact = new ComputeEffectShader(context) { ShaderSourceName = "PostFxContactShadows", ThreadNumbers = new Int3(8, 8, 1) };
        reflections = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflections", ThreadNumbers = new Int3(8, 8, 1) };
        mip = new ComputeEffectShader(context) { ShaderSourceName = "PostFxScreenMip", ThreadNumbers = new Int3(8, 8, 1) };
        indirect = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirect", ThreadNumbers = new Int3(8, 8, 1) };
        indirectTemporal = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirectTemporal", ThreadNumbers = new Int3(8, 8, 1) };
        indirectFilter = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirectFilter", ThreadNumbers = new Int3(8, 8, 1) };
    }

    /// <summary>
    /// Traces the reflections of the frame (against its anti-aliased image) and their blur levels, for the next frame.
    /// </summary>
    public void DrawReflections(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, Texture scene, int frame)
    {
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        var size = new Int2(targets.Reflections.Width, targets.Reflections.Height);
        var parameters = reflections.Parameters;
        SetView(parameters, view, width, height);
        parameters.Set(PostFxReflectionsKeys.ReflectionDistance, targets.Pyramid);
        parameters.Set(PostFxReflectionsKeys.ReflectionScene, scene);
        parameters.Set(PostFxReflectionsKeys.ReflectionOutput, targets.ReflectionLevelViews[0]);
        parameters.Set(PostFxReflectionsKeys.ReflectionSize, size);
        parameters.Set(PostFxReflectionsKeys.ReflectionMaxDistance, 60.0f);
        parameters.Set(PostFxReflectionsKeys.ReflectionSteps, 32);
        parameters.Set(PostFxReflectionsKeys.ReflectionFrame, (float)(frame % 1024));
        reflections.ThreadGroupCounts = Groups(size.X, size.Y);
        reflections.Draw(context, "PostFx.Reflections");

        for (int level = 1; level < ScreenSpaceTargets.ReflectionLevels; level++)
        {
            var levelSize = new Int2(Math.Max(1, size.X >> level), Math.Max(1, size.Y >> level));
            mip.Parameters.Set(PostFxScreenMipKeys.MipSource, targets.ReflectionLevelViews[level - 1]);
            mip.Parameters.Set(PostFxScreenMipKeys.MipOutput, targets.ReflectionLevelViews[level]);
            mip.Parameters.Set(PostFxScreenMipKeys.MipSize, levelSize);
            mip.ThreadGroupCounts = Groups(levelSize.X, levelSize.Y);
            mip.Draw(context, "PostFx.ReflectionMips");
        }
        targets.ReflectionsValid = true;
    }

    /// <summary>
    /// Traces the indirect light of the frame, accumulates it over frames and filters it, for the next frame.
    /// </summary>
    /// <param name="exposure">Scene light to screen brightness: the light a ray brings is limited on screen.</param>
    public void DrawIndirect(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, Texture scene, Matrix inverseViewProjection, Matrix previousViewProjection, int frame, float exposure)
    {
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        var size = new Int2(targets.IndirectRaw.Width, targets.IndirectRaw.Height);
        var parameters = indirect.Parameters;
        SetView(parameters, view, width, height);
        parameters.Set(PostFxIndirectKeys.IndirectDistance, targets.Pyramid);
        parameters.Set(PostFxIndirectKeys.IndirectScene, scene);
        parameters.Set(PostFxIndirectKeys.IndirectOutput, targets.IndirectRaw);
        parameters.Set(PostFxIndirectKeys.IndirectNormal, targets.IndirectNormal);
        parameters.Set(PostFxIndirectKeys.IndirectSize, size);
        parameters.Set(PostFxIndirectKeys.IndirectMaxDistance, 8.0f);
        parameters.Set(PostFxIndirectKeys.IndirectSteps, 16);
        parameters.Set(PostFxIndirectKeys.IndirectFrame, (float)(frame % 1024));
        parameters.Set(PostFxIndirectKeys.IndirectExposure, exposure);
        parameters.Set(PostFxIndirectKeys.IndirectMaxBrightness, 2.0f);
        indirect.ThreadGroupCounts = Groups(size.X, size.Y);
        indirect.Draw(context, "PostFx.Indirect");

        var previous = targets.IndirectHistory[targets.IndirectIndex];
        var next = targets.IndirectHistory[1 - targets.IndirectIndex];
        var temporal = indirectTemporal.Parameters;
        SetView(temporal, view, width, height);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectCurrent, targets.IndirectRaw);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectHistory, previous);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectHistoryInfo, targets.IndirectHistoryInfo[targets.IndirectIndex]);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectTemporalDistance, targets.PyramidLevelViews[0]);
        // (accumulated into a filter target: the first filter pass writes the history)
        temporal.Set(PostFxIndirectTemporalKeys.IndirectAccumulated, targets.IndirectFiltered[1]);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectAccumulatedInfo, targets.IndirectHistoryInfo[1 - targets.IndirectIndex]);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectMaxFrames, 32.0f);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectTemporalSize, size);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectInverseViewProjection, inverseViewProjection);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectPreviousViewProjection, previousViewProjection);
        temporal.Set(PostFxIndirectTemporalKeys.IndirectHistoryValid, targets.IndirectValid ? 1.0f : 0.0f);
        indirectTemporal.ThreadGroupCounts = Groups(size.X, size.Y);
        indirectTemporal.Draw(context, "PostFx.IndirectTemporal");

        // The edge-aware filter: 4 passes (steps 1 to 8 pixels); the first is the history of the next frame (its noise
        // filtered before it accumulates: SVGF), the last ends in the first filtered target, which the surfaces read
        var filter = indirectFilter.Parameters;
        SetView(filter, view, width, height);
        filter.Set(PostFxIndirectFilterKeys.FilterNormal, targets.IndirectNormal);
        filter.Set(PostFxIndirectFilterKeys.FilterSize, size);
        indirectFilter.ThreadGroupCounts = Groups(size.X, size.Y);
        var source = targets.IndirectFiltered[1];
        for (int pass = 0; pass < 4; pass++)
        {
            var output = pass == 0 ? next : targets.IndirectFiltered[pass & 1 ^ 1];
            filter.Set(PostFxIndirectFilterKeys.FilterSource, source);
            filter.Set(PostFxIndirectFilterKeys.FilterOutput, output);
            filter.Set(PostFxIndirectFilterKeys.FilterStep, 1 << pass);
            indirectFilter.Draw(context, "PostFx.IndirectFilter");
            source = output;
        }
        targets.IndirectIndex = 1 - targets.IndirectIndex;
        targets.IndirectValid = true;
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

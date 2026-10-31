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
    public const int MaxPyramidLevels = 8;
    public const int SourceLevels = 5;

    public Texture Pyramid;
    // (fewer levels for a small view: down to 1x1 at most)
    public int PyramidLevels;
    public readonly Texture[] PyramidLevelViews = new Texture[MaxPyramidLevels];
    public Texture AmbientOcclusion;
    public Texture AmbientOcclusionRaw;
    public Texture ContactShadows;

    // The reflections found on screen after the frame, read by the next one (full resolution, premultiplied by their
    // confidence), and what makes them: the anti-aliased image at half resolution with mips, the rays (one per traced
    // pixel), the rays' length and the accumulated history with, per pixel, the distance it was accumulated at and its frames
    public Texture Reflections;
    public bool ReflectionsValid;
    public Texture ReflectionSource;
    public readonly Texture[] ReflectionSourceViews = new Texture[SourceLevels];
    public Texture ReflectionRays;
    public Texture ReflectionRayLength;
    public readonly Texture[] ReflectionHistory = new Texture[2];
    public readonly Texture[] ReflectionHistoryInfo = new Texture[2];
    public int ReflectionIndex;
    public bool ReflectionHistoryValid;
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
        PyramidLevels = Math.Min(MaxPyramidLevels, (int)Math.Floor(Math.Log2(Math.Max(width, height))) + 1);
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
    /// Creates the targets of the reflections (once the view's are), the rays' for a traced texel per scale x scale pixels.
    /// </summary>
    public void EnsureReflections(GraphicsDevice device, int traceScale)
    {
        var width = Pyramid.Width;
        var height = Pyramid.Height;
        var traceWidth = (width + traceScale - 1) / traceScale;
        var traceHeight = (height + traceScale - 1) / traceScale;
        if (Reflections != null && ReflectionRays.Width == traceWidth && ReflectionRays.Height == traceHeight)
            return;
        DisposeReflections();
        var flags = TextureFlags.ShaderResource | TextureFlags.UnorderedAccess;
        Reflections = Texture.New2D(device, width, height, PixelFormat.R16G16B16A16_Float, flags);
        Reflections.Name = "PostFxReflections";
        var sourceWidth = Math.Max(1, width / 2);
        var sourceHeight = Math.Max(1, height / 2);
        var sourceLevels = Math.Min(SourceLevels, (int)Math.Floor(Math.Log2(Math.Max(sourceWidth, sourceHeight))) + 1);
        ReflectionSource = Texture.New2D(device, sourceWidth, sourceHeight, sourceLevels, PixelFormat.R16G16B16A16_Float, flags);
        ReflectionSource.Name = "PostFxReflectionSource";
        for (int i = 0; i < sourceLevels; i++)
            ReflectionSourceViews[i] = ReflectionSource.ToTextureView(ViewType.Single, 0, i);
        // (hit uvs need more precision than half floats have across a wide view)
        ReflectionRays = Texture.New2D(device, traceWidth, traceHeight, PixelFormat.R32G32B32A32_Float, flags);
        ReflectionRays.Name = "PostFxReflectionRays";
        ReflectionRayLength = Texture.New2D(device, width, height, PixelFormat.R16_Float, flags);
        ReflectionRayLength.Name = "PostFxReflectionRayLength";
        for (int i = 0; i < 2; i++)
        {
            ReflectionHistory[i] = Texture.New2D(device, width, height, PixelFormat.R16G16B16A16_Float, flags);
            ReflectionHistory[i].Name = "PostFxReflectionHistory";
            ReflectionHistoryInfo[i] = Texture.New2D(device, width, height, PixelFormat.R16G16_Float, flags);
            ReflectionHistoryInfo[i].Name = "PostFxReflectionHistoryInfo";
        }
    }

    /// <summary>
    /// Forgets the light found in the previous frames (a camera cut).
    /// </summary>
    public void Restart()
    {
        ReflectionsValid = false;
        ReflectionHistoryValid = false;
        IndirectValid = false;
    }

    public void DisposeReflections()
    {
        for (int i = 0; i < SourceLevels; i++)
        {
            ReflectionSourceViews[i]?.Dispose();
            ReflectionSourceViews[i] = null;
        }
        ReflectionSource?.Dispose();
        Reflections?.Dispose();
        ReflectionRays?.Dispose();
        ReflectionRayLength?.Dispose();
        for (int i = 0; i < 2; i++)
        {
            ReflectionHistory[i]?.Dispose();
            ReflectionHistory[i] = null;
            ReflectionHistoryInfo[i]?.Dispose();
            ReflectionHistoryInfo[i] = null;
        }
        Reflections = ReflectionSource = ReflectionRays = ReflectionRayLength = null;
        ReflectionsValid = ReflectionHistoryValid = false;
    }

    public void Dispose()
    {
        for (int i = 0; i < MaxPyramidLevels; i++)
        {
            PyramidLevelViews[i]?.Dispose();
            PyramidLevelViews[i] = null;
        }
        DisposeReflections();
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
        IndirectRaw = IndirectNormal = null;
        IndirectValid = false;
        Pyramid?.Dispose();
        AmbientOcclusion?.Dispose();
        AmbientOcclusionRaw?.Dispose();
        ContactShadows?.Dispose();
        Pyramid = AmbientOcclusion = AmbientOcclusionRaw = ContactShadows = null;
    }
}

/// <summary>
/// How the reflections are found, per quality.
/// </summary>
internal readonly record struct ReflectionTraceSettings(int TraceScale, int Iterations, int Samples, float MaxRoughness, float MaxDistance);

/// <summary>
/// The view's state the reflections are accumulated with: the frame's matrices and the previous frame's.
/// </summary>
internal readonly record struct ReflectionFrameInfo(int Frame, float Exposure, Matrix InverseViewProjection, Matrix PreviousViewProjection, Vector3 Eye, Vector2 JitterDelta);

/// <summary>
/// The screen-space terms drawn from the depth prepass: the distance pyramid, the ambient occlusion (GTAO, denoised) and
/// the contact shadows; and those found on the frame's image for the next: the reflections and the indirect light.
/// </summary>
internal sealed class ScreenSpacePass
{

    private readonly ComputeEffectShader pyramid;
    private readonly ComputeEffectShader gtao;
    private readonly ComputeEffectShader denoise;
    private readonly ComputeEffectShader contact;
    private readonly ComputeEffectShader reflectionSource;
    private readonly ComputeEffectShader reflectionTrace;
    private readonly ComputeEffectShader reflectionResolve;
    private readonly ComputeEffectShader reflectionTemporal;
    private readonly ComputeEffectShader reflectionFilter;
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
        reflectionSource = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflectionSource", ThreadNumbers = new Int3(8, 8, 1) };
        reflectionTrace = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflectionTrace", ThreadNumbers = new Int3(8, 8, 1) };
        reflectionResolve = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflectionResolve", ThreadNumbers = new Int3(8, 8, 1) };
        reflectionTemporal = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflectionTemporal", ThreadNumbers = new Int3(8, 8, 1) };
        reflectionFilter = new ComputeEffectShader(context) { ShaderSourceName = "PostFxReflectionFilter", ThreadNumbers = new Int3(8, 8, 1) };
        mip = new ComputeEffectShader(context) { ShaderSourceName = "PostFxScreenMip", ThreadNumbers = new Int3(8, 8, 1) };
        indirect = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirect", ThreadNumbers = new Int3(8, 8, 1) };
        indirectTemporal = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirectTemporal", ThreadNumbers = new Int3(8, 8, 1) };
        indirectFilter = new ComputeEffectShader(context) { ShaderSourceName = "PostFxIndirectFilter", ThreadNumbers = new Int3(8, 8, 1) };
    }

    /// <summary>
    /// Finds the reflections of the frame's opaque surfaces on its anti-aliased image, for the next frame: rays sampled from
    /// each surface's specular lobe (its normal and roughness from the opaque pass), traced through the distance pyramid,
    /// resolved per pixel from its neighbours' rays, accumulated over frames and filtered.
    /// </summary>
    /// <param name="scene">The anti-aliased image (the hits' light).</param>
    /// <param name="surfaces">The opaque surfaces' normal and roughness (<see cref="ScreenSpace.PostFxNormalRoughnessTargetSemantic"/>).</param>
    /// <param name="motion">The motion vectors, or null.</param>
    public void DrawReflections(RenderDrawContext context, RenderView view, ScreenSpaceTargets targets, Texture scene, Texture surfaces, Texture motion, Texture black,
        in ReflectionTraceSettings settings, in ReflectionFrameInfo info)
    {
        targets.EnsureReflections(context.GraphicsDevice, settings.TraceScale);
        var width = targets.Pyramid.Width;
        var height = targets.Pyramid.Height;
        var frame = (float)(info.Frame % 1024);
        // World space normals to the passes' view space (z forward)
        var worldToView = view.View;
        worldToView.M13 = -worldToView.M13;
        worldToView.M23 = -worldToView.M23;
        worldToView.M33 = -worldToView.M33;
        worldToView.M43 = -worldToView.M43;

        var source = targets.ReflectionSource;
        var sourceSize = new Int2(source.Width, source.Height);
        var traceSize = new Int2(targets.ReflectionRays.Width, targets.ReflectionRays.Height);
        var traceOffset = settings.TraceScale > 1 ? (info.Frame & 3) switch { 0 => new Int2(0, 0), 1 => new Int2(1, 1), 2 => new Int2(1, 0), _ => new Int2(0, 1) } : Int2.Zero;
        var index = targets.ReflectionIndex;

        // The image the hits read, at half resolution with mips (prefiltered for rough lobes and long rays)
        reflectionSource.Parameters.Set(PostFxReflectionSourceKeys.SourceScene, scene);
        reflectionSource.Parameters.Set(PostFxReflectionSourceKeys.SourceOutput, targets.ReflectionSourceViews[0]);
        reflectionSource.Parameters.Set(PostFxReflectionSourceKeys.SourceSize, sourceSize);
        reflectionSource.Parameters.Set(PostFxReflectionSourceKeys.SourceMaxLuminance, 64.0f / Math.Max(info.Exposure, 1e-6f));
        reflectionSource.ThreadGroupCounts = Groups(sourceSize.X, sourceSize.Y);
        reflectionSource.Draw(context, "PostFx.ReflectionSource");
        for (int level = 1; level < source.MipLevelCount; level++)
        {
            var levelSize = new Int2(Math.Max(1, sourceSize.X >> level), Math.Max(1, sourceSize.Y >> level));
            mip.Parameters.Set(PostFxScreenMipKeys.MipSource, targets.ReflectionSourceViews[level - 1]);
            mip.Parameters.Set(PostFxScreenMipKeys.MipOutput, targets.ReflectionSourceViews[level]);
            mip.Parameters.Set(PostFxScreenMipKeys.MipSize, levelSize);
            mip.ThreadGroupCounts = Groups(levelSize.X, levelSize.Y);
            mip.Draw(context, "PostFx.ReflectionSourceMips");
        }

        // The rays: one per traced pixel (at half resolution, one pixel of each 2x2 in turn)
        var trace = reflectionTrace.Parameters;
        SetView(trace, view, width, height);
        trace.Set(PostFxReflectionTraceKeys.ReflectionPyramid, targets.Pyramid);
        trace.Set(PostFxReflectionTraceKeys.ReflectionGBuffer, surfaces);
        trace.Set(PostFxReflectionTraceKeys.ReflectionRays, targets.ReflectionRays);
        trace.Set(PostFxReflectionTraceKeys.ReflectionTraceSize, traceSize);
        trace.Set(PostFxReflectionTraceKeys.ReflectionTraceScale, settings.TraceScale);
        trace.Set(PostFxReflectionTraceKeys.ReflectionTraceOffset, traceOffset);
        trace.Set(PostFxReflectionTraceKeys.ReflectionWorldToView, worldToView);
        trace.Set(PostFxReflectionTraceKeys.ReflectionMaxRoughness, settings.MaxRoughness);
        trace.Set(PostFxReflectionTraceKeys.ReflectionMaxDistance, settings.MaxDistance);
        trace.Set(PostFxReflectionTraceKeys.ReflectionIterations, settings.Iterations);
        trace.Set(PostFxReflectionTraceKeys.ReflectionMaxLevel, targets.PyramidLevels - 1);
        trace.Set(PostFxReflectionTraceKeys.ReflectionFrame, frame);
        reflectionTrace.ThreadGroupCounts = Groups(traceSize.X, traceSize.Y);
        reflectionTrace.Draw(context, "PostFx.ReflectionTrace");

        // Each pixel from its neighbours' rays
        var resolve = reflectionResolve.Parameters;
        SetView(resolve, view, width, height);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionPyramid, targets.PyramidLevelViews[0]);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionGBuffer, surfaces);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionRays, targets.ReflectionRays);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionScene, scene);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionSource, source);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionResolved, targets.Reflections);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionRayLength, targets.ReflectionRayLength);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionTraceSize, traceSize);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionTraceScale, settings.TraceScale);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionTraceOffset, traceOffset);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionWorldToView, worldToView);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionMaxRoughness, settings.MaxRoughness);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionSamples, settings.Samples);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionExposure, info.Exposure);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionFrame, frame);
        resolve.Set(PostFxReflectionResolveKeys.ReflectionSourceLevels, (float)source.MipLevelCount);
        reflectionResolve.ThreadGroupCounts = Groups(width, height);
        reflectionResolve.Draw(context, "PostFx.ReflectionResolve");

        // Accumulated over frames
        var temporal = reflectionTemporal.Parameters;
        SetView(temporal, view, width, height);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionPyramid, targets.PyramidLevelViews[0]);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionGBuffer, surfaces);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionCurrent, targets.Reflections);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionRayLength, targets.ReflectionRayLength);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionMotion, motion ?? black);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionHasMotion, motion != null);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionHistory, targets.ReflectionHistory[index]);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionHistoryInfo, targets.ReflectionHistoryInfo[index]);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionAccumulated, targets.ReflectionHistory[1 - index]);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionAccumulatedInfo, targets.ReflectionHistoryInfo[1 - index]);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionInverseViewProjection, info.InverseViewProjection);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionPreviousViewProjection, info.PreviousViewProjection);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionEye, info.Eye);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionJitterDelta, info.JitterDelta);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionHistoryValid, targets.ReflectionHistoryValid ? 1.0f : 0.0f);
        temporal.Set(PostFxReflectionTemporalKeys.ReflectionMaxRoughness, settings.MaxRoughness);
        reflectionTemporal.ThreadGroupCounts = Groups(width, height);
        reflectionTemporal.Draw(context, "PostFx.ReflectionTemporal");

        // Filtered into the target the next frame's surfaces read
        var filter = reflectionFilter.Parameters;
        SetView(filter, view, width, height);
        filter.Set(PostFxReflectionFilterKeys.ReflectionPyramid, targets.PyramidLevelViews[0]);
        filter.Set(PostFxReflectionFilterKeys.ReflectionGBuffer, surfaces);
        filter.Set(PostFxReflectionFilterKeys.ReflectionFilterSource, targets.ReflectionHistory[1 - index]);
        filter.Set(PostFxReflectionFilterKeys.ReflectionFilterInfo, targets.ReflectionHistoryInfo[1 - index]);
        filter.Set(PostFxReflectionFilterKeys.ReflectionFilterOutput, targets.Reflections);
        filter.Set(PostFxReflectionFilterKeys.ReflectionWorldToView, worldToView);
        filter.Set(PostFxReflectionFilterKeys.ReflectionFilterFrame, frame);
        reflectionFilter.ThreadGroupCounts = Groups(width, height);
        reflectionFilter.Draw(context, "PostFx.ReflectionFilter");

        targets.ReflectionIndex = 1 - index;
        targets.ReflectionHistoryValid = true;
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
        for (int level = 0; level < targets.PyramidLevels; level++)
        {
            var levelWidth = Math.Max(1, width >> level);
            var levelHeight = Math.Max(1, height >> level);
            var parameters = pyramid.Parameters;
            SetView(parameters, view, width, height);
            parameters.Set(PostFxDepthPyramidKeys.PyramidSource, level == 0 ? depth : targets.PyramidLevelViews[level - 1]);
            parameters.Set(PostFxDepthPyramidKeys.PyramidOutput, targets.PyramidLevelViews[level]);
            parameters.Set(PostFxDepthPyramidKeys.PyramidSize, new Int2(levelWidth, levelHeight));
            parameters.Set(PostFxDepthPyramidKeys.PyramidSourceSize, new Int2(Math.Max(1, width >> Math.Max(level - 1, 0)), Math.Max(1, height >> Math.Max(level - 1, 0))));
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

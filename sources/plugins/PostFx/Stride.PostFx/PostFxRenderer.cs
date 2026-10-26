// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Engine;
using Stride.PostFx.Passes;
using Stride.PostFx.ScreenSpace;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.PostFx;

/// <summary>
/// How the scene's light becomes displayable values.
/// </summary>
public enum PostFxDisplayTransform
{
    /// <summary>
    /// AgX (Troy Sobotka): natural, desaturating gracefully towards white in the highlights.
    /// </summary>
    AgX,

    /// <summary>
    /// AgX with more contrast and saturation.
    /// </summary>
    AgXPunchy,

    /// <summary>
    /// Tony McMapface (Tomasz Stachowiak): neutral, hue preserving, gentle on saturated lights.
    /// </summary>
    TonyMcMapface,

    /// <summary>
    /// No curve: values above 1 are clipped (for debugging).
    /// </summary>
    Clamp,
}

/// <summary>
/// How the exposure is chosen.
/// </summary>
public enum PostFxExposureMode
{
    /// <summary>
    /// The renderer's exposure, the game's offset and the volumes' compensation (an exposure driven by the light of the sky
    /// goes in the offset).
    /// </summary>
    Fixed,

    /// <summary>
    /// Adapted to the scene's luminance (a histogram, center weighted: middle grey at its average), within the volumes'
    /// range; the game's offset and the volumes' compensation are added (not the renderer's exposure).
    /// </summary>
    Histogram,
}

/// <summary>
/// How the image is anti-aliased.
/// </summary>
public enum PostFxAntialiasing
{
    None,

    /// <summary>
    /// Fast approximate anti-aliasing on the final image (edges only).
    /// </summary>
    Fxaa,

    /// <summary>
    /// Temporal anti-aliasing: a sub-pixel jitter each frame, accumulated with the reprojected history (all edges,
    /// shading and foliage; needs motion vectors).
    /// </summary>
    Taa,
}

/// <summary>
/// The resolution the scene is drawn at, upscaled to the output by the temporal anti-aliasing.
/// </summary>
public enum PostFxUpscaling
{
    /// <summary>
    /// The output's resolution.
    /// </summary>
    Native,

    /// <summary>
    /// 77% of the output's resolution on each axis.
    /// </summary>
    Quality,

    /// <summary>
    /// 67% of the output's resolution on each axis.
    /// </summary>
    Balanced,

    /// <summary>
    /// Half the output's resolution on each axis.
    /// </summary>
    Performance,

    /// <summary>
    /// As much of the output's resolution as fits the target frame time (the GPU time of the scene's drawing, shadows to
    /// post-processing).
    /// </summary>
    Dynamic,
}

/// <summary>
/// Quality of the screen-space ambient occlusion.
/// </summary>
public enum PostFxAmbientOcclusionQuality
{
    Off,

    /// <summary>
    /// Two slices of three steps.
    /// </summary>
    Low,

    /// <summary>
    /// Two slices of four steps.
    /// </summary>
    Medium,

    /// <summary>
    /// Three slices of five steps.
    /// </summary>
    High,
}

/// <summary>
/// A buffer of the renderer shown instead of the image.
/// </summary>
public enum PostFxDebugView
{
    None,
    AmbientOcclusion,
    ContactShadows,
    Reflections,
    IndirectLight,
}

/// <summary>
/// The post-processing of a forward renderer: automatic exposure, a physically based glow, grading and the display
/// transform in one final pass, lens effects. The look (exposure compensation, glow, grading, lens) comes from the
/// post-processing volumes of the scene, blended at the camera; these are the renderer's quality settings and the look
/// without volumes.
/// </summary>
[DataContract("PostFxRenderer")]
[Display("Post-processing (PostFx)")]
public sealed class PostFxRenderer : ImageEffect, IPostProcessingEffects, IScreenSpaceLightingEffects, IUpscalingEffects
{
    private const int TimingLatency = 4;
    private readonly QueryPool[] timingPools = new QueryPool[TimingLatency];
    private readonly bool[] timingPending = new bool[TimingLatency];
    private readonly long[] timingData = new long[2];
    private int timingIndex;
    private bool timingOpen;
    private float measuredMilliseconds;
    private int scaleCooldown;
    private const int ViewTimeout = 120;

    private readonly Dictionary<RenderView, ViewState> views = new();
    private readonly List<RenderView> staleViews = new();
    private ImageEffectShader finalShader;
    private ImageEffectShader debugShader;
    private readonly PostFxGradingSettings neutralGrading = new();
    private readonly PostFxLensSettings noLens = new() { Vignette = 0.0f };
    private BloomPass bloom;
    private ExposurePass exposure;
    private FXAAEffect fxaa;
    private TemporalPass temporal;
    private CameraEffectsPass cameraEffects;
    private ImageEffectShader heatHaze;
    private readonly Vector4[] heatShapes = new Vector4[MaxHeatSources];
    private readonly Vector4[] heatParameters = new Vector4[MaxHeatSources];
    private const int MaxHeatSources = 16;
    private ScreenSpacePass screenSpacePass;
    private readonly PostFxScreenSpace screenSpace = new();
    private readonly ContactShadowReceiverExtension contactShadowExtension = new();
    private LightDirectionalShadowMapRenderer shadowMapRenderer;
    private Texture tonyMcMapface;
    private Texture black;
    private int frame;

    [DataMember(-100)]
    [Display(Browsable = false)]
    [NonOverridable]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// How the image is anti-aliased.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(PostFxAntialiasing.Taa)]
    public PostFxAntialiasing Antialiasing { get; set; } = PostFxAntialiasing.Taa;

    /// <summary>
    /// Sharpening of the anti-aliased image (temporal anti-aliasing softens it a little; more is added when upscaling).
    /// </summary>
    [DataMember(15)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Sharpness { get; set; } = 0.3f;

    /// <summary>
    /// The resolution the scene is drawn at (upscaled by the temporal anti-aliasing; native without it).
    /// </summary>
    [DataMember(16)]
    [DefaultValue(PostFxUpscaling.Native)]
    public PostFxUpscaling Upscaling { get; set; } = PostFxUpscaling.Native;

    /// <summary>
    /// GPU time the dynamic resolution aims the scene's drawing at (milliseconds).
    /// </summary>
    [DataMember(17)]
    [DefaultValue(12.0f)]
    [DataMemberRange(1, 100, 0.1, 1, 1)]
    public float TargetFrameTime { get; set; } = 12.0f;

    /// <summary>
    /// Lowest share of the output's resolution the dynamic resolution goes to (on each axis).
    /// </summary>
    [DataMember(18)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0.25, 1, 0.01, 0.1, 2)]
    public float MinimumRenderScale { get; set; } = 0.5f;

    /// <summary>
    /// Share of the output's resolution the scene is drawn at (on each axis).
    /// </summary>
    [DataMemberIgnore]
    public float RenderScale { get; private set; } = 1.0f;

    /// <summary>
    /// GPU time of the scene's drawing measured a few frames ago (milliseconds; 0 until known, dynamic resolution only).
    /// </summary>
    [DataMemberIgnore]
    public float MeasuredFrameTime => measuredMilliseconds;

    /// <summary>
    /// How the scene's light becomes displayable values.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(PostFxDisplayTransform.AgX)]
    public PostFxDisplayTransform DisplayTransform { get; set; } = PostFxDisplayTransform.AgX;

    /// <summary>
    /// How the exposure is chosen.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(PostFxExposureMode.Fixed)]
    public PostFxExposureMode ExposureMode { get; set; } = PostFxExposureMode.Fixed;

    /// <summary>
    /// The exposure (EV): negative darkens.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-16, 16, 0.05, 0.5, 2)]
    public float Exposure { get; set; }

    /// <summary>
    /// Quality of the ambient occlusion (from the depth prepass, on the light from around: the sky, the probes).
    /// </summary>
    [DataMember(45)]
    [DefaultValue(PostFxAmbientOcclusionQuality.Medium)]
    public PostFxAmbientOcclusionQuality AmbientOcclusion { get; set; } = PostFxAmbientOcclusionQuality.Medium;

    /// <summary>
    /// Whether the main directional light casts contact shadows (from the depth prepass).
    /// </summary>
    [DataMember(46)]
    [DefaultValue(true)]
    public bool ContactShadows { get; set; } = true;

    /// <summary>
    /// Whether reflections are found on screen (traced after each frame at half resolution, read by the next): they
    /// replace the sky's where they're found.
    /// </summary>
    [DataMember(47)]
    [DefaultValue(true)]
    public bool Reflections { get; set; } = true;

    /// <summary>
    /// Whether indirect light is found on screen (a ray per pixel at half resolution, accumulated over frames): light
    /// bouncing off nearby surfaces replaces the sky's where it's found.
    /// </summary>
    [DataMember(48)]
    [DefaultValue(true)]
    public bool IndirectLight { get; set; } = true;

    /// <summary>
    /// Whether the lens blurs away from its focus (where the volumes give it an amount).
    /// </summary>
    [DataMember(49)]
    [DefaultValue(true)]
    public bool DepthOfField { get; set; } = true;

    /// <summary>
    /// Whether the image blurs with the motion over the shutter (camera and objects).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(true)]
    public bool MotionBlur { get; set; } = true;

    /// <summary>
    /// Samples along the motion per pixel.
    /// </summary>
    [DataMember(51)]
    [DefaultValue(12)]
    [DataMemberRange(4, 32, 1, 4, 0)]
    public int MotionBlurSamples { get; set; } = 12;

    /// <summary>
    /// Whether hot air shimmers (heat sources, hot ground).
    /// </summary>
    [DataMember(52)]
    [DefaultValue(true)]
    public bool HeatHaze { get; set; } = true;

    /// <summary>
    /// Whether the lens shows itself: vignetting, chromatic aberration, film grain, lens dirt, the anamorphic streak.
    /// </summary>
    [DataMember(53)]
    [DefaultValue(true)]
    public bool LensEffects { get; set; } = true;

    /// <summary>
    /// Whether the bright light glows.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(true)]
    public bool Bloom { get; set; } = true;

    /// <summary>
    /// Whether the final image is dithered (no banding in smooth gradients such as the sky).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(true)]
    public bool Dither { get; set; } = true;

    /// <summary>
    /// The look where no volume overrides it.
    /// </summary>
    [DataMember(100)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxProfile Defaults { get; set; } = new();

    /// <summary>
    /// Exposure added by the game (EV, not saved): an exposure following the light of the sky, menus dimming the scene.
    /// </summary>
    [DataMemberIgnore]
    public float ExposureOffset { get; set; }

    /// <summary>
    /// The player's brightness: exposure added to all (EV, not saved).
    /// </summary>
    [DataMemberIgnore]
    public float UserExposure { get; set; }

    /// <summary>
    /// The player's contrast, multiplying the grading's (not saved).
    /// </summary>
    [DataMemberIgnore]
    public float UserContrast { get; set; } = 1.0f;

    /// <summary>
    /// The player's saturation, multiplying the grading's (not saved).
    /// </summary>
    [DataMemberIgnore]
    public float UserSaturation { get; set; } = 1.0f;

    /// <summary>
    /// Whether the volumes' color grading applies (white balance, contrast, saturation, tints, look tables; not saved).
    /// </summary>
    [DataMemberIgnore]
    public bool ColorGrading { get; set; } = true;

    /// <summary>
    /// A buffer shown instead of the image (not saved).
    /// </summary>
    [DataMemberIgnore]
    public PostFxDebugView DebugView { get; set; }

    /// <summary>
    /// The look of the last view drawn (the volumes blended at its camera).
    /// </summary>
    [DataMemberIgnore]
    public PostFxProfile Current { get; private set; }

    public bool RequiresVelocityBuffer => Antialiasing == PostFxAntialiasing.Taa || MotionBlur;

    public bool RequiresNormalBuffer => false;

    public bool RequiresSpecularRoughnessBuffer => false;

    /// <inheritdoc/>
    [DataMemberIgnore]
    public RenderStage OpaqueRenderStage
    {
        get => screenSpace.OpaqueStage;
        set => screenSpace.OpaqueStage = value;
    }

    protected override void InitializeCore()
    {
        base.InitializeCore();
        finalShader = ToLoadAndUnload(new ImageEffectShader("PostFxFinal"));
        debugShader = ToLoadAndUnload(new ImageEffectShader("PostFxDebugView"));
        bloom = new BloomPass(ToLoadAndUnload(new ImageEffectShader("PostFxBloomDownsample")), ToLoadAndUnload(new ImageEffectShader("PostFxBloomUpsample")), ToLoadAndUnload(new ImageEffectShader("PostFxStreak")));
        exposure = new ExposurePass(new ComputeEffectShader(Context) { ShaderSourceName = "PostFxHistogram" }, new ComputeEffectShader(Context) { ShaderSourceName = "PostFxExposureAdapt" });
        exposure.Initialize(GraphicsDevice);
        fxaa = ToLoadAndUnload(new FXAAEffect { InputLuminanceInAlpha = true });
        temporal = new TemporalPass(ToLoadAndUnload(new ImageEffectShader("PostFxTemporal")));
        heatHaze = ToLoadAndUnload(new ImageEffectShader("PostFxHeatHaze"));
        cameraEffects = new CameraEffectsPass(Context, ToLoadAndUnload(new ImageEffectShader("PostFxMotionBlur")), ToLoadAndUnload(new ImageEffectShader("PostFxDepthOfFieldComposite")));
        screenSpacePass = new ScreenSpacePass(Context);
        contactShadowExtension.ScreenSpace = screenSpace;
        tonyMcMapface = LoadTonyMcMapface(GraphicsDevice);
        black = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new byte[] { 0, 0, 0, 0 });
    }

    protected override void Destroy()
    {
        foreach (var state in views.Values)
            state.Dispose();
        views.Clear();
        shadowMapRenderer?.RemoveReceiverExtension(contactShadowExtension);
        for (int i = 0; i < TimingLatency; i++)
        {
            timingPools[i]?.Dispose();
            timingPools[i] = null;
        }
        exposure?.Dispose();
        tonyMcMapface?.Dispose();
        black?.Dispose();
        base.Destroy();
    }

    /// <inheritdoc/>
    public float UpdateRenderScale(RenderContext context)
    {
        if (Antialiasing != PostFxAntialiasing.Taa)
            return RenderScale = 1.0f;
        switch (Upscaling)
        {
            case PostFxUpscaling.Quality:
                return RenderScale = 1.0f / 1.3f;
            case PostFxUpscaling.Balanced:
                return RenderScale = 1.0f / 1.5f;
            case PostFxUpscaling.Performance:
                return RenderScale = 0.5f;
            case PostFxUpscaling.Dynamic:
                break;
            default:
                return RenderScale = 1.0f;
        }

        // The cost follows the pixels drawn: the scale towards the target by the square root of the times' ratio, in steps
        // of 5% (each change reallocates the view's targets), waiting for the measurements of a change to arrive
        var minimum = Math.Clamp(MinimumRenderScale, 0.25f, 1.0f);
        if (scaleCooldown > 0)
        {
            scaleCooldown--;
        }
        else if (measuredMilliseconds > 0.0f)
        {
            var desired = Math.Clamp(RenderScale * MathF.Sqrt(TargetFrameTime / measuredMilliseconds), minimum, 1.0f);
            // (down at once when over the target, up only with room to spare)
            var change = desired < RenderScale ? desired - RenderScale : desired - RenderScale - 0.03f;
            if (MathF.Abs(change) >= 0.05f)
            {
                RenderScale = MathF.Round((RenderScale + change) * 20.0f) / 20.0f;
                scaleCooldown = TimingLatency + 4;
            }
        }
        return RenderScale = Math.Clamp(RenderScale, minimum, 1.0f);
    }

    /// <inheritdoc/>
    public void BeginDraw(RenderDrawContext context)
    {
        if (Upscaling != PostFxUpscaling.Dynamic || Antialiasing != PostFxAntialiasing.Taa)
            return;
        // The oldest measurement, then its pool for this frame
        timingIndex = (timingIndex + 1) % TimingLatency;
        var pool = timingPools[timingIndex] ??= QueryPool.New(context.GraphicsDevice, QueryType.Timestamp, 2);
        if (timingPending[timingIndex] && pool.TryGetData(timingData))
        {
            var frequency = context.GraphicsDevice.TimestampFrequency;
            if (frequency > 0 && timingData[1] > timingData[0])
                measuredMilliseconds = (float)((timingData[1] - timingData[0]) * 1000.0 / frequency);
        }
        timingPending[timingIndex] = false;
        context.CommandList.WriteTimestamp(pool, 0);
        timingOpen = true;
    }

    private void EndTiming(RenderDrawContext context)
    {
        if (!timingOpen)
            return;
        timingOpen = false;
        context.CommandList.WriteTimestamp(timingPools[timingIndex], 1);
        timingPending[timingIndex] = true;
    }

    public void Collect(RenderContext context)
    {
        if (!Initialized)
            Initialize(context);
        var view = context.RenderView;
        if (view == null)
            return;
        var state = GetState(view, context.RenderSystem.FrameCounter);
        // (once a frame: the jitter and the matrices kept for the next frame)
        if (state.CollectedFrame == context.RenderSystem.FrameCounter)
            return;
        state.CollectedFrame = context.RenderSystem.FrameCounter;
        Resolve(context, view, state);

        // The screen-space lighting of the opaque surfaces
        screenSpace.Enabled = AmbientOcclusion != PostFxAmbientOcclusionQuality.Off || ContactShadows || Reflections || IndirectLight;
        if (screenSpace.Enabled)
            EnsureScreenSpaceFeatures(context);

        // Temporal anti-aliasing: the frame rendered with a sub-pixel offset (after the camera set the view's matrices,
        // before anything uploads them), the meshes writing their motion
        // (motion blur reads the motion, and the matrices kept with the history)
        if (Antialiasing == PostFxAntialiasing.Taa || MotionBlur)
        {
            EnsureMotionVectors(context);
            state.Temporal ??= new TemporalHistory();
            // (more phases when upscaling: an output pixel sees fewer samples a frame)
            state.Temporal.JitterCount = Math.Clamp((int)MathF.Ceiling(8.0f / (RenderScale * RenderScale)), 8, 32);
            state.Temporal.ApplyJitter(view, Antialiasing == PostFxAntialiasing.Taa);
        }

        // A cut: nothing on screen came from the previous frame's view
        if (context.Services.GetService<PostFxVolumes>()?.IsCut(context.RenderSystem.FrameCounter) == true)
        {
            state.Temporal?.Restart();
            state.ScreenSpaceTargets?.Restart();
        }

        PublishScreenSpace(context, view, state);
    }

    public void Draw(RenderDrawContext drawContext, RenderOutputValidator outputValidator, Span<Texture> inputs, Texture inputDepthStencil, Texture outputTarget)
    {
        var colorIndex = outputValidator.Find<ColorTargetSemantic>();
        if (colorIndex < 0)
            return;
        SetInput(0, inputs[colorIndex]);
        SetInput(1, inputDepthStencil);
        var velocityIndex = outputValidator.Find<VelocityTargetSemantic>();
        SetInput(2, velocityIndex >= 0 ? inputs[velocityIndex] : null);
        SetOutput(outputTarget);
        Draw(drawContext);
    }

    protected override void DrawCore(RenderDrawContext context)
    {
        var input = GetInput(0);
        var output = GetOutput(0);
        if (input == null || output == null)
            return;

        var renderContext = context.RenderContext;
        var view = renderContext.RenderView;
        var state = GetState(view, renderContext.RenderSystem.FrameCounter);
        if (state.ResolvedFrame != renderContext.RenderSystem.FrameCounter)
            Resolve(renderContext, view, state);
        var profile = state.Profile;
        Current = profile;
        frame++;

        var elapsed = (float)(renderContext.Time?.Elapsed.TotalSeconds ?? 0.0);
        var autoExposure = exposure.Unit;
        if (ExposureMode == PostFxExposureMode.Histogram)
        {
            state.Exposure ??= ExposurePass.CreateState(GraphicsDevice);
            exposure.Draw(context, input, state.Exposure, profile.Exposure, elapsed);
            autoExposure = state.Exposure;
        }

        // The anti-aliased scene feeds the rest
        var scene = input;
        var depth = GetInput(1);
        var motion = GetInput(2);
        var temporalResolved = false;
        if (Antialiasing == PostFxAntialiasing.Taa && state.Temporal != null && depth != null && motion != null)
        {
            var exposureScale = MathF.Pow(2.0f, ExposureEV(profile));
            scene = temporal.Draw(context, state.Temporal, input, depth, motion, autoExposure, exposureScale, 0.08f, output.ViewWidth, output.ViewHeight);
            temporalResolved = true;
        }

        // Light found on the anti-aliased image, for the next frame's surfaces
        if (state.ScreenSpaceTargets?.Pyramid != null && renderContext.RenderView.Projection.M44 != 1.0f)
        {
            var targets = state.ScreenSpaceTargets;
            // (the image just resolved is read)
            context.CommandList.ResetTargets();
            if (Reflections && profile.ScreenSpaceLight.Reflections > 0.0f)
                screenSpacePass.DrawReflections(context, renderContext.RenderView, targets, scene, frame);
            else
                targets.ReflectionsValid = false;
            if (IndirectLight && profile.ScreenSpaceLight.IndirectLight > 0.0f)
            {
                var viewProjection = state.Temporal?.ViewProjection ?? renderContext.RenderView.ViewProjection;
                Matrix.Invert(ref viewProjection, out var inverseViewProjection);
                var previous = state.Temporal?.HasPrevious == true ? state.Temporal.PreviousUnjitteredViewProjection : viewProjection;
                screenSpacePass.DrawIndirect(context, renderContext.RenderView, targets, scene, inverseViewProjection, previous, frame);
            }
            else
            {
                targets.IndirectValid = false;
            }
        }

        // Hot air bending what is seen through it (after the temporal accumulation, which would smooth it away)
        if (HeatHaze && depth != null && renderContext.RenderView.Projection.M44 != 1.0f)
        {
            var hazed = DrawHeatHaze(context, renderContext, state, profile, scene, depth);
            if (hazed != null)
                scene = hazed;
        }

        // The camera: depth of field, then motion blur
        var perspective = renderContext.RenderView.Projection.M44 != 1.0f;
        var focusing = DepthOfField && perspective && depth != null && CameraEffectsPass.HasDepthOfField(renderContext.RenderView, profile.DepthOfField, scene.ViewHeight);
        var blurring = MotionBlur && perspective && depth != null && motion != null && state.Temporal is { HasPrevious: true } history && profile.MotionBlur.Shutter > 0.0f
            && Vector3.Distance(history.Eye, history.PreviousEye) < 50.0f;
        if (focusing || blurring)
        {
            state.CameraTargets ??= new CameraEffectsTargets();
            state.CameraTargets.Ensure(GraphicsDevice, scene.ViewWidth, scene.ViewHeight);
            if (focusing)
            {
                context.CommandList.ResetTargets();
                var focused = NewScopedRenderTarget2D(scene.ViewWidth, scene.ViewHeight, scene.ViewFormat);
                cameraEffects.DrawDepthOfField(context, renderContext.RenderView, state.CameraTargets, profile.DepthOfField, scene, depth, focused, frame);
                scene = focused;
            }
            if (blurring)
            {
                context.CommandList.ResetTargets();
                var blurred = NewScopedRenderTarget2D(scene.ViewWidth, scene.ViewHeight, scene.ViewFormat);
                cameraEffects.DrawMotionBlur(context, renderContext.RenderView, state.CameraTargets, state.Temporal, profile.MotionBlur, scene, depth, motion, blurred, MotionBlurSamples, frame);
                scene = blurred;
            }
        }

        Texture glow = null;
        Texture streak = null;
        if (Bloom && (profile.Bloom.Intensity > 0.0f || profile.Bloom.LensDirtIntensity > 0.0f))
        {
            // (white: the scene luminance the exposure shows as 1; the automatic exposure's part isn't known here, its
            // middle is assumed)
            var white = MathF.Pow(2.0f, -ExposureEV(profile)) * (ExposureMode == PostFxExposureMode.Histogram ? 0.18f * MathF.Pow(2.0f, 3.0f) : 1.0f);
            (glow, streak) = bloom.Draw(context, (w, h, f) => NewScopedRenderTarget2D(w, h, f), scene, profile.Bloom.Scatter, LensEffects && profile.Bloom.AnamorphicIntensity > 0.0f, white);
        }

        var antialias = Antialiasing == PostFxAntialiasing.Fxaa;
        var target = antialias ? NewScopedRenderTarget2D(output.ViewWidth, output.ViewHeight, output.ViewFormat) : output;
        DrawFinal(context, scene, glow, streak, autoExposure, profile, target, antialias, temporalResolved ? Math.Min(Sharpness + 0.4f * (1.0f - RenderScale), 1.0f) : 0.0f);
        EndTiming(context);

        if (antialias)
        {
            fxaa.SetInput(0, target);
            fxaa.SetOutput(output);
            fxaa.Draw(context);
        }

        var debugTexture = DebugView switch
        {
            PostFxDebugView.AmbientOcclusion => state.ScreenSpaceTargets?.AmbientOcclusion,
            PostFxDebugView.ContactShadows => state.ScreenSpaceTargets?.ContactShadows,
            PostFxDebugView.Reflections => state.ScreenSpaceTargets?.ReflectionLevelViews[0],
            PostFxDebugView.IndirectLight => state.ScreenSpaceTargets?.Indirect,
            _ => null,
        };
        if (debugTexture != null)
        {
            debugShader.Parameters.Set(PostFxDebugViewKeys.DebugColor, DebugView is PostFxDebugView.Reflections or PostFxDebugView.IndirectLight);
            debugShader.Parameters.Set(PostFxDebugViewKeys.DebugScale, MathF.Pow(2.0f, ExposureEV(profile)));
            debugShader.SetInput(0, debugTexture);
            debugShader.SetOutput(output);
            debugShader.Draw(context, "PostFx.DebugView");
        }
    }

    private void DrawFinal(RenderDrawContext context, Texture scene, Texture glow, Texture streak, Buffer autoExposure, PostFxProfile profile, Texture target, bool lumaInAlpha, float sharpness)
    {
        var parameters = finalShader.Parameters;
        parameters.Set(PostFxFinalKeys.PostFxSharpness, Math.Clamp(sharpness, 0.0f, 1.0f));
        parameters.Set(PostFxFinalKeys.PostFxSceneTexel, new Vector2(1.0f / scene.ViewWidth, 1.0f / scene.ViewHeight));
        parameters.Set(PostFxFinalKeys.PostFxExposure, MathF.Pow(2.0f, ExposureEV(profile)));
        parameters.Set(PostFxFinalKeys.PostFxAutoExposure, autoExposure);
        parameters.Set(PostFxFinalKeys.PostFxTonyMcMapface, tonyMcMapface);

        var bloomSettings = profile.Bloom;
        parameters.Set(PostFxFinalKeys.PostFxBloomIntensity, glow != null ? Math.Clamp(bloomSettings.Intensity, 0.0f, 1.0f) : 0.0f);
        parameters.Set(PostFxFinalKeys.PostFxBloomTint, (Vector3)bloomSettings.Tint);
        parameters.Set(PostFxFinalKeys.PostFxStreakIntensity, streak != null ? Math.Clamp(bloomSettings.AnamorphicIntensity, 0.0f, 1.0f) : 0.0f);
        parameters.Set(PostFxFinalKeys.PostFxStreakTint, (Vector3)bloomSettings.AnamorphicTint);
        parameters.Set(PostFxFinalKeys.PostFxLensDirtIntensity, LensEffects && glow != null && bloomSettings.LensDirt != null ? bloomSettings.LensDirtIntensity : 0.0f);

        // (without the color grading: neutral, the player's contrast and saturation only)
        var grading = ColorGrading ? profile.Grading : neutralGrading;
        var whiteBalance = WhiteBalance(grading.Temperature, grading.Tint);
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance0, new Vector4(whiteBalance.M11, whiteBalance.M12, whiteBalance.M13, 0));
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance1, new Vector4(whiteBalance.M21, whiteBalance.M22, whiteBalance.M23, 0));
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance2, new Vector4(whiteBalance.M31, whiteBalance.M32, whiteBalance.M33, 0));
        parameters.Set(PostFxFinalKeys.PostFxFilter, (Vector3)grading.Filter);
        parameters.Set(PostFxFinalKeys.PostFxContrast, Math.Max(grading.Contrast * UserContrast, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxSaturation, Math.Max(grading.Saturation * UserSaturation, 0.0f));
        var lut0 = grading.BlendedWeight0 > 0.001f ? grading.BlendedLut0 : null;
        var lut1 = grading.BlendedWeight1 > 0.001f ? grading.BlendedLut1 : null;
        parameters.Set(PostFxFinalKeys.PostFxLutWeights, new Vector2(lut0 != null ? grading.BlendedWeight0 : 0.0f, lut1 != null ? grading.BlendedWeight1 : 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxLutSizes, new Vector2(lut0?.Height ?? 1, lut1?.Height ?? 1));
        parameters.Set(PostFxFinalKeys.PostFxShadows, (Vector3)grading.Shadows);
        parameters.Set(PostFxFinalKeys.PostFxHighlights, (Vector3)grading.Highlights);

        var lens = LensEffects ? profile.Lens : noLens;
        parameters.Set(PostFxFinalKeys.PostFxVignette, Math.Max(lens.Vignette, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxAspect, target.ViewWidth / (float)Math.Max(target.ViewHeight, 1));
        parameters.Set(PostFxFinalKeys.PostFxChromaticAberration, Math.Max(lens.ChromaticAberration, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxGrain, Math.Max(lens.FilmGrain, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxGrainResponse, Math.Clamp(lens.FilmGrainResponse, 0.0f, 1.0f));
        parameters.Set(PostFxFinalKeys.PostFxFrame, (float)(frame % 1024));
        parameters.Set(PostFxFinalKeys.PostFxDisplayTransform, (int)DisplayTransform);
        parameters.Set(PostFxFinalKeys.PostFxTargetSrgb, target.ViewFormat.IsSRgb);
        parameters.Set(PostFxFinalKeys.PostFxDither, Dither);
        parameters.Set(PostFxFinalKeys.PostFxLumaInAlpha, lumaInAlpha);

        finalShader.SetInput(0, scene);
        finalShader.SetInput(1, glow ?? black);
        finalShader.SetInput(2, bloomSettings.LensDirt ?? black);
        finalShader.SetInput(3, streak ?? black);
        finalShader.SetInput(4, lut0 ?? black);
        finalShader.SetInput(5, lut1 ?? black);
        finalShader.SetOutput(target);
        finalShader.Draw(context, "PostFx.Final");
    }

    /// <summary>
    /// The exposure besides the automatic one (EV): the renderer's (with a fixed exposure only), the game's offset and the
    /// volumes' compensation.
    /// </summary>
    private float ExposureEV(PostFxProfile profile)
    {
        var fixedExposure = ExposureMode == PostFxExposureMode.Fixed ? Exposure : 0.0f;
        return fixedExposure + ExposureOffset + UserExposure + profile.Exposure.Compensation;
    }

    private ViewState GetState(RenderView view, int frameCounter)
    {
        staleViews.Clear();
        foreach (var (other, otherState) in views)
        {
            if (frameCounter - otherState.LastFrame > ViewTimeout)
                staleViews.Add(other);
        }
        foreach (var stale in staleViews)
        {
            views[stale].Dispose();
            views.Remove(stale);
        }

        if (!views.TryGetValue(view, out var state))
            views.Add(view, state = new ViewState());
        state.LastFrame = frameCounter;
        return state;
    }

    /// <summary>
    /// The heat haze of the nearest heat sources and the hot ground, or null where nothing shimmers.
    /// </summary>
    private Texture DrawHeatHaze(RenderDrawContext context, RenderContext renderContext, ViewState state, PostFxProfile profile, Texture scene, Texture depth)
    {
        var view = renderContext.RenderView;
        Matrix.Invert(ref view.View, out var viewInverse);
        var eye = viewInverse.TranslationVector;
        var count = 0;
        if (renderContext.Services.GetService<PostFxVolumes>() is { } volumes)
        {
            // The nearest that can be seen (by the distance to their columns)
            foreach (var source in volumes.HeatSources.OrderBy(x => x.Entity == null ? float.MaxValue : Vector3.DistanceSquared(x.Entity.Transform.WorldMatrix.TranslationVector, eye)))
            {
                if (count == MaxHeatSources)
                    break;
                if (!source.Enabled || source.Strength <= 0.0f || source.Entity?.Scene == null)
                    continue;
                var position = source.Entity.Transform.WorldMatrix.TranslationVector;
                if (Vector3.Distance(position, eye) > 150.0f + source.Height)
                    break;
                heatShapes[count] = new Vector4(position, source.Radius);
                heatParameters[count] = new Vector4(source.Height, source.Strength, source.RiseSpeed, 0.0f);
                count++;
            }
        }
        var ground = profile.HeatHaze.GroundShimmer;
        if (count == 0 && ground <= 0.0f)
            return null;

        var viewProjection = state.Temporal?.UnjitteredViewProjection is { } unjittered && unjittered != default ? unjittered : view.ViewProjection;
        Matrix.Invert(ref viewProjection, out var inverseViewProjection);
        var parameters = heatHaze.Parameters;
        parameters.Set(PostFxHeatHazeKeys.HeatInverseViewProjection, inverseViewProjection);
        parameters.Set(PostFxHeatHazeKeys.HeatCameraPosition, eye);
        parameters.Set(PostFxHeatHazeKeys.HeatTime, (float)((renderContext.Time?.Total.TotalSeconds ?? 0.0) % 3600.0));
        parameters.Set(PostFxHeatHazeKeys.HeatSourceShapes, heatShapes);
        parameters.Set(PostFxHeatHazeKeys.HeatSourceParameters, heatParameters);
        parameters.Set(PostFxHeatHazeKeys.HeatSourceCount, count);
        parameters.Set(PostFxHeatHazeKeys.HeatGroundShimmer, ground);
        parameters.Set(PostFxHeatHazeKeys.HeatGroundDistance, Math.Max(profile.HeatHaze.GroundDistance, 1.0f));
        // (about 4 pixels at 1080p for a campfire)
        var pixels = 4.0f * scene.ViewHeight / 1080.0f;
        parameters.Set(PostFxHeatHazeKeys.HeatDisplacement, new Vector2(pixels / scene.ViewWidth, pixels / scene.ViewHeight));
        var output = NewScopedRenderTarget2D(scene.ViewWidth, scene.ViewHeight, scene.ViewFormat);
        heatHaze.SetInput(0, scene);
        heatHaze.SetInput(1, depth);
        heatHaze.SetOutput(output);
        heatHaze.Draw(context, "PostFx.HeatHaze");
        return output;
    }

    private void Resolve(RenderContext context, RenderView view, ViewState state)
    {
        state.ResolvedFrame = context.RenderSystem.FrameCounter;
        state.Profile.CopyFrom(Defaults);
        if (context.Services.GetService<PostFxVolumes>() is { } volumes)
        {
            Matrix.Invert(ref view.View, out var viewInverse);
            volumes.Resolve(viewInverse.TranslationVector, state.Profile);
        }
    }

    /// <summary>
    /// White balance in linear sRGB: a von Kries adaptation in LMS from the white of a temperature and tint offset to D65
    /// (temperature and tint in -1..1).
    /// </summary>
    private static Matrix WhiteBalance(float temperature, float tint)
    {
        var t1 = temperature * 100.0f / 60.0f;
        var t2 = tint * 100.0f / 60.0f;
        // The chromaticity of the white: along the Planckian locus by the temperature, off it by the tint
        var x = 0.31271f - t1 * (t1 < 0.0f ? 0.1f : 0.05f);
        var y = 2.87f * x - 3.0f * x * x - 0.27509507f + t2 * 0.05f;
        var X = x / y;
        var Z = (1.0f - x - y) / y;
        var lms = new Vector3(0.7328f * X + 0.4296f - 0.1624f * Z, -0.7036f * X + 1.6975f + 0.0061f * Z, 0.0030f * X + 0.0136f + 0.9834f * Z);
        var d65 = new Vector3(0.949237f, 1.03542f, 1.08728f);
        var scale = d65 / lms;

        // Linear sRGB to LMS and back (rows)
        var toLms = new Matrix(
            0.390405f, 0.549941f, 0.00892632f, 0,
            0.0708416f, 0.963172f, 0.00135775f, 0,
            0.0231082f, 0.128021f, 0.936245f, 0,
            0, 0, 0, 1);
        var fromLms = new Matrix(
            2.85847f, -1.62879f, -0.024891f, 0,
            -0.210182f, 1.1582f, 0.000324281f, 0,
            -0.041812f, -0.118169f, 1.06867f, 0,
            0, 0, 0, 1);
        // (rows are output channels: fromLms * diag(scale) * toLms)
        var result = new Matrix();
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                float sum = 0.0f;
                for (int k = 0; k < 3; k++)
                    sum += fromLms[r, k] * scale[k] * toLms[k, c];
                result[r, c] = sum;
            }
        }
        result.M44 = 1.0f;
        return result;
    }

    /// <summary>
    /// Decides the screen-space terms of a view and publishes them to its opaque surfaces (at collect: the surfaces'
    /// parameters are prepared before the depth prepass draws them).
    /// </summary>
    private void PublishScreenSpace(RenderContext renderContext, RenderView view, ViewState state)
    {
        if (!screenSpace.Views.TryGetValue(view, out var data))
            screenSpace.Views.Add(view, data = new ScreenSpaceViewData());
        // (orthographic views have no distances to march in)
        if (!screenSpace.Enabled || view.Projection.M44 == 1.0f || view.ViewSize.X < 1.0f || view.ViewSize.Y < 1.0f)
        {
            data.Frame = -1;
            return;
        }

        var profile = state.Profile;
        state.ScreenSpaceTargets ??= new ScreenSpaceTargets();
        var targets = state.ScreenSpaceTargets;
        targets.Ensure(GraphicsDevice, (int)view.ViewSize.X, (int)view.ViewSize.Y);

        data.Frame = renderContext.RenderSystem.FrameCounter;
        data.ScreenSize = new Vector4(targets.Pyramid.Width, targets.Pyramid.Height, 1.0f / targets.Pyramid.Width, 1.0f / targets.Pyramid.Height);
        data.PreviousViewProjection = state.Temporal?.HasPrevious == true ? state.Temporal.PreviousUnjitteredViewProjection : view.ViewProjection;

        var occlusion = profile.AmbientOcclusion;
        var occlusionOn = AmbientOcclusion != PostFxAmbientOcclusionQuality.Off && occlusion.Intensity > 0.0f;
        data.AmbientOcclusion = occlusionOn ? targets.AmbientOcclusion : null;
        data.AmbientOcclusionStrength = Math.Clamp(occlusion.Intensity, 0.0f, 2.0f);

        // The light found on screen after the last frame
        var found = profile.ScreenSpaceLight;
        data.Reflections = Reflections && found.Reflections > 0.0f && targets.ReflectionsValid ? targets.Reflections : null;
        data.ReflectionStrength = Math.Clamp(found.Reflections, 0.0f, 1.0f);
        data.Indirect = IndirectLight && found.IndirectLight > 0.0f ? targets.Indirect : null;
        data.IndirectStrength = Math.Clamp(found.IndirectLight, 0.0f, 1.0f);

        var contact = profile.ContactShadows;
        data.ContactLight = ContactShadows && contact.Intensity > 0.0f ? FindMainLight(renderContext) : null;
        data.ContactShadows = data.ContactLight != null ? targets.ContactShadows : null;
        data.ContactShadowStrength = Math.Clamp(contact.Intensity, 0.0f, 1.0f);
    }

    /// <inheritdoc/>
    public void DrawAfterDepthPrepass(RenderDrawContext context, Texture depthStencil)
    {
        var renderContext = context.RenderContext;
        var view = renderContext.RenderView;
        if (view == null || depthStencil == null || !screenSpace.Views.TryGetValue(view, out var data) || data.Frame != renderContext.RenderSystem.FrameCounter)
            return;
        var state = GetState(view, data.Frame);
        var targets = state.ScreenSpaceTargets;
        // (a depth of another size than the view's: nothing to match it with this frame)
        if (targets?.Pyramid == null || targets.Pyramid.Width != depthStencil.ViewWidth || targets.Pyramid.Height != depthStencil.ViewHeight)
        {
            data.Frame = -1;
            return;
        }
        var profile = state.Profile;

        screenSpacePass.DrawPyramid(context, view, targets, depthStencil);
        if (data.AmbientOcclusion != null)
        {
            var (slices, steps) = AmbientOcclusion switch
            {
                PostFxAmbientOcclusionQuality.Low => (2, 3),
                PostFxAmbientOcclusionQuality.Medium => (2, 4),
                _ => (3, 5),
            };
            screenSpacePass.DrawAmbientOcclusion(context, view, targets, profile.AmbientOcclusion, slices, steps, frame);
        }
        if (data.ContactLight != null)
            screenSpacePass.DrawContactShadows(context, view, targets, profile.ContactShadows, -Vector3.Normalize(data.ContactLight.Direction), 16, frame);
    }

    /// <summary>
    /// The brightest directional light with shadows: the one contact shadows are traced for.
    /// </summary>
    private static RenderLight FindMainLight(RenderContext context)
    {
        var lights = SceneInstance.GetCurrent(context)?.GetProcessor<LightProcessor>()?.Lights;
        if (lights == null)
            return null;
        RenderLight best = null;
        var bestLuminance = 0.0f;
        foreach (var light in lights)
        {
            if (light.Type is not LightDirectional { Shadow.Enabled: true })
                continue;
            var color = light.Color;
            var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
            if (luminance > bestLuminance)
            {
                bestLuminance = luminance;
                best = light;
            }
        }
        return best;
    }

    /// <summary>
    /// Adds the screen-space lighting of the opaque surfaces and the contact shadows to the render system.
    /// </summary>
    private void EnsureScreenSpaceFeatures(RenderContext context)
    {
        foreach (var feature in context.RenderSystem.RenderFeatures)
        {
            if (feature is not MeshRenderFeature meshes)
                continue;
            if (meshes.RenderFeatures.OfType<PostFxScreenSpaceRenderFeature>().FirstOrDefault() is { } existing)
                existing.ScreenSpace = screenSpace;
            else
                meshes.RenderFeatures.Add(new PostFxScreenSpaceRenderFeature { ScreenSpace = screenSpace });

            if (shadowMapRenderer == null)
            {
                var lighting = meshes.RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault();
                shadowMapRenderer = (lighting?.ShadowMapRenderer as ShadowMapRenderer)?.Renderers.OfType<LightDirectionalShadowMapRenderer>().FirstOrDefault();
                shadowMapRenderer?.AddReceiverExtension(contactShadowExtension);
            }
        }
    }

    /// <summary>
    /// Adds the motion vectors of the meshes to the render system if its compositor lacks them.
    /// </summary>
    private static void EnsureMotionVectors(RenderContext context)
    {
        foreach (var feature in context.RenderSystem.RenderFeatures)
        {
            if (feature is MeshRenderFeature meshes && !meshes.RenderFeatures.Any(x => x is MeshVelocityRenderFeature))
                meshes.RenderFeatures.Add(new MeshVelocityRenderFeature());
        }
    }

    private static Texture LoadTonyMcMapface(GraphicsDevice device)
    {
        // A DDS with the DX10 header (148 bytes), then 48^3 RGB9E5 texels
        using var stream = typeof(PostFxRenderer).Assembly.GetManifestResourceStream("Stride.PostFx.TonyMcMapface.dds")
            ?? throw new InvalidOperationException("The Tony McMapface look-up table is missing from the assembly");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        const int Size = 48;
        const int HeaderSize = 148;
        unsafe
        {
            fixed (byte* pointer = &data[HeaderSize])
            {
                var box = new DataBox((IntPtr)pointer, Size * 4, Size * Size * 4);
                var texture = Texture.New3D(device, Size, Size, Size, MipMapCount.One, PixelFormat.R9G9B9E5_Sharedexp, [box]);
                texture.Name = "TonyMcMapface";
                return texture;
            }
        }
    }

    private sealed class ViewState : IDisposable
    {
        public int LastFrame;
        public int ResolvedFrame = -1;
        public readonly PostFxProfile Profile = new();
        public Buffer Exposure;
        public TemporalHistory Temporal;
        public CameraEffectsTargets CameraTargets;
        public int CollectedFrame = -1;
        public ScreenSpaceTargets ScreenSpaceTargets;

        public void Dispose()
        {
            ScreenSpaceTargets?.Dispose();
            ScreenSpaceTargets = null;
            CameraTargets?.Dispose();
            CameraTargets = null;
            Exposure?.Dispose();
            Exposure = null;
            Temporal?.Dispose();
            Temporal = null;
        }
    }
}

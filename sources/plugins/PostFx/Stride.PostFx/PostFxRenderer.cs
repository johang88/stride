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
using Stride.PostFx.Passes;
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
/// The post-processing of a forward renderer: automatic exposure, a physically based glow, grading and the display
/// transform in one final pass, lens effects. The look (exposure compensation, glow, grading, lens) comes from the
/// post-processing volumes of the scene, blended at the camera; these are the renderer's quality settings and the look
/// without volumes.
/// </summary>
[DataContract("PostFxRenderer")]
[Display("Post-processing (PostFx)")]
public sealed class PostFxRenderer : ImageEffect, IPostProcessingEffects
{
    private const int ViewTimeout = 120;

    private readonly Dictionary<RenderView, ViewState> views = new();
    private readonly List<RenderView> staleViews = new();
    private ImageEffectShader finalShader;
    private BloomPass bloom;
    private ExposurePass exposure;
    private FXAAEffect fxaa;
    private TemporalPass temporal;
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
    /// Sharpening of the anti-aliased image (temporal anti-aliasing softens it a little).
    /// </summary>
    [DataMember(15)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Sharpness { get; set; } = 0.3f;

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
    /// The look of the last view drawn (the volumes blended at its camera).
    /// </summary>
    [DataMemberIgnore]
    public PostFxProfile Current { get; private set; }

    public bool RequiresVelocityBuffer => Antialiasing == PostFxAntialiasing.Taa;

    public bool RequiresNormalBuffer => false;

    public bool RequiresSpecularRoughnessBuffer => false;

    protected override void InitializeCore()
    {
        base.InitializeCore();
        finalShader = ToLoadAndUnload(new ImageEffectShader("PostFxFinal"));
        bloom = new BloomPass(ToLoadAndUnload(new ImageEffectShader("PostFxBloomDownsample")), ToLoadAndUnload(new ImageEffectShader("PostFxBloomUpsample")));
        exposure = new ExposurePass(new ComputeEffectShader(Context) { ShaderSourceName = "PostFxHistogram" }, new ComputeEffectShader(Context) { ShaderSourceName = "PostFxExposureAdapt" });
        exposure.Initialize(GraphicsDevice);
        fxaa = ToLoadAndUnload(new FXAAEffect { InputLuminanceInAlpha = true });
        temporal = new TemporalPass(ToLoadAndUnload(new ImageEffectShader("PostFxTemporal")));
        tonyMcMapface = LoadTonyMcMapface(GraphicsDevice);
        black = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new byte[] { 0, 0, 0, 0 });
    }

    protected override void Destroy()
    {
        foreach (var state in views.Values)
            state.Dispose();
        views.Clear();
        exposure?.Dispose();
        tonyMcMapface?.Dispose();
        black?.Dispose();
        base.Destroy();
    }

    public void Collect(RenderContext context)
    {
        if (!Initialized)
            Initialize(context);
        var view = context.RenderView;
        if (view == null)
            return;
        var state = GetState(view, context.RenderSystem.FrameCounter);
        Resolve(context, view, state);

        // Temporal anti-aliasing: the frame rendered with a sub-pixel offset (after the camera set the view's matrices,
        // before anything uploads them), the meshes writing their motion
        if (Antialiasing == PostFxAntialiasing.Taa)
        {
            EnsureMotionVectors(context);
            state.Temporal ??= new TemporalHistory();
            state.Temporal.ApplyJitter(view);
        }
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
            scene = temporal.Draw(context, state.Temporal, input, depth, motion, autoExposure, exposureScale, 0.08f);
            temporalResolved = true;
        }

        Texture glow = null;
        if (Bloom && (profile.Bloom.Intensity > 0.0f || profile.Bloom.LensDirtIntensity > 0.0f))
            glow = bloom.Draw(context, (w, h, f) => NewScopedRenderTarget2D(w, h, f), scene, profile.Bloom.Scatter);

        var antialias = Antialiasing == PostFxAntialiasing.Fxaa;
        var target = antialias ? NewScopedRenderTarget2D(output.ViewWidth, output.ViewHeight, output.ViewFormat) : output;
        DrawFinal(context, scene, glow, autoExposure, profile, target, antialias, temporalResolved ? Sharpness : 0.0f);

        if (antialias)
        {
            fxaa.SetInput(0, target);
            fxaa.SetOutput(output);
            fxaa.Draw(context);
        }
    }

    private void DrawFinal(RenderDrawContext context, Texture scene, Texture glow, Buffer autoExposure, PostFxProfile profile, Texture target, bool lumaInAlpha, float sharpness)
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
        parameters.Set(PostFxFinalKeys.PostFxLensDirtIntensity, glow != null && bloomSettings.LensDirt != null ? bloomSettings.LensDirtIntensity : 0.0f);

        var grading = profile.Grading;
        var whiteBalance = WhiteBalance(grading.Temperature, grading.Tint);
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance0, new Vector4(whiteBalance.M11, whiteBalance.M12, whiteBalance.M13, 0));
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance1, new Vector4(whiteBalance.M21, whiteBalance.M22, whiteBalance.M23, 0));
        parameters.Set(PostFxFinalKeys.PostFxWhiteBalance2, new Vector4(whiteBalance.M31, whiteBalance.M32, whiteBalance.M33, 0));
        parameters.Set(PostFxFinalKeys.PostFxFilter, (Vector3)grading.Filter);
        parameters.Set(PostFxFinalKeys.PostFxContrast, Math.Max(grading.Contrast, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxSaturation, Math.Max(grading.Saturation, 0.0f));
        parameters.Set(PostFxFinalKeys.PostFxShadows, (Vector3)grading.Shadows);
        parameters.Set(PostFxFinalKeys.PostFxHighlights, (Vector3)grading.Highlights);

        var lens = profile.Lens;
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
        return fixedExposure + ExposureOffset + profile.Exposure.Compensation;
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

        public void Dispose()
        {
            Exposure?.Dispose();
            Exposure = null;
            Temporal?.Dispose();
            Temporal = null;
        }
    }
}

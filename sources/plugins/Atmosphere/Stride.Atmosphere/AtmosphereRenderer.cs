// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Atmosphere.Clouds;
using Stride.Atmosphere.Fog;
using Stride.Atmosphere.Sky;
using Stride.Atmosphere.Weather;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;

namespace Stride.Atmosphere;

/// <summary>
/// How finely the clouds are raymarched: each pixel of the cloud buffer is marched every fourth frame and resolved over time.
/// </summary>
public enum CloudResolution
{
    /// <summary>
    /// A quarter of the screen's resolution along each axis.
    /// </summary>
    Quarter = 4,

    /// <summary>
    /// Half the screen's resolution along each axis.
    /// </summary>
    Half = 2,

    /// <summary>
    /// The screen's resolution.
    /// </summary>
    Full = 1,
}

/// <summary>
/// The sky and the participating media of a forward renderer, as one volumetric system: the physically based atmosphere
/// (the sky, the sun and the moon, the stars, the aerial perspective), the volumetric clouds with their shadows, the
/// volumetric fog (height fog, fog volumes, fog painted on terrains, smoke from particles) lit by both, and the light of
/// the sky captured for the surfaces. The look comes from the scene (sky atmosphere, volumetric clouds, weather and
/// volumetric fog components); these are the quality settings.
/// </summary>
[DataContract("AtmosphereRenderer")]
[Display("Atmosphere")]
public sealed class AtmosphereRenderer : RendererCoreBase, IAtmosphereRenderer
{
    /// <summary>
    /// Property key of the scene's sky in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<RenderAtmosphere> CurrentAtmosphere = new("Atmosphere.CurrentAtmosphere", typeof(AtmosphereRenderer));

    /// <summary>
    /// Property key of the scene's clouds in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<RenderClouds> CurrentClouds = new("Atmosphere.CurrentClouds", typeof(AtmosphereRenderer));

    /// <summary>
    /// Property key of the scene's weather in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<WeatherState> CurrentWeather = new("Atmosphere.CurrentWeather", typeof(AtmosphereRenderer));

    public static readonly ProfilingKey SkyKey = new("Atmosphere.Sky");

    public static readonly ProfilingKey CloudsKey = new("Atmosphere.Clouds");

    public static readonly ProfilingKey CompositeKey = new("Atmosphere.Composite");

    public static readonly ProfilingKey SkyCaptureKey = new("Atmosphere.SkyCapture");

    public static readonly ProfilingKey CloudShadowKey = new("Atmosphere.CloudShadow");

    public static readonly ProfilingKey SkyFilterKey = new("Atmosphere.SkyFilter");

    private const int ViewTimeout = 120;

    private static readonly WeatherState DefaultWeather = WeatherState.FromPreset(WeatherPreset.Fair);

    private readonly Dictionary<RenderView, ViewState> views = new();
    private readonly List<RenderView> staleViews = new();
    private readonly CloudShadowReceiverExtension cloudShadowExtension = new();
    private readonly CloudShadows cloudShadows = new();
    private SkyRenderer sky;
    private CloudRenderer clouds;
    private ImageEffectShader composite;
    private LightDirectionalShadowMapRenderer shadowMapRenderer;
    private Action<ParameterCollection> setCloudPassParameters;
    private bool cloudShadowsRegistered;

    // The scene of the frame (from the first view collected), and what was rendered for it once
    private int collectedFrame = -1;
    private int preparedFrame = -1;
    private RenderAtmosphere atmosphere;
    private RenderClouds sceneClouds;
    private WeatherState weather = DefaultWeather;
    private Vector3 frameEye;

    public AtmosphereRenderer()
    {
        Enabled = true;
    }

    /// <summary>
    /// Whether the volumetric clouds are drawn.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(true)]
    public bool Clouds { get; set; } = true;

    /// <summary>
    /// Resolution of the cloud raymarch.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(CloudResolution.Half)]
    public CloudResolution CloudResolution { get; set; } = CloudResolution.Half;

    /// <summary>
    /// Raymarching steps through the low cloud layer (along the longest rays; fewer through short ones).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(96)]
    [DataMemberRange(16, 512, 4, 16, 0)]
    public int CloudSteps { get; set; } = 96;

    /// <summary>
    /// Steps towards the light from each cloud sample (self-shadowing).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(6)]
    [DataMemberRange(1, 12, 1, 2, 0)]
    public int CloudLightSteps { get; set; } = 6;

    /// <summary>
    /// Weight of a new cloud sample in the temporal blend (lower: smoother, slower to follow fast changes).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.08f)]
    [DataMemberRange(0.01, 1, 0.01, 0.05, 2)]
    public float CloudTemporalBlend { get; set; } = 0.08f;

    /// <summary>
    /// Whether the clouds shade the surfaces and the fog.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(true)]
    public bool CloudShadows { get; set; } = true;

    /// <summary>
    /// Texels along a side of the cloud shadow map.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(256)]
    [DataMemberRange(64, 2048, 64, 256, 0)]
    public int CloudShadowMapSize { get; set; } = 256;

    /// <summary>
    /// Size of the area around the camera the cloud shadows cover (meters).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(10000.0f)]
    [DataMemberRange(1000, 100000, 100, 1000, 0)]
    public float CloudShadowDistance { get; set; } = 10000.0f;

    /// <summary>
    /// Size of the faces of the sky capture lighting the surfaces (one face is captured each frame).
    /// </summary>
    [DataMember(90)]
    [DefaultValue(64)]
    [DataMemberRange(32, 512, 32, 64, 0)]
    public int SkyCaptureSize { get; set; } = 64;

    /// <summary>
    /// Debug view of the clouds (0: none, 1-2: shape noise slices, 3: detail noise, 4: weather map).
    /// </summary>
    [DataMemberIgnore]
    public int CloudDebug { get => clouds?.Debug ?? cloudDebug; set { cloudDebug = value; if (clouds != null) clouds.Debug = value; } }

    private int cloudDebug;

    /// <summary>
    /// The volumetric fog.
    /// </summary>
    [DataMember(100)]
    [Display(Expand = ExpandRule.Once)]
    public VolumetricFogRenderer Fog { get; set; } = new();

    protected override void InitializeCore()
    {
        base.InitializeCore();
        sky = new SkyRenderer();
        sky.Initialize(Context);
        clouds = new CloudRenderer { Debug = cloudDebug };
        clouds.Initialize(Context);
        composite = ToLoadAndUnload(new ImageEffectShader("AtmosphereCompositeEffect"));
        // Background * transmittance (alpha) + light (color)
        var blend = BlendStates.Opaque;
        blend.RenderTargets[0].BlendEnable = true;
        blend.RenderTargets[0].ColorSourceBlend = Blend.One;
        blend.RenderTargets[0].ColorDestinationBlend = Blend.SourceAlpha;
        blend.RenderTargets[0].ColorBlendFunction = BlendFunction.Add;
        blend.RenderTargets[0].AlphaSourceBlend = Blend.Zero;
        blend.RenderTargets[0].AlphaDestinationBlend = Blend.One;
        blend.RenderTargets[0].AlphaBlendFunction = BlendFunction.Add;
        composite.BlendState = blend;
        composite.DepthStencilState = DepthStencilStates.None;
        setCloudPassParameters = SetCloudPassParameters;

        // Cloud shadows join the sun's shadow maps
        var meshRenderFeature = Context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
        var shadowRenderer = meshRenderFeature?.RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault()?.ShadowMapRenderer as ShadowMapRenderer;
        shadowMapRenderer = shadowRenderer?.Renderers.OfType<LightDirectionalShadowMapRenderer>().FirstOrDefault();
        if (Context.Services.GetService<CloudShadows>() == null)
        {
            Context.Services.AddService(cloudShadows);
            cloudShadowsRegistered = true;
        }
    }

    protected override void Destroy()
    {
        foreach (var state in views.Values)
            state.Dispose();
        views.Clear();
        shadowMapRenderer?.RemoveReceiverExtension(cloudShadowExtension);
        if (cloudShadowsRegistered)
            Context.Services.RemoveService<CloudShadows>();
        sky?.Dispose();
        clouds?.Dispose();
        Fog?.Dispose();
        base.Destroy();
    }

    /// <inheritdoc/>
    public void Collect(RenderContext context, RenderStage transparentStage)
    {
        if (!Initialized)
            Initialize(context);
        var frame = context.RenderSystem.FrameCounter;
        var view = context.RenderView;
        var tags = context.VisibilityGroup.Tags;

        // The scene, once per frame
        if (collectedFrame != frame)
        {
            collectedFrame = frame;
            atmosphere = Enabled ? tags.Get(CurrentAtmosphere) : null;
            sceneClouds = atmosphere != null && Clouds ? tags.Get(CurrentClouds) : null;
            weather = tags.Get(CurrentWeather) ?? DefaultWeather;
            Matrix.Invert(ref view.View, out var frameViewInverse);
            frameEye = frameViewInverse.TranslationVector;

            // Cloud shadows on the surfaces (the map placed now: the surfaces' parameters are set before it is drawn)
            var shadows = sceneClouds != null && CloudShadows && atmosphere.KeyLight != null;
            if (shadows)
            {
                clouds.ShadowMapSize = CloudShadowMapSize;
                clouds.ShadowDistance = CloudShadowDistance;
                cloudShadows.Texture = clouds.PlaceShadowMap(frameEye);
                cloudShadows.Light = atmosphere.KeyLight;
                cloudShadows.Region = new Vector4(clouds.ShadowRegion, Math.Clamp(sceneClouds.ShadowStrength, 0, 1));
                var (lightDirection, _) = CloudRenderer.CloudLight(atmosphere);
                cloudShadows.LightDirection = new Vector4(lightDirection, atmosphere.SeaLevel);
                shadowMapRenderer?.AddReceiverExtension(cloudShadowExtension);
            }
            else
            {
                cloudShadows.Texture = null;
                cloudShadows.Light = null;
            }
        }

        // Forget the views that aren't drawn anymore
        staleViews.Clear();
        foreach (var (otherView, otherState) in views)
        {
            if (frame - otherState.LastFrame > ViewTimeout)
                staleViews.Add(otherView);
        }
        foreach (var staleView in staleViews)
        {
            views[staleView].Dispose();
            views.Remove(staleView);
        }

        if (!views.TryGetValue(view, out var state))
            views.Add(view, state = new ViewState());
        state.LastFrame = frame;

        // What the fog of the view is lit with
        var environment = state.FogEnvironment;
        environment.DensityScale = weather.FogDensity;
        if (atmosphere != null)
        {
            if (state.CameraVolume == null)
            {
                state.CameraVolume = Texture.New3D(GraphicsDevice, SkyRenderer.VolumeSize, SkyRenderer.VolumeSize, SkyRenderer.VolumeSize, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
                state.CameraVolume.Name = "AtmosphereAerialPerspective";
            }
            environment.Sun = atmosphere.KeyLight;
            environment.SkyCubeMap = sky.SkyCube;
            environment.SkyIntensity = 1.0f;
            environment.SkyLighting = sky.SkyCube != null ? sky.Harmonics : null;
            environment.AerialPerspective = state.CameraVolume;
            environment.AerialPerspectiveParameters = new Vector4(sky.AerialPerspectiveKmPerSlice, atmosphere.AerialPerspectiveScale, 1, 0);
            var fogShadows = cloudShadows.Texture != null && sceneClouds.FogShadowStrength > 0;
            environment.CloudShadow = fogShadows ? cloudShadows.Texture : null;
            environment.CloudShadowRegion = fogShadows ? new Vector4(clouds.ShadowRegion, Math.Clamp(sceneClouds.FogShadowStrength, 0, 1)) : Vector4.Zero;
            environment.CloudShadowLight = cloudShadows.LightDirection;
        }
        else
        {
            environment.Sun = null;
            environment.SkyCubeMap = null;
            environment.SkyLighting = null;
            environment.AerialPerspective = null;
            environment.CloudShadow = null;
        }
        Fog?.Collect(context, transparentStage, environment);
    }

    /// <inheritdoc/>
    public void Prepare(RenderDrawContext context)
    {
        var view = context.RenderContext.RenderView;
        var frame = context.RenderContext.RenderSystem.FrameCounter;
        if (!views.TryGetValue(view, out var state) || state.LastFrame != frame)
            return;

        if (atmosphere != null)
        {
            // (the look-up tables and the capture are drawn as image effects: the view's targets stay bound)
            using (context.PushRenderTargetsAndRestore())
            using (context.QueryManager.BeginProfile(Color.SkyBlue, SkyKey))
            {
                var haze = weather.Haze;
                // The sky of the frame, once (from the first view)
                if (preparedFrame != frame)
                {
                    preparedFrame = frame;
                    sky.CaptureSize = SkyCaptureSize;
                    sky.Update(context, atmosphere, haze, frameEye);
                    if (sceneClouds != null)
                    {
                        clouds.Update(context, sceneClouds, weather, context.RenderContext.Time?.Total.TotalSeconds ?? 0.0);
                        if (cloudShadows.Texture != null)
                        {
                            using (context.QueryManager.BeginProfile(Color.SkyBlue, CloudShadowKey))
                                clouds.DrawShadowMap(context, setCloudPassParameters, frameEye);
                        }
                    }
                    using (context.QueryManager.BeginProfile(Color.SkyBlue, SkyCaptureKey))
                        sky.Capture(context, atmosphere, haze, frameEye, sceneClouds != null ? setCloudPassParameters : null);
                }

                Matrix.Invert(ref view.ViewProjection, out var inverseViewProjection);
                Matrix.Invert(ref view.View, out var viewInverse);
                sky.DrawCameraVolume(context, atmosphere, haze, state.CameraVolume, inverseViewProjection, viewInverse.TranslationVector);
            }
        }

        Fog?.DrawVolume(context);
    }

    /// <inheritdoc/>
    public void Draw(RenderDrawContext context, Texture color, Texture depthStencil)
    {
        var view = context.RenderContext.RenderView;
        var frame = context.RenderContext.RenderSystem.FrameCounter;
        if (color == null || depthStencil == null || !views.TryGetValue(view, out var state) || state.LastFrame != frame)
            return;

        var parameters = composite.Parameters;
        var hasMedia = Fog?.ApplyViewParameters(context.RenderContext, parameters) ?? false;
        if (atmosphere == null && !hasMedia)
            return;

        using var _ = context.QueryManager.BeginProfile(Color.SkyBlue, CompositeKey);
        var depth = context.Resolver.ResolveDepthStencil(depthStencil);
        context.CommandList.ResourceBarrierTransition(depth, BarrierLayout.ShaderResource);

        Matrix.Invert(ref view.View, out var viewInverse);
        var eye = viewInverse.TranslationVector;
        var projection = view.Projection;
        var verticalFieldOfView = 2.0f * MathF.Atan(1.0f / projection.M22);
        var hasClouds = false;
        if (atmosphere != null)
        {
            sky.SetParameters(parameters, atmosphere, weather.Haze, verticalFieldOfView / Math.Max(color.ViewHeight, 1));
            if (sceneClouds != null)
            {
                using (context.QueryManager.BeginProfile(Color.SkyBlue, CloudsKey))
                using (context.PushRenderTargetsAndRestore())
                {
                    clouds.DrawView(context, state.Clouds, setCloudPassParameters, depth, new Int2(color.ViewWidth, color.ViewHeight), view.ViewProjection, eye, verticalFieldOfView,
                        (int)CloudResolution, CloudSteps, CloudLightSteps, sceneClouds.MaxDistance, CloudTemporalBlend);
                }
                hasClouds = state.Clouds.Resolved != null;
            }
        }

        parameters.Set(AtmosphereCompositeKeys.CompositeProjection, new Vector4(1.0f / projection.M11, 1.0f / projection.M22, projection.M31, projection.M32));
        parameters.Set(AtmosphereCompositeKeys.CompositeDepth, new Vector2(projection.M33, projection.M43));
        parameters.Set(AtmosphereCompositeKeys.CompositeViewInverse, viewInverse);
        parameters.Set(AtmosphereCompositeKeys.CompositeTargetSize, new Vector2(color.ViewWidth, color.ViewHeight));
        parameters.Set(AtmosphereCompositeKeys.CompositeCloudViewProjection, hasClouds ? state.Clouds.ResolvedViewProjection : view.ViewProjection);
        parameters.Set(AtmosphereCompositeKeys.CompositeCloudSize, hasClouds ? new Vector3(state.Clouds.Resolved.Width, state.Clouds.Resolved.Height, 1) : new Vector3(1, 1, 0));
        parameters.Set(AtmosphereCompositeKeys.CompositeSky, atmosphere != null ? 1.0f : 0.0f);

        using (context.PushRenderTargetsAndRestore())
        {
            composite.SetInput(0, depth);
            composite.SetInput(1, hasClouds ? state.Clouds.Resolved : context.GraphicsDevice.GetSharedWhiteTexture());
            composite.SetOutput(color);
            composite.Draw(context, "AtmosphereComposite");
        }

        context.Resolver.ReleaseDepthStenctilAsShaderResource(depth);
        context.CommandList.ResourceBarrierTransition(depthStencil, BarrierLayout.DepthStencilWrite);
    }

    // The atmosphere and cloud parameters of the cloud passes (march, shadow map, capture)
    private void SetCloudPassParameters(ParameterCollection parameters)
    {
        sky.SetParameters(parameters, atmosphere, weather.Haze);
        clouds.SetParameters(parameters, atmosphere, sceneClouds, weather, sky.Ambient, frameEye);
    }

    private sealed class ViewState : IDisposable
    {
        public readonly FogEnvironment FogEnvironment = new();
        public readonly CloudViewState Clouds = new();
        public Texture CameraVolume;
        public int LastFrame;

        public void Dispose()
        {
            Clouds.Dispose();
            CameraVolume?.Dispose();
            CameraVolume = null;
        }
    }
}

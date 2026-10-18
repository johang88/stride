// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Rendering.Skyboxes;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering.VolumetricFog;

/// <summary>
/// Volumetric fog of a forward renderer: the fog of the scene (<see cref="CurrentFog"/>, its local volumes and the fog layers
/// painted on height fields) is lit in a froxel grid aligned with the view (sun with shadows, sky, local lights), integrated
/// along the view rays and applied to the opaque surfaces and the sky (full-screen pass) and to the transparent ones (in their
/// shading, see <see cref="VolumetricFogRenderFeature"/>). Beyond the distance the grid covers, the height fog is integrated
/// analytically.
/// </summary>
/// <remarks>
/// The grid is jittered every frame and blended with the previous frame's (temporal reprojection), so a coarse grid gives
/// smooth results. These are the quality settings; the look of the fog comes from the scene.
/// </remarks>
[DataContract("VolumetricFogRenderer")]
[Display("Volumetric fog")]
public sealed class VolumetricFogRenderer : RendererCoreBase
{
    /// <summary>
    /// Property key of the scene fog in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<RenderVolumetricFog> CurrentFog = new("VolumetricFog.CurrentFog", typeof(VolumetricFogRenderer));

    /// <summary>
    /// Property key of the local fog volumes in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<List<RenderFogVolume>> CurrentVolumes = new("VolumetricFog.CurrentVolumes", typeof(VolumetricFogRenderer));

    /// <summary>
    /// Property key of the height fields with painted fog in <see cref="VisibilityGroup.Tags"/> (the first one is used).
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<List<RenderFogHeightfield>> CurrentHeightfields = new("VolumetricFog.CurrentHeightfields", typeof(VolumetricFogRenderer));

    public const int MaxLights = 64;
    public const int MaxVolumes = 32;

    private const int LightStride = 4;
    private const int VolumeStride = 8;
    private const int NoiseSize = 64;

    /// <summary>
    /// Slice distribution: nearly linear over the first 1 / B meters, exponential beyond.
    /// </summary>
    private const float DepthDistribution = 0.25f;

    /// <summary>
    /// Views not drawn for this many frames lose their grid.
    /// </summary>
    private const int ViewTimeout = 120;

    /// <summary>
    /// GPU profiling key of the lighting and integration of the fog grid.
    /// </summary>
    public static readonly ProfilingKey VolumeKey = new("VolumetricFog.Volume");

    /// <summary>
    /// GPU profiling key of the full-screen pass fogging the opaque surfaces.
    /// </summary>
    public static readonly ProfilingKey ApplyKey = new("VolumetricFog.Apply");

    private readonly Dictionary<RenderView, ViewState> views = new();
    private readonly List<RenderView> staleViews = new();
    private readonly Vector4[] lightData = new Vector4[MaxLights * LightStride];
    private readonly Vector4[] volumeData = new Vector4[MaxVolumes * VolumeStride];
    private readonly List<(float Distance, RenderLight Light)> lightCandidates = new();
    private readonly List<(float Distance, RenderFogVolume Volume)> volumeCandidates = new();
    private readonly Matrix[] shadowMatrices = new Matrix[4];

    private IShadowMapRenderer shadowMapRenderer;
    private VolumetricFogRenderFeature renderFeature;
    private ComputeEffectShader scatterEffect;
    private ComputeEffectShader integrateEffect;
    private ComputeEffectShader noiseEffect;
    private ImageEffectShader applyEffect;
    private Texture noiseTexture;
    private Buffer lightBuffer;
    private Buffer volumeBuffer;

    public VolumetricFogRenderer()
    {
        Enabled = true;
    }

    /// <summary>
    /// Whether the fog is lit and integrated in a froxel grid; otherwise only the height fog is applied (analytically, without
    /// shadows, local lights, volumes or painted fog).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(true)]
    public bool Volumetric { get; set; } = true;

    /// <summary>
    /// Width and height of a froxel in pixels: smaller is sharper (more detailed shadows and volumes) and costs more.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(8)]
    [DataMemberRange(4, 32, 1, 4, 0)]
    public int TileSize { get; set; } = 8;

    /// <summary>
    /// Number of froxel slices along the view depth.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(128)]
    [DataMemberRange(16, 256, 8, 32, 0)]
    public int DepthSlices { get; set; } = 128;

    /// <summary>
    /// Blends each frame with the previous ones (with a different jitter every frame): much smoother, a little trailing in
    /// fast changes.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(true)]
    public bool TemporalReprojection { get; set; } = true;

    /// <summary>
    /// Weight of the previous frames in the temporal blend.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.9f)]
    [DataMemberRange(0, 0.98, 0.01, 0.05, 2)]
    public float HistoryWeight { get; set; } = 0.9f;

    /// <summary>
    /// Whether the sun light in the fog is shadowed (shadow maps and height field shadows: light shafts).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(true)]
    public bool Shadows { get; set; } = true;

    /// <summary>
    /// Whether point and spot lights light the fog (up to <see cref="MaxLights"/>, the closest ones).
    /// </summary>
    [DataMember(70)]
    [DefaultValue(true)]
    public bool LocalLights { get; set; } = true;

    protected override void InitializeCore()
    {
        base.InitializeCore();

        var meshRenderFeature = Context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
        shadowMapRenderer = meshRenderFeature?.RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault()?.ShadowMapRenderer;
        if (meshRenderFeature != null)
        {
            renderFeature = meshRenderFeature.RenderFeatures.OfType<VolumetricFogRenderFeature>().FirstOrDefault();
            if (renderFeature == null)
            {
                renderFeature = new VolumetricFogRenderFeature();
                meshRenderFeature.RenderFeatures.Add(renderFeature);
            }
        }

        scatterEffect = new ComputeEffectShader(Context) { ShaderSourceName = "VolumetricFogScatter", ThreadNumbers = new Int3(4, 4, 4) };
        integrateEffect = new ComputeEffectShader(Context) { ShaderSourceName = "VolumetricFogIntegrate", ThreadNumbers = new Int3(8, 8, 1) };
        noiseEffect = new ComputeEffectShader(Context) { ShaderSourceName = "VolumetricFogNoise", ThreadNumbers = new Int3(4, 4, 4) };

        applyEffect = ToLoadAndUnload(new ImageEffectShader("VolumetricFogApplyEffect"));
        // Scene color * transmittance (alpha) + scattered light (color)
        var blend = BlendStates.Opaque;
        blend.RenderTargets[0].BlendEnable = true;
        blend.RenderTargets[0].ColorSourceBlend = Blend.One;
        blend.RenderTargets[0].ColorDestinationBlend = Blend.SourceAlpha;
        blend.RenderTargets[0].ColorBlendFunction = BlendFunction.Add;
        blend.RenderTargets[0].AlphaSourceBlend = Blend.Zero;
        blend.RenderTargets[0].AlphaDestinationBlend = Blend.One;
        blend.RenderTargets[0].AlphaBlendFunction = BlendFunction.Add;
        applyEffect.BlendState = blend;

        lightBuffer = Buffer.New(GraphicsDevice, lightData.Length * 16, 0, BufferFlags.ShaderResource, PixelFormat.R32G32B32A32_Float);
        lightBuffer.Name = "VolumetricFogLights";
        volumeBuffer = Buffer.New(GraphicsDevice, volumeData.Length * 16, 0, BufferFlags.ShaderResource, PixelFormat.R32G32B32A32_Float);
        volumeBuffer.Name = "VolumetricFogVolumes";
    }

    protected override void Destroy()
    {
        foreach (var state in views.Values)
            state.Dispose();
        views.Clear();
        scatterEffect?.Dispose();
        integrateEffect?.Dispose();
        noiseEffect?.Dispose();
        noiseTexture?.Dispose();
        lightBuffer?.Dispose();
        volumeBuffer?.Dispose();
        base.Destroy();
    }

    /// <summary>
    /// Collects the fog of the current view (after its camera was set) and sizes its grid. The transparent surfaces of
    /// <paramref name="transparentStage"/> sample the fog in their shading.
    /// </summary>
    public void Collect(RenderContext context, RenderStage transparentStage)
    {
        if (!Initialized)
            Initialize(context);
        var frame = context.RenderSystem.FrameCounter;

        var view = context.RenderView;
        var tags = context.VisibilityGroup.Tags;
        var fog = Enabled ? tags.Get(CurrentFog) : null;

        if (renderFeature != null && transparentStage != null)
            renderFeature.TransparentStages.Add(transparentStage);

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
            renderFeature?.ViewParameters.Remove(staleView);
        }

        if (fog == null || view.ViewSize.X < 1 || view.ViewSize.Y < 1)
        {
            renderFeature?.ViewParameters.Remove(view);
            return;
        }

        if (!views.TryGetValue(view, out var state))
            views.Add(view, state = new ViewState());
        state.LastFrame = frame;
        state.Fog = fog;
        state.Volumes = tags.Get(CurrentVolumes);
        state.Heightfield = tags.Get(CurrentHeightfields)?.FirstOrDefault(x => x.Heightmap != null);
        state.Lights = tags.Get(ForwardLightingRenderFeature.CurrentLights);

        // The grid, (re)allocated when the view or the quality changes
        var volumetric = Volumetric && fog.Distance > 0;
        var size = volumetric
            ? new Int3(Math.Max(1, (int)MathF.Ceiling(view.ViewSize.X / Math.Max(TileSize, 1))), Math.Max(1, (int)MathF.Ceiling(view.ViewSize.Y / Math.Max(TileSize, 1))), Math.Clamp(DepthSlices, 4, 512))
            : Int3.Zero;
        if (size != state.Size)
        {
            state.Dispose();
            state.Size = size;
            if (volumetric)
            {
                for (int i = 0; i < 2; i++)
                    state.Scatter[i] = Texture.New3D(GraphicsDevice, size.X, size.Y, size.Z, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
                state.Integrated = Texture.New3D(GraphicsDevice, size.X, size.Y, size.Z, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
                state.Scatter[0].Name = state.Scatter[1].Name = "VolumetricFogScatter";
                state.Integrated.Name = "VolumetricFogIntegrated";
            }
        }

        var distance = Math.Max(fog.Distance, 1.0f);
        state.Grid = new Vector4(DepthDistribution, MathF.Log2(distance * DepthDistribution + 1), distance, Math.Max(size.Z, 1));

        // The lights of the fog
        state.Sun = FindSun(state.Lights);
        var sunColor = state.Sun != null ? state.Sun.Color * fog.SunScattering : new Color3();
        var sunDirection = state.Sun?.Direction ?? -Vector3.UnitY;
        // The light around: half from the sky, half from the ground lit by the sun and the sky
        var skyLight = ComputeAmbient(state.Lights);
        var sunIrradiance = state.Sun != null ? state.Sun.Color * Math.Max(0, -Vector3.Normalize(sunDirection).Y) : new Color3();
        var ground = fog.GroundAlbedo * (sunIrradiance * (1.0f / MathF.PI) + skyLight);
        state.Ambient = (skyLight + ground) * (0.5f * fog.AmbientScattering);
        var (skyCubeMap, sky, skyMatrix) = FindSkyCubeMap(state.Lights);
        sky.Y *= fog.AmbientScattering;
        sky.W = 0.5f;

        Matrix.Invert(ref view.View, out var viewInverse);
        var eye = viewInverse.TranslationVector;
        var hasFog = fog.Density > 0 || (volumetric && (state.Volumes?.Count > 0 || state.Heightfield?.LayerCount > 0));
        var skyDistance = fog.FogSky ? Math.Max(view.FarClipPlane, distance) : 0.0f;
        state.Parameters = new VolumetricFogViewParameters
        {
            Integrated = volumetric ? state.Integrated : null,
            Grid = state.Grid,
            Height = new Vector4(fog.Density, fog.HeightFalloff, fog.BaseHeight, volumetric ? 1 : 0),
            Ambient = new Vector4((Vector3)state.Ambient, hasFog ? 1 : 0),
            Albedo = new Vector4((Vector3)fog.Albedo, Math.Clamp(fog.MultipleScattering, 0, 1)),
            Ground = new Vector4((Vector3)(ground * fog.AmbientScattering), 0),
            Emissive = new Vector4((Vector3)fog.Emissive, 0),
            SkyCubeMap = skyCubeMap,
            Sky = sky,
            SkyMatrix = skyMatrix,
            Sun = new Vector4((Vector3)(sunColor * fog.Albedo), Math.Clamp(fog.Anisotropy, -0.95f, 0.95f)),
            SunDirection = new Vector4(Vector3.Normalize(sunDirection), skyDistance),
            Eye = eye,
        };
        renderFeature?.ViewParameters.Remove(view);
        renderFeature?.ViewParameters.Add(view, state.Parameters);
    }

    /// <summary>
    /// Lights and integrates the fog grid of the current view. Call after the shadow maps were drawn, before the opaque surfaces.
    /// </summary>
    public void DrawVolume(RenderDrawContext context)
    {
        var view = context.RenderContext.RenderView;
        if (!views.TryGetValue(view, out var state) || state.Integrated == null || state.LastFrame != context.RenderContext.RenderSystem.FrameCounter)
            return;
        var fog = state.Fog;

        using var _ = context.QueryManager.BeginProfile(Color.LightBlue, VolumeKey);
        EnsureNoise(context);

        var size = state.Size;
        var commandList = context.CommandList;
        Matrix.Invert(ref view.View, out var viewInverse);
        var eye = viewInverse.TranslationVector;
        var projection = view.Projection;
        var projectionTerms = new Vector4(1.0f / projection.M11, 1.0f / projection.M22, projection.M31, projection.M32);

        var parameters = scatterEffect.Parameters;
        parameters.Set(VolumetricFogScatterKeys.FogViewInverse, viewInverse);
        parameters.Set(VolumetricFogScatterKeys.FogProjection, projectionTerms);
        parameters.Set(VolumetricFogScatterKeys.FogEye, eye);
        parameters.Set(VolumetricFogScatterKeys.FogGrid, state.Grid);
        parameters.Set(VolumetricFogScatterKeys.FogGridSize, new Vector3(size.X, size.Y, size.Z));

        // A different sample in each froxel every frame (Halton 2, 3, 5), blended over the frames
        var useHistory = TemporalReprojection && state.HasHistory;
        var jitterIndex = TemporalReprojection ? state.FrameIndex % 16 + 1 : 0;
        var jitter = TemporalReprojection ? new Vector3(Halton(jitterIndex, 2), Halton(jitterIndex, 3), Halton(jitterIndex, 5)) - new Vector3(0.5f) : Vector3.Zero;
        parameters.Set(VolumetricFogScatterKeys.FogJitter, jitter);
        parameters.Set(VolumetricFogScatterKeys.FogPrevViewProjection, state.PreviousViewProjection);
        parameters.Set(VolumetricFogScatterKeys.FogPrevView, state.PreviousView);
        parameters.Set(VolumetricFogScatterKeys.FogPrevGrid, state.PreviousGrid);
        parameters.Set(VolumetricFogScatterKeys.FogHistoryWeight, useHistory ? Math.Clamp(HistoryWeight, 0, 0.98f) : 0.0f);

        // Medium
        parameters.Set(VolumetricFogScatterKeys.FogHeight, new Vector3(fog.Density, fog.HeightFalloff, fog.BaseHeight));
        parameters.Set(VolumetricFogScatterKeys.FogAlbedo, (Vector3)fog.Albedo);
        parameters.Set(VolumetricFogScatterKeys.FogEmissive, (Vector3)fog.Emissive);
        parameters.Set(VolumetricFogScatterKeys.FogAnisotropy, Math.Clamp(fog.Anisotropy, -0.95f, 0.95f));
        parameters.Set(VolumetricFogScatterKeys.FogMultipleScattering, Math.Clamp(fog.MultipleScattering, 0, 1));
        parameters.Set(VolumetricFogScatterKeys.FogNoiseStrength, Math.Clamp(fog.NoiseStrength, 0, 1));
        var noiseScale = Math.Max(fog.NoiseScale, 0.1f);
        parameters.Set(VolumetricFogScatterKeys.FogNoiseFrequency, 1.0f / noiseScale);
        var time = (float)(context.RenderContext.Time?.Total.TotalSeconds ?? 0);
        // Wrapped to the noise period (one noise texture per noise scale) to keep the precision
        var offset = -fog.Wind * time / noiseScale;
        parameters.Set(VolumetricFogScatterKeys.FogNoiseOffset, new Vector3(Wrap(offset.X), Wrap(offset.Y), Wrap(offset.Z)));

        // Sun
        var sunColor = state.Sun != null ? state.Sun.Color * fog.SunScattering : new Color3();
        parameters.Set(VolumetricFogScatterKeys.FogSunColor, (Vector3)sunColor);
        parameters.Set(VolumetricFogScatterKeys.FogSunDirection, Vector3.Normalize(state.Sun?.Direction ?? -Vector3.UnitY));
        SetShadowParameters(parameters, view, state.Sun);
        parameters.Set(VolumetricFogScatterKeys.FogAmbient, (Vector3)state.Ambient);

        // Local lights and volumes near enough
        var lightCount = LocalLights && fog.LocalLightScattering > 0 ? GatherLights(state.Lights, view, eye, state.Grid.Z) : 0;
        if (lightCount > 0)
            lightBuffer.SetData(commandList, (ReadOnlySpan<Vector4>)lightData.AsSpan(0, lightCount * LightStride));
        parameters.Set(VolumetricFogScatterKeys.FogLights, lightBuffer);
        parameters.Set(VolumetricFogScatterKeys.FogLightCount, lightCount);
        parameters.Set(VolumetricFogScatterKeys.FogLocalLightScale, fog.LocalLightScattering);
        var volumeCount = GatherVolumes(state.Volumes, view, eye, state.Grid.Z);
        if (volumeCount > 0)
            volumeBuffer.SetData(commandList, (ReadOnlySpan<Vector4>)volumeData.AsSpan(0, volumeCount * VolumeStride));
        parameters.Set(VolumetricFogScatterKeys.FogVolumes, volumeBuffer);
        parameters.Set(VolumetricFogScatterKeys.FogVolumeCount, volumeCount);

        SetHeightfieldParameters(parameters, state.Heightfield, context.GraphicsDevice);

        var current = state.Current;
        parameters.Set(VolumetricFogScatterKeys.FogNoiseTexture, noiseTexture);
        parameters.Set(VolumetricFogScatterKeys.FogHistory, state.Scatter[1 - current]);
        parameters.Set(VolumetricFogScatterKeys.FogScatterOutput, state.Scatter[current]);
        scatterEffect.ThreadGroupCounts = new Int3((size.X + 3) / 4, (size.Y + 3) / 4, (size.Z + 3) / 4);
        scatterEffect.Draw(context, "VolumetricFogScatter");

        var integrate = integrateEffect.Parameters;
        integrate.Set(VolumetricFogIntegrateKeys.FogGrid, state.Grid);
        integrate.Set(VolumetricFogIntegrateKeys.FogGridSize, new Vector3(size.X, size.Y, size.Z));
        integrate.Set(VolumetricFogIntegrateKeys.FogProjection, projectionTerms);
        integrate.Set(VolumetricFogIntegrateKeys.FogScatterInput, state.Scatter[current]);
        integrate.Set(VolumetricFogIntegrateKeys.FogIntegratedOutput, state.Integrated);
        integrateEffect.ThreadGroupCounts = new Int3((size.X + 7) / 8, (size.Y + 7) / 8, 1);
        integrateEffect.Draw(context, "VolumetricFogIntegrate");

        // This frame is the history of the next one
        state.Current = 1 - current;
        state.HasHistory = true;
        state.FrameIndex++;
        state.PreviousViewProjection = view.ViewProjection;
        state.PreviousView = view.View;
        state.PreviousGrid = state.Grid;
    }

    /// <summary>
    /// Fogs the opaque surfaces and the sky of the current view.
    /// </summary>
    /// <param name="context">The draw context.</param>
    /// <param name="color">The color target (written).</param>
    /// <param name="depthStencil">Its depth buffer (read).</param>
    public void Apply(RenderDrawContext context, Texture color, Texture depthStencil)
    {
        var view = context.RenderContext.RenderView;
        if (color == null || depthStencil == null || !views.TryGetValue(view, out var state) || state.LastFrame != context.RenderContext.RenderSystem.FrameCounter || state.Parameters.Ambient.W <= 0)
            return;

        using var _ = context.QueryManager.BeginProfile(Color.LightBlue, ApplyKey);
        var depth = context.Resolver.ResolveDepthStencil(depthStencil);
        context.CommandList.ResourceBarrierTransition(depth, BarrierLayout.ShaderResource);

        var projection = view.Projection;
        Matrix.Invert(ref view.View, out var viewInverse);
        var parameters = applyEffect.Parameters;
        state.Parameters.ApplyTo(parameters, context.GraphicsDevice);
        parameters.Set(VolumetricFogApplyShaderKeys.FogApplyProjection, new Vector4(1.0f / projection.M11, 1.0f / projection.M22, projection.M31, projection.M32));
        parameters.Set(VolumetricFogApplyShaderKeys.FogApplyDepth, new Vector2(projection.M33, projection.M43));
        parameters.Set(VolumetricFogApplyShaderKeys.FogApplyViewInverse, viewInverse);
        parameters.Set(VolumetricFogApplyShaderKeys.FogApplyTargetSize, new Vector2(color.ViewWidth, color.ViewHeight));

        using (context.PushRenderTargetsAndRestore())
        {
            applyEffect.SetInput(0, depth);
            applyEffect.SetOutput(color);
            applyEffect.Draw(context, "VolumetricFogApply");
        }

        context.Resolver.ReleaseDepthStenctilAsShaderResource(depth);
        context.CommandList.ResourceBarrierTransition(depthStencil, BarrierLayout.DepthStencilWrite);
    }

    private static RenderLight FindSun(RenderLightCollection lights)
    {
        RenderLight sun = null;
        var brightest = 0.0f;
        if (lights == null)
            return null;
        foreach (var light in lights)
        {
            if (light.Type is not LightDirectional)
                continue;
            var luminance = ((Vector3)light.Color).LengthSquared();
            if (sun == null || luminance > brightest)
            {
                sun = light;
                brightest = luminance;
            }
        }
        return sun;
    }

    // Radiance of the environment lights scattered evenly by the fog: the light of the sky above (the lower half of a sky box is
    // usually a dark ground the terrain hides)
    private static Color3 ComputeAmbient(RenderLightCollection lights)
    {
        var ambient = new Color3();
        if (lights == null)
            return ambient;
        foreach (var light in lights)
        {
            switch (light.Type)
            {
                case LightAmbient:
                    ambient += light.Color;
                    break;
                case LightSkybox { Skybox: { } skybox } skyboxLight:
                {
                    // The diffuse lighting of a surface facing up: the cosine-weighted average of the sky above
                    // (rotated as LightSkyboxShader does)
                    var diffuse = skybox.DiffuseLightingParameters;
                    if (diffuse.Get(SkyboxKeys.Shader) is ShaderClassSource { ClassName: "SphericalHarmonicsEnvironmentColor" })
                    {
                        var skyMatrix = Matrix.Invert(Matrix.RotationQuaternion(skyboxLight.Rotation));
                        var up = Vector3.TransformNormal(Vector3.UnitY, skyMatrix);
                        up.Z = -up.Z;
                        ambient += EvaluateSphericalHarmonics(diffuse.GetValues(SphericalHarmonicsEnvironmentColorKeys.SphericalColors), Vector3.Normalize(up)) * light.Intensity;
                    }
                    break;
                }
            }
        }
        return ambient;
    }

    // The prefiltered cube map of the sky box light, blurry (x = mip level, y = intensity, z = 1 when there is one), and its rotation
    private static (Texture CubeMap, Vector4 Parameters, Matrix Rotation) FindSkyCubeMap(RenderLightCollection lights)
    {
        if (lights != null)
        {
            foreach (var light in lights)
            {
                if (light.Type is LightSkybox { Skybox: { } skybox } skyboxLight && skybox.SpecularLightingParameters.Get(SkyboxKeys.CubeMap) is { } cubeMap)
                {
                    // The prefiltered mips are blurred like rough reflections: the second one keeps the horizon from taking the
                    // color of the ground below it
                    var mip = Math.Min(1, cubeMap.MipLevelCount - 1);
                    return (cubeMap, new Vector4(mip, light.Intensity, 1, 0), Matrix.Invert(Matrix.RotationQuaternion(skyboxLight.Rotation)));
                }
            }
        }
        return (null, Vector4.Zero, Matrix.Identity);
    }

    // As SphericalHarmonicsUtils evaluates them (coefficients premultiplied by their basis constants), up to order 3
    private static Color3 EvaluateSphericalHarmonics(Color3[] coefficients, Vector3 direction)
    {
        if (coefficients.Length == 0)
            return new Color3();
        var (x, y, z) = (direction.X, direction.Y, direction.Z);
        var color = coefficients[0];
        if (coefficients.Length >= 4)
            color += coefficients[1] * y + coefficients[2] * z + coefficients[3] * x;
        if (coefficients.Length >= 9)
            color += coefficients[4] * (y * x) + coefficients[5] * (y * z) + coefficients[6] * (3 * z * z - 1) + coefficients[7] * (x * z) + coefficients[8] * (x * x - y * y);
        return new Color3(Math.Max(color.R, 0), Math.Max(color.G, 0), Math.Max(color.B, 0));
    }

    private void SetShadowParameters(ParameterCollection parameters, RenderView view, RenderLight sun)
    {
        var cascades = 0;
        Texture shadowMap = null;
        var splits = Vector4.Zero;
        var bias = 0.0f;
        if (Shadows && sun != null && shadowMapRenderer != null
            && shadowMapRenderer.FindShadowMap(view.LightingView ?? view, sun) is { ShaderData: LightDirectionalShadowMapRenderer.ShaderData data } && data.Texture != null)
        {
            cascades = Math.Min(data.CascadeSplits.Length, 4);
            for (int i = 0; i < cascades; i++)
            {
                shadowMatrices[i] = data.WorldToShadowCascadeUV[i];
                splits[i] = data.CascadeSplits[i];
            }
            shadowMap = data.Texture;
            bias = data.DepthBias;
        }
        parameters.Set(VolumetricFogScatterKeys.FogShadowCascades, cascades);
        parameters.Set(VolumetricFogScatterKeys.FogShadowSplits, splits);
        parameters.Set(VolumetricFogScatterKeys.FogShadowMatrices, shadowMatrices);
        parameters.Set(VolumetricFogScatterKeys.FogShadowBias, bias);
        // Sampled with a comparison: a depth format even when unused
        parameters.Set(VolumetricFogScatterKeys.FogShadowMap, shadowMap ?? GraphicsDevice.GetSharedDepthTexture());
    }

    private void SetHeightfieldParameters(ParameterCollection parameters, RenderFogHeightfield field, GraphicsDevice device)
    {
        var white = device.GetSharedWhiteTexture();
        if (field == null || field.Size <= 0)
        {
            parameters.Set(VolumetricFogScatterKeys.FogFieldRegion, Vector4.Zero);
            parameters.Set(VolumetricFogScatterKeys.FogFieldShadow, Vector4.Zero);
            parameters.Set(VolumetricFogScatterKeys.FogFieldHeights, Vector4.Zero);
            parameters.Set(VolumetricFogScatterKeys.FogFieldHeightmap, white);
            parameters.Set(VolumetricFogScatterKeys.FogFieldWeights, white);
            parameters.Set(VolumetricFogScatterKeys.FogFieldSunShadow, white);
            return;
        }

        parameters.Set(VolumetricFogScatterKeys.FogFieldRegion, new Vector4(field.Origin.X, field.Origin.Z, 1.0f / field.Size, 1));

        // Sample (x, z) of a texture with spacing s is at local (x, z) * s: its texel center (x + 0.5) / width
        var layerCount = field.LayerWeights != null ? Math.Clamp(field.LayerCount, 0, RenderFogHeightfield.MaxLayers) : 0;
        var heightmap = field.Heightmap;
        // A mip about as coarse as the fog layers (the heights only shape them)
        var mip = field.LayerWeights != null ? Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(1.0f, field.WeightCellSize / Math.Max(field.HeightCellSize, 1e-3f)))), 0, heightmap.MipLevelCount - 1) : 0;
        parameters.Set(VolumetricFogScatterKeys.FogFieldHeights, new Vector4(field.Origin.Y + field.MinHeight, field.HeightRange, mip, layerCount));
        parameters.Set(VolumetricFogScatterKeys.FogFieldHeightUV, new Vector4(1.0f / (field.HeightCellSize * heightmap.Width), 1.0f / (field.HeightCellSize * heightmap.Height), 0.5f / heightmap.Width, 0));
        parameters.Set(VolumetricFogScatterKeys.FogFieldHeightmap, heightmap);
        if (field.LayerWeights is { } weights)
        {
            parameters.Set(VolumetricFogScatterKeys.FogFieldWeightUV, new Vector4(1.0f / (field.WeightCellSize * weights.Width), 1.0f / (field.WeightCellSize * weights.Height), 0.5f / weights.Width, 0));
            parameters.Set(VolumetricFogScatterKeys.FogFieldWeights, weights);
        }
        else
        {
            parameters.Set(VolumetricFogScatterKeys.FogFieldWeights, white);
        }

        Span<Vector4> layerParams = stackalloc Vector4[RenderFogHeightfield.MaxLayers];
        Span<Vector4> layerAlbedo = stackalloc Vector4[RenderFogHeightfield.MaxLayers];
        Span<Vector4> layerEmissive = stackalloc Vector4[RenderFogHeightfield.MaxLayers];
        for (int i = 0; i < layerCount; i++)
        {
            var layer = field.Layers[i];
            var altitude = layer.HeightMode == FogLayerHeightMode.Altitude;
            layerParams[i] = new Vector4(layer.Density, altitude ? layer.Height + field.Origin.Y : layer.Height, Math.Max(layer.Softness, 0.01f), altitude ? 1 : 0);
            layerAlbedo[i] = new Vector4((Vector3)layer.Albedo, Math.Clamp(layer.NoiseStrength, 0, 1));
            layerEmissive[i] = new Vector4((Vector3)layer.Emissive, 0);
        }
        parameters.Set(VolumetricFogScatterKeys.FogLayerParams, layerParams.Length, ref layerParams[0]);
        parameters.Set(VolumetricFogScatterKeys.FogLayerAlbedo, layerAlbedo.Length, ref layerAlbedo[0]);
        parameters.Set(VolumetricFogScatterKeys.FogLayerEmissive, layerEmissive.Length, ref layerEmissive[0]);

        var hasShadow = Shadows && field.SunShadow != null;
        parameters.Set(VolumetricFogScatterKeys.FogFieldShadow, hasShadow ? new Vector4(field.Origin.Y + field.SunShadowMinHeight, field.SunShadowHeightRange, field.SunShadowPenumbra, 1) : Vector4.Zero);
        parameters.Set(VolumetricFogScatterKeys.FogFieldSunShadow, hasShadow ? field.SunShadow : white);
    }

    // The point and spot lights reaching into the fog grid, nearest first
    private int GatherLights(RenderLightCollection lights, RenderView view, Vector3 eye, float distance)
    {
        lightCandidates.Clear();
        if (lights == null)
            return 0;
        foreach (var light in lights)
        {
            float range;
            switch (light.Type)
            {
                case LightPoint point:
                    range = point.Radius;
                    break;
                case LightSpot spot:
                    range = spot.Range;
                    break;
                default:
                    continue;
            }
            if (range <= 0 || ((Vector3)light.Color).LengthSquared() <= 0)
                continue;
            var lightDistance = Math.Max(0, Vector3.Distance(light.Position, eye) - range);
            if (lightDistance > distance)
                continue;
            var bounds = new BoundingBoxExt(light.Position - new Vector3(range), light.Position + new Vector3(range));
            if (!view.Frustum.Contains(ref bounds))
                continue;
            lightCandidates.Add((lightDistance, light));
        }
        lightCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var count = Math.Min(lightCandidates.Count, MaxLights);
        for (int i = 0; i < count; i++)
        {
            var light = lightCandidates[i].Light;
            var b = i * LightStride;
            switch (light.Type)
            {
                case LightPoint point:
                    lightData[b] = new Vector4(light.Position, point.InvSquareRadius);
                    lightData[b + 1] = new Vector4((Vector3)light.Color, point.Radius);
                    lightData[b + 2] = new Vector4(Vector3.UnitY, 0);
                    lightData[b + 3] = new Vector4(0, 1, 0, 0);
                    break;
                case LightSpot spot:
                    lightData[b] = new Vector4(light.Position, spot.InvSquareRange);
                    lightData[b + 1] = new Vector4((Vector3)light.Color, spot.Range);
                    lightData[b + 2] = new Vector4(Vector3.Normalize(light.Direction), 0);
                    lightData[b + 3] = new Vector4(spot.LightAngleScale, spot.LightAngleOffset, 0, 0);
                    break;
            }
        }
        return count;
    }

    // The fog volumes overlapping the fog grid, nearest first
    private int GatherVolumes(List<RenderFogVolume> volumes, RenderView view, Vector3 eye, float distance)
    {
        volumeCandidates.Clear();
        if (volumes == null)
            return 0;
        foreach (var volume in volumes)
        {
            if (volume.Density <= 0)
                continue;
            var bounds = volume.Bounds;
            var closest = Vector3.Clamp(eye, bounds.Minimum, bounds.Maximum);
            var volumeDistance = Vector3.Distance(closest, eye);
            if (volumeDistance > distance)
                continue;
            var extended = new BoundingBoxExt(bounds);
            if (!view.Frustum.Contains(ref extended))
                continue;
            volumeCandidates.Add((volumeDistance, volume));
        }
        volumeCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var count = Math.Min(volumeCandidates.Count, MaxVolumes);
        for (int i = 0; i < count; i++)
        {
            var volume = volumeCandidates[i].Volume;
            var b = i * VolumeStride;
            // Rows of the world to local transform (row vectors: local = position * M)
            var m = volume.WorldToLocal;
            volumeData[b] = new Vector4(m.M11, m.M21, m.M31, m.M41);
            volumeData[b + 1] = new Vector4(m.M12, m.M22, m.M32, m.M42);
            volumeData[b + 2] = new Vector4(m.M13, m.M23, m.M33, m.M43);
            volumeData[b + 3] = new Vector4(volume.Shape == FogVolumeShape.Sphere ? 1 : 0, volume.Density, Math.Clamp(volume.EdgeFade, 0, 1), Math.Clamp(volume.NoiseStrength, 0, 1));
            volumeData[b + 4] = new Vector4((Vector3)volume.Albedo, 0);
            volumeData[b + 5] = new Vector4((Vector3)volume.Emissive, 0);
            volumeData[b + 6] = new Vector4(volume.Bounds.Minimum, 0);
            volumeData[b + 7] = new Vector4(volume.Bounds.Maximum, 0);
        }
        return count;
    }

    private void EnsureNoise(RenderDrawContext context)
    {
        if (noiseTexture != null)
            return;
        noiseTexture = Texture.New3D(GraphicsDevice, NoiseSize, NoiseSize, NoiseSize, PixelFormat.R8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        noiseTexture.Name = "VolumetricFogNoise";
        noiseEffect.Parameters.Set(VolumetricFogNoiseKeys.FogNoiseOutput, noiseTexture);
        noiseEffect.Parameters.Set(VolumetricFogNoiseKeys.FogNoiseSize, NoiseSize);
        noiseEffect.ThreadGroupCounts = new Int3(NoiseSize / 4);
        noiseEffect.Draw(context, "VolumetricFogNoise");
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

    private static float Wrap(float value) => value - MathF.Floor(value);

    private sealed class ViewState : IDisposable
    {
        public readonly Texture[] Scatter = new Texture[2];
        public Texture Integrated;
        public Int3 Size;
        public int Current;
        public bool HasHistory;
        public int FrameIndex;
        public int LastFrame;
        public Vector4 Grid;
        public Matrix PreviousViewProjection;
        public Matrix PreviousView;
        public Vector4 PreviousGrid;
        public RenderVolumetricFog Fog;
        public List<RenderFogVolume> Volumes;
        public RenderFogHeightfield Heightfield;
        public RenderLightCollection Lights;
        public RenderLight Sun;
        public Color3 Ambient;
        public VolumetricFogViewParameters Parameters;

        public void Dispose()
        {
            for (int i = 0; i < Scatter.Length; i++)
            {
                Scatter[i]?.Dispose();
                Scatter[i] = null;
            }
            Integrated?.Dispose();
            Integrated = null;
            HasHistory = false;
            Size = Int3.Zero;
        }
    }
}

/// <summary>
/// The fog of a view as its surfaces apply it (see VolumetricFogApplyBase).
/// </summary>
public struct VolumetricFogViewParameters
{
    /// <summary>
    /// The integrated froxel grid (null: only the height fog, analytically).
    /// </summary>
    public Texture Integrated;

    public Vector4 Grid;

    public Vector4 Height;

    public Vector4 Ambient;

    public Vector4 Albedo;

    public Vector4 Emissive;

    /// <summary>
    /// Light the ground reflects up onto the distant fog.
    /// </summary>
    public Vector4 Ground;

    /// <summary>
    /// The prefiltered cube map of the sky lighting the distant fog (null: <see cref="Ambient"/>).
    /// </summary>
    public Texture SkyCubeMap;

    public Vector4 Sky;

    public Matrix SkyMatrix;

    public Vector4 Sun;

    public Vector4 SunDirection;

    public Vector3 Eye;

    internal readonly void ApplyTo(ParameterCollection parameters, GraphicsDevice device)
    {
        parameters.Set(VolumetricFogApplyBaseKeys.FogIntegrated, Integrated ?? VolumetricFogRenderFeature.GetEmptyVolume(device));
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyGrid, Grid);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyHeight, Height);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyAmbient, Ambient);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyAlbedo, Albedo);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyEmissive, Emissive);
        parameters.Set(VolumetricFogApplyBaseKeys.FogSkyCubeMap, SkyCubeMap ?? VolumetricFogRenderFeature.GetEmptyCubeMap(device));
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplySky, SkyCubeMap != null ? Sky : Vector4.Zero);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplySkyMatrix, SkyMatrix);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyGround, Ground);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplySun, Sun);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplySunDirection, SunDirection);
        parameters.Set(VolumetricFogApplyBaseKeys.FogApplyEye, Eye);
    }
}

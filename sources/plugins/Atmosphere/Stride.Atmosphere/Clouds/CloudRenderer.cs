// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Atmosphere.Sky;
using Stride.Atmosphere.Weather;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Atmosphere.Clouds;

/// <summary>
/// The clouds of the atmosphere renderer: the noise textures generated on the GPU (with mips, so distant clouds filter
/// instead of aliasing), the weather map evolving over time, the cloud shadow map around the camera, and per view the
/// reduced resolution raymarch resolved over the frames.
/// </summary>
internal sealed class CloudRenderer : RendererCoreBase
{
    private const int ShapeSize = 128;
    private const int DetailSize = 32;
    private const int CirrusSize = 1024;
    private const int WeatherSize = 512;

    private static readonly Int2[] InterleaveOrder = [new(0, 0), new(1, 1), new(1, 0), new(0, 1)];

    private ComputeEffectShader noiseEffect;
    private ComputeEffectShader cirrusEffect;
    private ComputeEffectShader mip3DEffect;
    private ComputeEffectShader mip2DEffect;
    private ComputeEffectShader weatherEffect;
    private ComputeEffectShader shadowEffect;
    private ImageEffectShader raymarchEffect;
    private ImageEffectShader temporalEffect;

    private Texture shapeNoise;
    private Texture detailNoise;
    private Texture cirrus;
    private Texture weatherMap;
    private Texture shadowMap;
    private bool noiseReady;
    private (float Phase, int Seed, int Cells) weatherKey = (-1, 0, 0);

    private Vector2 windLow;
    private Vector2 windMid;
    private Vector2 windHigh;
    private float evolution;
    private double lastTime = double.NaN;

    /// <summary>
    /// Top-down transmittance of the sunlight through the clouds around the camera (see <see cref="ShadowRegion"/>).
    /// </summary>
    public Texture ShadowMap => shadowMap;

    /// <summary>
    /// xy = world XZ of the shadow map's corner, z = 1 / its size.
    /// </summary>
    public Vector3 ShadowRegion { get; private set; }

    public int ShadowMapSize { get; set; } = 512;

    public float ShadowDistance { get; set; } = 16000.0f;

    /// <summary>
    /// Debug view of the raymarch (0: none; see CloudRaymarch).
    /// </summary>
    public int Debug { get; set; }

    protected override void InitializeCore()
    {
        base.InitializeCore();
        noiseEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudNoiseGenerate", ThreadNumbers = new Int3(4, 4, 4) };
        cirrusEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudCirrusGenerate", ThreadNumbers = new Int3(8, 8, 1) };
        mip3DEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudMipGenerate3D", ThreadNumbers = new Int3(4, 4, 4) };
        mip2DEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudMipGenerate2D", ThreadNumbers = new Int3(8, 8, 1) };
        weatherEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudWeatherGenerate", ThreadNumbers = new Int3(8, 8, 1) };
        shadowEffect = new ComputeEffectShader(Context) { ShaderSourceName = "CloudShadowMap", ThreadNumbers = new Int3(8, 8, 1) };
        raymarchEffect = ToLoadAndUnload(new ImageEffectShader("CloudRaymarch"));
        temporalEffect = ToLoadAndUnload(new ImageEffectShader("CloudTemporal"));
    }

    protected override void Destroy()
    {
        shapeNoise?.Dispose();
        detailNoise?.Dispose();
        cirrus?.Dispose();
        weatherMap?.Dispose();
        shadowMap?.Dispose();
        noiseEffect?.Dispose();
        cirrusEffect?.Dispose();
        mip3DEffect?.Dispose();
        mip2DEffect?.Dispose();
        weatherEffect?.Dispose();
        shadowEffect?.Dispose();
        base.Destroy();
    }

    /// <summary>
    /// Advances the wind and the evolution of the weather, generates what is missing and updates the weather map and the
    /// shadow map.
    /// </summary>
    public void Update(RenderDrawContext context, RenderClouds clouds, WeatherState weather, double time)
    {
        EnsureTextures(context);

        // The wind, integrated: a change of wind doesn't move the clouds already in the sky
        var elapsed = double.IsNaN(lastTime) || time < lastTime || time - lastTime > 1.0 ? 0.0f : (float)(time - lastTime);
        lastTime = time;
        var direction = WindDirection(weather);
        var drift = direction * (weather.WindSpeed * elapsed);
        windLow += drift;
        windMid += drift * clouds.Middle.WindFactor;
        windHigh += drift * clouds.High.WindFactor;
        // (kept small around the tiles of the weather and the noises)
        windLow = WrapOffset(windLow, clouds.WeatherScale * 64.0f);
        windMid = WrapOffset(windMid, clouds.WeatherScale * 64.0f);
        windHigh = WrapOffset(windHigh, clouds.WeatherScale * 64.0f);
        if (clouds.EvolutionPeriod > 0)
            evolution = (evolution + elapsed / clouds.EvolutionPeriod) % 1.0f;

        // The weather map, regenerated as it evolves (in small steps: the clouds change slowly)
        var phase = MathF.Floor(evolution * 4096.0f) / 4096.0f;
        var key = (phase, clouds.Seed, clouds.CellsPerPattern);
        if (key != weatherKey)
        {
            weatherKey = key;
            var parameters = weatherEffect.Parameters;
            parameters.Set(CloudWeatherGenerateKeys.CloudWeatherOutput, weatherMap);
            parameters.Set(CloudWeatherGenerateKeys.CloudWeatherSize, WeatherSize);
            parameters.Set(CloudWeatherGenerateKeys.CloudWeatherEvolution, phase);
            parameters.Set(CloudWeatherGenerateKeys.CloudWeatherSeed, (uint)clouds.Seed);
            parameters.Set(CloudWeatherGenerateKeys.CloudWeatherCells, (float)clouds.CellsPerPattern);
            weatherEffect.ThreadGroupCounts = new Int3(WeatherSize / 8, WeatherSize / 8, 1);
            weatherEffect.Draw(context, "CloudWeather");
            context.CommandList.ResourceBarrierTransition(weatherMap, BarrierLayout.ShaderResource);
        }
    }

    /// <summary>
    /// Sets the parameters of CloudCommon (the atmosphere's already set).
    /// </summary>
    public void SetParameters(ParameterCollection parameters, RenderAtmosphere atmosphere, RenderClouds clouds, WeatherState weather, Buffer skyAmbient, Vector3 eye)
    {
        var planetRadius = atmosphere.Parameters.PlanetRadius * 1000.0f;
        parameters.Set(CloudCommonKeys.CloudShapeNoise, shapeNoise);
        parameters.Set(CloudCommonKeys.CloudDetailNoise, detailNoise);
        parameters.Set(CloudCommonKeys.CloudWeatherMap, weatherMap);
        parameters.Set(CloudCommonKeys.CloudCirrusTexture, cirrus);
        parameters.Set(CloudCommonKeys.CloudMapTexture, clouds.CloudMap ?? Context.GraphicsDevice.GetSharedWhiteTexture());
        parameters.Set(CloudCommonKeys.CloudAmbient, skyAmbient);
        // The planet's center under the camera, sea level at the world height AtmosphereSeaLevel (CloudAltitude subtracts it)
        parameters.Set(CloudCommonKeys.CloudPlanet, new Vector4(eye.X, -planetRadius, eye.Z, planetRadius));

        var low = clouds.Low;
        parameters.Set(CloudCommonKeys.CloudLowLayer, new Vector4(low.BaseAltitude, low.MaxHeight, Math.Clamp(weather.CloudCoverage, 0, 1), Math.Clamp(weather.CloudType, 0, 1)));
        parameters.Set(CloudCommonKeys.CloudLowShape, new Vector4(low.Density, 1.0f / Math.Max(low.ShapeScale, 1), 1.0f / Math.Max(low.DetailScale, 1), Math.Clamp(low.Erosion, 0, 1)));
        var mid = clouds.Middle;
        parameters.Set(CloudCommonKeys.CloudMidLayer, new Vector4(mid.BaseAltitude, Math.Max(mid.Thickness, 1), Math.Clamp(weather.MidCloudCoverage, 0, 1), Math.Clamp(weather.MidCloudType, 0, 1)));
        parameters.Set(CloudCommonKeys.CloudMidShape, new Vector4(mid.Density, 1.0f / Math.Max(mid.ShapeScale, 1), 1.0f / Math.Max(mid.DetailScale, 1), Math.Clamp(mid.Erosion, 0, 1)));
        var high = clouds.High;
        parameters.Set(CloudCommonKeys.CloudHighLayer, new Vector4(high.Altitude, Math.Clamp(weather.HighCloudCoverage, 0, 1), Math.Clamp(weather.HighCloudType, 0, 1), high.Opacity));
        var direction = WindDirection(weather);
        parameters.Set(CloudCommonKeys.CloudHighShape, new Vector4(1.0f / Math.Max(high.StreakLength, 1), 1.0f / Math.Max(high.StreakWidth, 1), direction.X, direction.Y));
        parameters.Set(CloudCommonKeys.CloudWindLow, new Vector4(-windLow.X, -windLow.Y, -windMid.X, -windMid.Y));
        parameters.Set(CloudCommonKeys.CloudWindHigh, new Vector4(-windHigh.X, -windHigh.Y, evolution * 600.0f, low.WindShear));
        parameters.Set(CloudCommonKeys.CloudWeather, new Vector4(1.0f / Math.Max(clouds.WeatherScale, 1), Math.Clamp(low.TypeVariation, 0, 1), Math.Clamp(weather.Precipitation, 0, 1), 1));
        parameters.Set(CloudCommonKeys.CloudMapRegion, clouds.CloudMap != null
            ? new Vector4(clouds.CloudMapCorner.X, clouds.CloudMapCorner.Y, 1.0f / Math.Max(clouds.CloudMapSize, 1), clouds.CloudMapStrength)
            : Vector4.Zero);
        parameters.Set(CloudCommonKeys.CloudPhase, DropletPhase(clouds.DropletSize));
        parameters.Set(CloudCommonKeys.CloudScattering, new Vector4(clouds.SilverLining, Math.Clamp(clouds.MultipleScattering, 0, 1), clouds.AmbientLight, Math.Clamp(clouds.DarkEdges, 0, 1)));

        var (lightDirection, lightIlluminance) = CloudLight(atmosphere);
        parameters.Set(CloudCommonKeys.CloudLightDirection, lightDirection);
        parameters.Set(CloudCommonKeys.CloudLightIlluminance, (Vector3)lightIlluminance);
    }

    /// <summary>
    /// Places the cloud shadow map of this frame around the camera (before the surfaces' shadow parameters are set) and
    /// allocates it. Returns it.
    /// </summary>
    public Texture PlaceShadowMap(Vector3 eye)
    {
        var size = Math.Clamp(ShadowMapSize, 64, 2048);
        if (shadowMap == null || shadowMap.Width != size)
        {
            shadowMap?.Dispose();
            shadowMap = Texture.New2D(Context.GraphicsDevice, size, size, PixelFormat.R16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            shadowMap.Name = "CloudShadowMap";
        }

        // Snapped to its texels: no shimmering as the camera moves
        var extent = Math.Max(ShadowDistance, 100.0f);
        var texel = extent / size;
        var cornerX = MathF.Floor((eye.X - extent * 0.5f) / texel) * texel;
        var cornerZ = MathF.Floor((eye.Z - extent * 0.5f) / texel) * texel;
        ShadowRegion = new Vector3(cornerX, cornerZ, 1.0f / extent);
        return shadowMap;
    }

    /// <summary>
    /// Draws the cloud shadow map where it was placed (CloudCommon's parameters set by <paramref name="setParameters"/>).
    /// </summary>
    public void DrawShadowMap(RenderDrawContext context, Action<ParameterCollection> setParameters, Vector3 eye)
    {
        if (shadowMap == null)
            return;
        var size = shadowMap.Width;
        var extent = 1.0f / ShadowRegion.Z;

        var parameters = shadowEffect.Parameters;
        setParameters(parameters);
        parameters.Set(CloudShadowMapKeys.CloudShadowOutput, shadowMap);
        parameters.Set(CloudShadowMapKeys.CloudShadowRect, new Vector4(ShadowRegion.X, ShadowRegion.Y, extent, size));
        parameters.Set(CloudShadowMapKeys.CloudShadowEye, eye);
        shadowEffect.ThreadGroupCounts = new Int3((size + 7) / 8, (size + 7) / 8, 1);
        shadowEffect.Draw(context, "CloudShadowMap");
        context.CommandList.ResourceBarrierTransition(shadowMap, BarrierLayout.ShaderResource);
    }

    /// <summary>
    /// Raymarches the clouds of a view (interleaved) and resolves them with the history of the view.
    /// </summary>
    public void DrawView(RenderDrawContext context, CloudViewState state, Action<ParameterCollection> setParameters, Texture depth, Int2 targetSize, Matrix viewProjection, Vector3 eye, float verticalFieldOfView,
        int resolutionDivisor, float steps, int lightSteps, float maxDistance, float blend)
    {
        var width = Math.Max(1, targetSize.X / resolutionDivisor);
        var height = Math.Max(1, targetSize.Y / resolutionDivisor);
        state.EnsureSize(Context.GraphicsDevice, width, height);
        state.Frame++;

        Matrix.Invert(ref viewProjection, out var inverseViewProjection);
        var interleave = InterleaveOrder[state.Frame & 3];
        var marchWidth = (width + 1) / 2;
        var marchHeight = (height + 1) / 2;
        var current = Context.Allocator.GetTemporaryTexture2D(marchWidth, marchHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);

        var parameters = raymarchEffect.Parameters;
        setParameters(parameters);
        parameters.Set(CloudRaymarchKeys.CloudInvViewProjection, inverseViewProjection);
        parameters.Set(CloudRaymarchKeys.CloudEye, eye);
        // (the jitter advances once per round of the interleave: each pixel sees it consecutively)
        parameters.Set(CloudRaymarchKeys.CloudFrame, (float)((state.Frame >> 2) % 4096));
        parameters.Set(CloudRaymarchKeys.CloudSteps, steps);
        parameters.Set(CloudRaymarchKeys.CloudLightSteps, lightSteps);
        parameters.Set(CloudRaymarchKeys.CloudMaxDistance, maxDistance);
        parameters.Set(CloudRaymarchKeys.CloudPixelAngle, verticalFieldOfView / height);
        parameters.Set(CloudRaymarchKeys.CloudSceneDepth, depth);
        parameters.Set(CloudRaymarchKeys.CloudResolution, new Vector2(width, height));
        parameters.Set(CloudRaymarchKeys.CloudInterleave, new Vector2(interleave.X, interleave.Y));
        parameters.Set(CloudRaymarchKeys.CloudDebug, Debug);
        raymarchEffect.SetOutput(current);
        raymarchEffect.Draw(context, "CloudRaymarch");

        var output = state.History[state.HistoryIndex];
        var previous = state.History[1 - state.HistoryIndex];
        var temporal = temporalEffect.Parameters;
        temporal.Set(CloudTemporalKeys.CloudInvViewProjection, inverseViewProjection);
        temporal.Set(CloudTemporalKeys.CloudPreviousViewProjection, state.HistoryValid ? state.ResolvedViewProjection : viewProjection);
        temporal.Set(CloudTemporalKeys.CloudEye, eye);
        temporal.Set(CloudTemporalKeys.CloudTexel, new Vector2(1.0f / marchWidth, 1.0f / marchHeight));
        temporal.Set(CloudTemporalKeys.CloudResolution, new Vector2(width, height));
        temporal.Set(CloudTemporalKeys.CloudInterleave, new Vector2(interleave.X, interleave.Y));
        temporal.Set(CloudTemporalKeys.CloudHistoryValid, state.HistoryValid ? 1.0f : 0.0f);
        temporal.Set(CloudTemporalKeys.CloudBlend, Math.Clamp(blend, 0.01f, 1.0f));
        temporalEffect.SetInput(0, current);
        temporalEffect.SetInput(1, previous);
        temporalEffect.SetOutput(output);
        temporalEffect.Draw(context, "CloudTemporal");
        Context.Allocator.ReleaseReference(current);

        state.HistoryIndex = 1 - state.HistoryIndex;
        state.HistoryValid = true;
        state.ResolvedViewProjection = viewProjection;
        state.Resolved = output;
    }

    /// <summary>
    /// The light of the clouds: the sun, or the moon once the sun is well below the horizon.
    /// </summary>
    public static (Vector3 Direction, Color3 Illuminance) CloudLight(RenderAtmosphere atmosphere)
    {
        // (as the atmosphere's key light: the shadows of the clouds fall where the light comes from)
        var useMoon = atmosphere.SunDirection.Y < -0.06f && atmosphere.MoonIlluminance.R > 0;
        return useMoon ? (atmosphere.MoonDirection, atmosphere.MoonIlluminance) : (atmosphere.SunDirection, atmosphere.SunIlluminance);
    }

    private static Vector2 WindDirection(WeatherState weather)
    {
        var angle = MathUtil.DegreesToRadians(weather.WindDirection);
        // Clockwise from +X seen from above (+Y up, -Z forward): towards +Z is clockwise
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private static Vector2 WrapOffset(Vector2 offset, float period)
    {
        return new Vector2(offset.X - MathF.Floor(offset.X / period) * period, offset.Y - MathF.Floor(offset.Y / period) * period);
    }

    // The phase function of cloud droplets of a diameter (micrometers): a Henyey-Greenstein and a Draine lobe fitted to Mie
    // theory (Jendersie and d'Eon 2023, "An Approximate Mie Scattering Function for Fog and Cloud Rendering")
    private static Vector4 DropletPhase(float diameter)
    {
        var d = Math.Clamp(diameter, 5.0f, 50.0f);
        var gHg = MathF.Exp(-0.0990567f / (d - 1.67154f));
        var gD = MathF.Exp(-2.20679f / (d + 3.91029f) - 0.428934f);
        var alpha = MathF.Exp(3.62489f - 8.29288f / (d + 5.52825f));
        var weight = MathF.Exp(-0.599085f / (d - 0.641583f) - 0.665888f);
        return new Vector4(gHg, gD, alpha, weight);
    }

    private void EnsureTextures(RenderDrawContext context)
    {
        var device = Context.GraphicsDevice;
        if (weatherMap == null)
        {
            weatherMap = Texture.New2D(device, WeatherSize, WeatherSize, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            weatherMap.Name = "CloudWeatherMap";
        }
        if (noiseReady)
            return;

        shapeNoise = Texture.New3D(device, ShapeSize, ShapeSize, ShapeSize, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        shapeNoise.Name = "CloudShapeNoise";
        detailNoise = Texture.New3D(device, DetailSize, DetailSize, DetailSize, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        detailNoise.Name = "CloudDetailNoise";
        cirrus = Texture.New2D(device, CirrusSize, CirrusSize, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        cirrus.Name = "CloudCirrus";

        GenerateVolume(context, shapeNoise, ShapeSize, 0);
        GenerateVolume(context, detailNoise, DetailSize, 1);

        using (var top = cirrus.ToTextureView(ViewType.Single, 0, 0))
        {
            cirrusEffect.Parameters.Set(CloudCirrusGenerateKeys.CloudCirrusOutput, top);
            cirrusEffect.Parameters.Set(CloudCirrusGenerateKeys.CloudCirrusSize, CirrusSize);
            cirrusEffect.ThreadGroupCounts = new Int3(CirrusSize / 8, CirrusSize / 8, 1);
            cirrusEffect.Draw(context, "CloudCirrus");
        }
        for (int mip = 1; mip < cirrus.MipLevelCount; mip++)
        {
            using var source = cirrus.ToTextureView(ViewType.Single, 0, mip - 1);
            using var target = cirrus.ToTextureView(ViewType.Single, 0, mip);
            var size = Math.Max(1, CirrusSize >> mip);
            mip2DEffect.Parameters.Set(CloudMipGenerate2DKeys.CloudMipSource2D, source);
            mip2DEffect.Parameters.Set(CloudMipGenerate2DKeys.CloudMipOutput2D, target);
            mip2DEffect.Parameters.Set(CloudMipGenerate2DKeys.CloudMipSize2D, new Int2(size, size));
            mip2DEffect.ThreadGroupCounts = new Int3((size + 7) / 8, (size + 7) / 8, 1);
            mip2DEffect.Draw(context, "CloudCirrusMip");
        }

        var commandList = context.CommandList;
        commandList.ResourceBarrierTransition(shapeNoise, BarrierLayout.ShaderResource);
        commandList.ResourceBarrierTransition(detailNoise, BarrierLayout.ShaderResource);
        commandList.ResourceBarrierTransition(cirrus, BarrierLayout.ShaderResource);
        noiseReady = true;
    }

    private void GenerateVolume(RenderDrawContext context, Texture volume, int size, int mode)
    {
        using (var top = volume.ToTextureView(ViewType.Single, 0, 0))
        {
            noiseEffect.Parameters.Set(CloudNoiseGenerateKeys.CloudNoiseOutput, top);
            noiseEffect.Parameters.Set(CloudNoiseGenerateKeys.CloudNoiseSize, size);
            noiseEffect.Parameters.Set(CloudNoiseGenerateKeys.CloudNoiseMode, mode);
            noiseEffect.ThreadGroupCounts = new Int3(size / 4, size / 4, size / 4);
            noiseEffect.Draw(context, "CloudNoise");
        }
        for (int mip = 1; mip < volume.MipLevelCount; mip++)
        {
            using var source = volume.ToTextureView(ViewType.Single, 0, mip - 1);
            using var target = volume.ToTextureView(ViewType.Single, 0, mip);
            var mipSize = Math.Max(1, size >> mip);
            mip3DEffect.Parameters.Set(CloudMipGenerate3DKeys.CloudMipSource3D, source);
            mip3DEffect.Parameters.Set(CloudMipGenerate3DKeys.CloudMipOutput3D, target);
            mip3DEffect.Parameters.Set(CloudMipGenerate3DKeys.CloudMipSize3D, new Int3(mipSize));
            mip3DEffect.ThreadGroupCounts = new Int3((mipSize + 3) / 4, (mipSize + 3) / 4, (mipSize + 3) / 4);
            mip3DEffect.Draw(context, "CloudNoiseMip");
        }
    }
}

/// <summary>
/// The clouds of a view: their history and the view projection it was resolved with.
/// </summary>
internal sealed class CloudViewState : IDisposable
{
    public readonly Texture[] History = new Texture[2];
    public int HistoryIndex;
    public bool HistoryValid;
    public Matrix ResolvedViewProjection;
    public int Frame;

    /// <summary>
    /// The resolved clouds of the last frame (rgb light, a transmittance, -1 where missing).
    /// </summary>
    public Texture Resolved;

    public void EnsureSize(GraphicsDevice device, int width, int height)
    {
        if (History[0] != null && History[0].Width == width && History[0].Height == height)
            return;
        Dispose();
        for (int i = 0; i < 2; i++)
        {
            History[i] = Texture.New2D(device, width, height, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            History[i].Name = "CloudHistory";
        }
        HistoryValid = false;
    }

    public void Dispose()
    {
        for (int i = 0; i < 2; i++)
        {
            History[i]?.Dispose();
            History[i] = null;
        }
        Resolved = null;
        HistoryValid = false;
    }
}

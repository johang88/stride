// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;
using Stride.Rendering.Skyboxes;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// The sky of the atmosphere renderer: the look-up tables of the atmosphere, the light around the clouds, the aerial
/// perspective volumes of the views, and the capture of the sky (with its clouds) that lights the scene every few frames:
/// a cube with its mips for the reflections and spherical harmonics for the diffuse light, published together in the
/// scene's sky light. Everything stays on the GPU.
/// </summary>
internal sealed class SkyRenderer : RendererCoreBase
{
    public const int TransmittanceWidth = 256;
    public const int TransmittanceHeight = 64;
    public const int MultiScatterSize = 32;
    public const int SkyViewWidth = 256;
    public const int SkyViewHeight = 144;
    public const int VolumeSize = 32;

    // The atmosphere camera never goes below this altitude (km): float precision at the horizon
    private const float MinimumCameraAltitude = 0.02f;

    private ImageEffectShader transmittanceEffect;
    private ImageEffectShader multiScatterEffect;
    private ImageEffectShader skyViewEffect;
    private ImageEffectShader captureEffect;
    private ComputeEffectShader ambientEffect;
    private ComputeEffectShader cameraVolumeEffect;
    private ComputeEffectShader harmonicsEffect;
    private ImageScaler downsampler;

    private Texture captureCube;
    private Texture[,] captureFaces;
    private Texture captureAtlas;
    private Texture publishedCube;
    private int captureSize;
    private int captureFrame;
    private Buffer previousHarmonics;
    private bool hasHarmonics;
    private Skybox publishedSkybox;

    private bool lutsValid;
    private (float, float, Vector3, float, float, float, float, float, Vector3, Color3) lutKey;

    public Texture TransmittanceLut { get; private set; }

    public Texture MultiScatterLut { get; private set; }

    public Texture SkyViewLut { get; private set; }

    /// <summary>
    /// [0] = radiance of the clear sky over the upper hemisphere, [1] = of the ground (see SkyAmbient).
    /// </summary>
    public Buffer Ambient { get; private set; }

    /// <summary>
    /// Spherical harmonics and averages of the captured sky (see SkySphericalHarmonics).
    /// </summary>
    public Buffer Harmonics { get; private set; }

    /// <summary>
    /// The prefiltered cube of the captured sky, once a capture is complete.
    /// </summary>
    public Texture SkyCube => publishedSkybox != null ? publishedCube : null;

    /// <summary>
    /// The camera in atmosphere space (km) this frame.
    /// </summary>
    public Vector3 AtmosphereCamera { get; private set; }

    /// <summary>
    /// Size of the sky capture's faces.
    /// </summary>
    public int CaptureSize { get; set; } = 64;

    /// <summary>
    /// Frames between two captures of the sky.
    /// </summary>
    public int CaptureInterval { get; set; } = 4;

    /// <summary>
    /// Kilometers covered by one slice of the aerial perspective volume (32 slices, distributed quadratically).
    /// </summary>
    public float AerialPerspectiveKmPerSlice { get; set; } = 2.0f;

    protected override void InitializeCore()
    {
        base.InitializeCore();
        var device = Context.GraphicsDevice;
        TransmittanceLut = Texture.New2D(device, TransmittanceWidth, TransmittanceHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        TransmittanceLut.Name = "AtmosphereTransmittance";
        MultiScatterLut = Texture.New2D(device, MultiScatterSize, MultiScatterSize, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        MultiScatterLut.Name = "AtmosphereMultiScatter";
        SkyViewLut = Texture.New2D(device, SkyViewWidth, SkyViewHeight, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        SkyViewLut.Name = "AtmosphereSkyView";
        Ambient = Buffer.New(device, 2 * 16, 0, BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32G32B32A32_Float);
        Ambient.Name = "AtmosphereAmbient";
        Harmonics = Buffer.New(device, 12 * 16, 0, BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32G32B32A32_Float);
        Harmonics.Name = "AtmosphereSkyHarmonics";
        previousHarmonics = Buffer.New(device, 12 * 16, 0, BufferFlags.ShaderResource, PixelFormat.R32G32B32A32_Float);
        previousHarmonics.Name = "AtmosphereSkyHarmonicsPrevious";

        transmittanceEffect = ToLoadAndUnload(new ImageEffectShader("AtmosphereTransmittanceLut"));
        multiScatterEffect = ToLoadAndUnload(new ImageEffectShader("AtmosphereMultiScatterLut"));
        skyViewEffect = ToLoadAndUnload(new ImageEffectShader("AtmosphereSkyViewLut"));
        captureEffect = ToLoadAndUnload(new ImageEffectShader("SkyCubeCapture"));
        downsampler = ToLoadAndUnload(new ImageScaler(SamplingPattern.Linear));
        ambientEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkyAmbient", ThreadNumbers = new Int3(64, 1, 1), ThreadGroupCounts = new Int3(1, 1, 1) };
        cameraVolumeEffect = new ComputeEffectShader(Context) { ShaderSourceName = "AtmosphereCameraVolume", ThreadNumbers = new Int3(8, 8, 1), ThreadGroupCounts = new Int3(VolumeSize / 8, VolumeSize / 8, VolumeSize) };
        harmonicsEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkySphericalHarmonics", ThreadNumbers = new Int3(128, 1, 1), ThreadGroupCounts = new Int3(1, 1, 1) };
    }

    protected override void Destroy()
    {
        DisposeCapture();
        TransmittanceLut?.Dispose();
        MultiScatterLut?.Dispose();
        SkyViewLut?.Dispose();
        Ambient?.Dispose();
        Harmonics?.Dispose();
        previousHarmonics?.Dispose();
        ambientEffect?.Dispose();
        cameraVolumeEffect?.Dispose();
        harmonicsEffect?.Dispose();
        base.Destroy();
    }

    /// <summary>
    /// Sets the atmosphere and sky parameters of AtmosphereCommon and SkyCommon.
    /// </summary>
    public void SetParameters(ParameterCollection parameters, RenderAtmosphere atmosphere, float haze, float pixelAngle = 0.0f)
    {
        var a = atmosphere.Parameters;
        parameters.Set(AtmosphereCommonKeys.AtmosphereBottomRadius, a.PlanetRadius);
        parameters.Set(AtmosphereCommonKeys.AtmosphereTopRadius, a.PlanetRadius + a.Height);
        parameters.Set(AtmosphereCommonKeys.AtmosphereRayleighScattering, a.RayleighScattering);
        parameters.Set(AtmosphereCommonKeys.AtmosphereRayleighDensityExpScale, -1.0f / a.RayleighScaleHeight);
        var mie = a.MieScattering * haze;
        parameters.Set(AtmosphereCommonKeys.AtmosphereMieScattering, new Vector3(mie));
        parameters.Set(AtmosphereCommonKeys.AtmosphereMieExtinction, new Vector3(mie + a.MieAbsorption));
        parameters.Set(AtmosphereCommonKeys.AtmosphereMieDensityExpScale, -1.0f / a.MieScaleHeight);
        parameters.Set(AtmosphereCommonKeys.AtmosphereMiePhaseG, a.MieAnisotropy);
        parameters.Set(AtmosphereCommonKeys.AtmosphereAbsorptionExtinction, a.OzoneAbsorption);
        parameters.Set(AtmosphereCommonKeys.AtmosphereGroundAlbedo, (Vector3)a.GroundAlbedo);
        parameters.Set(AtmosphereCommonKeys.AtmosphereCameraPosition, AtmosphereCamera);
        parameters.Set(AtmosphereCommonKeys.AtmosphereSeaLevel, atmosphere.SeaLevel);
        parameters.Set(AtmosphereCommonKeys.AtmosphereSunDirection, atmosphere.SunDirection);
        parameters.Set(AtmosphereCommonKeys.AtmosphereSunIlluminance, (Vector3)atmosphere.SunIlluminance);
        parameters.Set(AtmosphereCommonKeys.AtmosphereMoonDirection, atmosphere.MoonDirection);
        parameters.Set(AtmosphereCommonKeys.AtmosphereMoonIlluminance, (Vector3)atmosphere.MoonIlluminance);
        parameters.Set(AtmosphereCommonKeys.AtmosphereTransmittanceLut, TransmittanceLut);
        parameters.Set(AtmosphereCommonKeys.AtmosphereMultiScatterLut, MultiScatterLut);
        parameters.Set(SkyCommonKeys.SkyViewLut, SkyViewLut);

        // Sun disk: its illuminance over its solid angle, capped so half floats and the bloom stay sane
        var sunRadius = atmosphere.SunDiskSize * 0.5f;
        var sunDisk = (Vector3)atmosphere.SunIlluminance * (atmosphere.SunDiskIntensity / (MathF.PI * sunRadius * sunRadius));
        var sunPeak = MathF.Max(sunDisk.X, MathF.Max(sunDisk.Y, sunDisk.Z));
        if (sunPeak > 30000.0f)
            sunDisk *= 30000.0f / sunPeak;
        parameters.Set(SkyCommonKeys.SunAngularRadius, sunRadius);
        parameters.Set(SkyCommonKeys.SunDiskLuminance, sunDisk);

        // Moon disk: as bright as a screen allows while keeping its maria (the real one is blinding next to a moonlit scene)
        var moonIlluminance = (Vector3)atmosphere.MoonIlluminance;
        var moonDisk = moonIlluminance.LengthSquared() > 0 ? Vector3.Normalize(moonIlluminance) * (((Vector3)atmosphere.SunIlluminance).Length() * 0.03f * 70.0f) : Vector3.Zero;
        parameters.Set(SkyCommonKeys.MoonAngularRadius, atmosphere.MoonDiskSize * 0.5f);
        parameters.Set(SkyCommonKeys.MoonDiskLuminance, moonDisk);

        // The night: stars and the glow of the moonless sky
        var night = atmosphere.Night;
        var fullMoon = ((Vector3)atmosphere.SunIlluminance).Length() * 0.03f;
        parameters.Set(SkyCommonKeys.NightSkyGlow, new Vector3(0.55f, 0.75f, 1.0f) * (fullMoon * 0.05f * atmosphere.NightSkyBrightness * night));
        parameters.Set(SkyCommonKeys.StarIntensity, night * night * 0.5f * atmosphere.StarBrightness);
        parameters.Set(SkyCommonKeys.StarAngularSize, pixelAngle);
        parameters.Set(SkyCommonKeys.StarRotation, atmosphere.StarRotation);
        parameters.Set(SkyCommonKeys.SkyTime, (float)(Context.Time?.Total.TotalSeconds % 3600.0 ?? 0.0));
    }

    /// <summary>
    /// Updates the look-up tables (the transmittance and multiple scattering when the atmosphere changes, the sky view every
    /// frame) and the light around the clouds, for a camera at a world position.
    /// </summary>
    public void Update(RenderDrawContext context, RenderAtmosphere atmosphere, float haze, Vector3 eye)
    {
        var altitude = MathF.Max((eye.Y - atmosphere.SeaLevel) * 0.001f, MinimumCameraAltitude);
        AtmosphereCamera = new Vector3(0, atmosphere.Parameters.PlanetRadius + altitude, 0);

        var a = atmosphere.Parameters;
        var key = (a.PlanetRadius, a.Height, a.RayleighScattering, a.RayleighScaleHeight, a.MieScattering * haze, a.MieAbsorption, a.MieScaleHeight, a.MieAnisotropy, a.OzoneAbsorption, a.GroundAlbedo);
        if (!lutsValid || key != lutKey)
        {
            lutsValid = true;
            lutKey = key;
            SetParameters(transmittanceEffect.Parameters, atmosphere, haze);
            transmittanceEffect.SetOutput(TransmittanceLut);
            transmittanceEffect.Draw(context, "AtmosphereTransmittance");
            SetParameters(multiScatterEffect.Parameters, atmosphere, haze);
            multiScatterEffect.SetOutput(MultiScatterLut);
            multiScatterEffect.Draw(context, "AtmosphereMultiScatter");
        }

        SetParameters(skyViewEffect.Parameters, atmosphere, haze);
        skyViewEffect.SetOutput(SkyViewLut);
        skyViewEffect.Draw(context, "AtmosphereSkyView");
        // (the tables are read by compute passes next: none may stay bound as a target)
        context.CommandList.ResetTargets();

        SetParameters(ambientEffect.Parameters, atmosphere, haze);
        ambientEffect.Parameters.Set(SkyAmbientKeys.SkyAmbientOutput, Ambient);
        ambientEffect.Draw(context, "SkyAmbient");
    }

    /// <summary>
    /// Computes the aerial perspective volume of a view.
    /// </summary>
    public void DrawCameraVolume(RenderDrawContext context, RenderAtmosphere atmosphere, float haze, Texture volume, Matrix inverseViewProjection, Vector3 eye)
    {
        var parameters = cameraVolumeEffect.Parameters;
        SetParameters(parameters, atmosphere, haze);
        parameters.Set(AtmosphereCameraVolumeKeys.CameraVolumeOutput, volume);
        parameters.Set(AtmosphereCameraVolumeKeys.CameraVolumeInvViewProjection, inverseViewProjection);
        parameters.Set(AtmosphereCameraVolumeKeys.CameraVolumeEye, eye);
        parameters.Set(AtmosphereCameraVolumeKeys.AerialPerspectiveKmPerSlice, AerialPerspectiveKmPerSlice);
        cameraVolumeEffect.Draw(context, "AtmosphereCameraVolume");
        context.CommandList.ResourceBarrierTransition(volume, BarrierLayout.ShaderResource);
    }

    /// <summary>
    /// Captures the sky when due (every <see cref="CaptureInterval"/> frames; the clouds' parameters set by
    /// <paramref name="setCloudParameters"/>): all faces in one pass, their mips, the spherical harmonics, published together
    /// in the atmosphere's sky light.
    /// </summary>
    public void Capture(RenderDrawContext context, RenderAtmosphere atmosphere, float haze, Vector3 eye, Action<ParameterCollection> setCloudParameters)
    {
        EnsureCapture();
        if (captureFrame++ % Math.Max(CaptureInterval, 1) != 0 && publishedSkybox == atmosphere.Skybox)
            return;

        var parameters = captureEffect.Parameters;
        SetParameters(parameters, atmosphere, haze);
        var clouds = setCloudParameters != null;
        setCloudParameters?.Invoke(parameters);
        parameters.Set(SkyCubeCaptureKeys.CaptureClouds, clouds ? 1 : 0);
        parameters.Set(SkyCubeCaptureKeys.CaptureOrigin, eye);
        parameters.Set(SkyCubeCaptureKeys.CaptureFrame, (float)(captureFrame % 1024));
        captureEffect.SetOutput(captureAtlas);
        captureEffect.Draw(context, "SkyCubeCapture");

        using var _ = context.QueryManager.BeginProfile(Color.SkyBlue, AtmosphereRenderer.SkyFilterKey);
        var commandList = context.CommandList;
        commandList.ResetTargets();
        // The faces into the cube, then its mips (a box filter: the blurry levels stand for the rough reflections of a sky
        // that has no sharp detail to lose)
        for (int face = 0; face < 6; face++)
        {
            var region = new ResourceRegion(face * captureSize, 0, 0, (face + 1) * captureSize, captureSize, 1);
            commandList.CopyRegion(captureAtlas, 0, region, captureCube, face * captureCube.MipLevelCount);
        }
        for (int mip = 1; mip < captureCube.MipLevelCount; mip++)
        {
            for (int face = 0; face < 6; face++)
            {
                downsampler.SetInput(captureFaces[face, mip - 1]);
                downsampler.SetOutput(captureFaces[face, mip]);
                downsampler.Draw(context, "SkyCaptureMip");
            }
        }
        commandList.ResetTargets();
        commandList.Copy(captureCube, publishedCube);
        commandList.ResourceBarrierTransition(publishedCube, BarrierLayout.ShaderResource);
        commandList.ResourceBarrierTransition(captureCube, BarrierLayout.ShaderResource);

        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyCaptureCube, captureCube);
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyCaptureMip, (float)Math.Max(0, (int)MathF.Log2(captureSize / 16.0f)));
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsOutput, Harmonics);
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsPrevious, previousHarmonics);
        // (about a quarter of a second to settle at the default interval)
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsBlend, hasHarmonics ? 0.35f : 1.0f);
        harmonicsEffect.Draw(context, "SkySphericalHarmonics");
        commandList.Copy(Harmonics, previousHarmonics);
        hasHarmonics = true;

        // The scene's sky light: set once, the textures it points at are updated in place
        var skybox = atmosphere.Skybox;
        if (publishedSkybox != skybox)
        {
            publishedSkybox = skybox;
            skybox.DiffuseLightingParameters.Set(SkyboxKeys.Shader, new Stride.Shaders.ShaderClassSource("SphericalHarmonicsBufferEnvironmentColor"));
            skybox.DiffuseLightingParameters.Set(SphericalHarmonicsBufferEnvironmentColorKeys.SphericalHarmonicsBuffer, Harmonics);
            skybox.SpecularLightingParameters.Set(SkyboxKeys.Shader, new Stride.Shaders.ShaderClassSource("RoughnessCubeMapEnvironmentColor"));
            skybox.SpecularLightingParameters.Set(SkyboxKeys.CubeMap, publishedCube);
        }
    }

    private void EnsureCapture()
    {
        var size = Math.Clamp(MathUtil.NextPowerOfTwo(CaptureSize), 32, 512);
        if (captureCube != null && captureSize == size)
            return;
        DisposeCapture();
        captureSize = size;
        var device = Context.GraphicsDevice;
        captureCube = Texture.NewCube(device, size, MipMapCount.Auto, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        captureCube.Name = "AtmosphereSkyCapture";
        captureFaces = new Texture[6, captureCube.MipLevelCount];
        for (int face = 0; face < 6; face++)
        {
            for (int mip = 0; mip < captureCube.MipLevelCount; mip++)
                captureFaces[face, mip] = captureCube.ToTextureView(ViewType.Single, face, mip);
        }
        captureAtlas = Texture.New2D(device, size * 6, size, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        captureAtlas.Name = "AtmosphereSkyCaptureFaces";
        publishedCube = Texture.NewCube(device, size, MipMapCount.Auto, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource);
        publishedCube.Name = "AtmosphereSkyLight";
        publishedSkybox = null;
    }

    private void DisposeCapture()
    {
        if (captureFaces != null)
        {
            foreach (var view in captureFaces)
                view?.Dispose();
            captureFaces = null;
        }
        captureCube?.Dispose();
        captureCube = null;
        captureAtlas?.Dispose();
        captureAtlas = null;
        if (publishedSkybox != null)
        {
            publishedSkybox.SpecularLightingParameters.Set(SkyboxKeys.CubeMap, null);
            publishedSkybox = null;
        }
        publishedCube?.Dispose();
        publishedCube = null;
    }
}

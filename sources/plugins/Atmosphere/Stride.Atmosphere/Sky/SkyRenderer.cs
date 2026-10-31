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
    private ComputeEffectShader prefilterEffect;
    private ComputeEffectShader downsampleEffect;

    private Texture captureCube;
    private Texture[] captureMips;
    private Texture captureAtlas;
    // (the capture's faces are averaged over the captures: which were captured yet; the step of the capture this frame)
    private readonly bool[] facesCaptured = new bool[6];
    private int captureStep;
    private int captureIndex;
    private Texture publishedCube;
    private Texture[] publishedMips;
    private int captureSize;
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
    /// Samples per texel of the reflection cube's prefilter (each reads the capture's mip covering its share of the lobe).
    /// </summary>
    public int PrefilterSamples { get; set; } = 32;

    /// <summary>
    /// Weight of a capture in the running average of the captures (each samples other points of the sky's texels).
    /// </summary>
    public float CaptureBlend { get; set; } = 0.25f;

    /// <summary>
    /// Faces of the sky captured per frame (1 to 6): the capture takes that many frames for the faces, one for the cube's
    /// mips and spherical harmonics and one for the reflection levels, the same cost every frame.
    /// </summary>
    public int CaptureFacesPerFrame { get; set; } = 1;

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
        // (blended into the average of the previous captures: the weight of this one in alpha)
        captureEffect = ToLoadAndUnload(new ImageEffectShader("SkyCubeCapture") { BlendState = new BlendStateDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha) });
        downsampleEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkyCubeDownsample", ThreadNumbers = new Int3(8, 8, 1) };
        ambientEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkyAmbient", ThreadNumbers = new Int3(64, 1, 1), ThreadGroupCounts = new Int3(1, 1, 1) };
        cameraVolumeEffect = new ComputeEffectShader(Context) { ShaderSourceName = "AtmosphereCameraVolume", ThreadNumbers = new Int3(8, 8, 1), ThreadGroupCounts = new Int3(VolumeSize / 8, VolumeSize / 8, VolumeSize) };
        harmonicsEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkySphericalHarmonics", ThreadNumbers = new Int3(128, 1, 1), ThreadGroupCounts = new Int3(1, 1, 1) };
        prefilterEffect = new ComputeEffectShader(Context) { ShaderSourceName = "SkyPrefilter", ThreadNumbers = new Int3(8, 8, 1) };
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
        prefilterEffect?.Dispose();
        downsampleEffect?.Dispose();
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

        // Moon disk: a few times a white surface in full moonlight, so its maria survive the exposure of a moonlit night (the
        // real one is blinding next to the scene it lights) and it is a pale disk in the blue sky by day
        var moonDisk = (Vector3)atmosphere.FullMoonIlluminance * (6.0f * atmosphere.MoonDiskIntensity);
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
    /// Captures the sky (the clouds' parameters set by <paramref name="setCloudParameters"/>) one step per frame, so the
    /// frames cost the same: <see cref="CaptureFacesPerFrame"/> faces a step, averaged into the previous captures of those
    /// faces; then the cube with its box filtered mips and the spherical harmonics; then the reflection cube's GGX levels.
    /// The atmosphere's sky light points at the results, updated in place. A sky light not published yet (a new
    /// atmosphere, a new size) is captured whole at once.
    /// </summary>
    public void Capture(RenderDrawContext context, RenderAtmosphere atmosphere, float haze, Vector3 eye, Action<ParameterCollection> setCloudParameters)
    {
        EnsureCapture();
        var facesPerStep = Math.Clamp(CaptureFacesPerFrame, 1, 6);
        var faceSteps = (6 + facesPerStep - 1) / facesPerStep;
        if (publishedSkybox != atmosphere.Skybox)
        {
            for (int step = 0; step < faceSteps; step++)
                CaptureFaces(context, atmosphere, haze, eye, setCloudParameters, step * facesPerStep, facesPerStep);
            FilterCapture(context);
            PrefilterReflections(context);
            Publish(atmosphere.Skybox);
            captureStep = 0;
            return;
        }

        if (captureStep < faceSteps)
            CaptureFaces(context, atmosphere, haze, eye, setCloudParameters, captureStep * facesPerStep, facesPerStep);
        else if (captureStep == faceSteps)
            FilterCapture(context);
        else
            PrefilterReflections(context);
        captureStep = captureStep >= faceSteps + 1 ? 0 : captureStep + 1;
    }

    // Faces [first, first + count) into the capture's faces, averaged with their previous captures (blended: the weight in
    // alpha). Each capture samples another point of every texel and another march jitter
    private void CaptureFaces(RenderDrawContext context, RenderAtmosphere atmosphere, float haze, Vector3 eye, Action<ParameterCollection> setCloudParameters, int first, int count)
    {
        count = Math.Min(count, 6 - first);
        var parameters = captureEffect.Parameters;
        SetParameters(parameters, atmosphere, haze);
        setCloudParameters?.Invoke(parameters);
        parameters.Set(SkyCubeCaptureKeys.CaptureClouds, setCloudParameters != null ? 1 : 0);
        parameters.Set(SkyCubeCaptureKeys.CaptureOrigin, eye);
        captureIndex = (captureIndex + 1) % 1024;
        parameters.Set(SkyCubeCaptureKeys.CaptureFrame, (float)captureIndex);
        parameters.Set(SkyCubeCaptureKeys.CaptureJitter, new Vector2(Halton(captureIndex % 16 + 1, 2) - 0.5f, Halton(captureIndex % 16 + 1, 3) - 0.5f));
        parameters.Set(SkyCubeCaptureKeys.CaptureSize, (float)captureSize);
        parameters.Set(SkyCubeCaptureKeys.CaptureFaceStart, (float)first);
        parameters.Set(SkyCubeCaptureKeys.CaptureFaceCount, (float)count);
        // (a face never captured takes this capture whole)
        parameters.Set(SkyCubeCaptureKeys.CaptureWeight, facesCaptured[first] ? CaptureBlend : 1.0f);
        captureEffect.SetOutput(captureAtlas);
        captureEffect.SetViewport(new Viewport(first * captureSize, 0, count * captureSize, captureSize));
        captureEffect.Draw(context, "SkyCubeCapture");
        for (int face = first; face < first + count; face++)
            facesCaptured[face] = true;
    }

    // The faces into the cube, its box filtered mips (what the prefilter reads) and the spherical harmonics
    private void FilterCapture(RenderDrawContext context)
    {
        using var _ = context.QueryManager.BeginProfile(Color.SkyBlue, AtmosphereRenderer.SkyFilterKey);
        var commandList = context.CommandList;
        commandList.ResetTargets();
        for (int face = 0; face < 6; face++)
        {
            var region = new ResourceRegion(face * captureSize, 0, 0, (face + 1) * captureSize, captureSize, 1);
            commandList.CopyRegion(captureAtlas, 0, region, captureCube, face * captureCube.MipLevelCount);
        }
        // (all faces of a level per dispatch)
        for (int mip = 1; mip < captureCube.MipLevelCount; mip++)
        {
            var mipSize = Math.Max(1, captureSize >> mip);
            downsampleEffect.Parameters.Set(SkyCubeDownsampleKeys.DownsampleSource, captureMips[mip - 1]);
            downsampleEffect.Parameters.Set(SkyCubeDownsampleKeys.DownsampleOutput, captureMips[mip]);
            downsampleEffect.Parameters.Set(SkyCubeDownsampleKeys.DownsampleSize, mipSize);
            downsampleEffect.ThreadGroupCounts = new Int3((mipSize + 7) / 8, (mipSize + 7) / 8, 6);
            downsampleEffect.Draw(context, "SkyCaptureMip");
        }
        commandList.ResourceBarrierTransition(captureCube, BarrierLayout.ShaderResource);

        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyCaptureCube, captureCube);
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyCaptureMip, (float)Math.Max(0, (int)MathF.Log2(captureSize / 16.0f)));
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsOutput, Harmonics);
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsPrevious, previousHarmonics);
        // (a few captures to settle: the sky light changes smoothly)
        harmonicsEffect.Parameters.Set(SkySphericalHarmonicsKeys.SkyHarmonicsBlend, hasHarmonics ? 0.35f : 1.0f);
        harmonicsEffect.Draw(context, "SkySphericalHarmonics");
        commandList.Copy(Harmonics, previousHarmonics);
        hasHarmonics = true;
    }

    // The sky light's reflections: the sharp capture at the top, then each level convolved with the GGX lobe of the
    // roughness the materials read it at (perceptual roughness x mip count, see RoughnessCubeMapEnvironmentColor)
    private void PrefilterReflections(RenderDrawContext context)
    {
        using var _ = context.QueryManager.BeginProfile(Color.SkyBlue, AtmosphereRenderer.SkyFilterKey);
        var commandList = context.CommandList;
        for (int face = 0; face < 6; face++)
            commandList.CopyRegion(captureCube, face * captureCube.MipLevelCount, null, publishedCube, face * publishedCube.MipLevelCount);
        var prefilter = prefilterEffect.Parameters;
        prefilter.Set(SkyPrefilterKeys.PrefilterSource, captureCube);
        prefilter.Set(SkyPrefilterKeys.PrefilterSourceSize, (float)captureSize);
        prefilter.Set(SkyPrefilterKeys.PrefilterSourceLevels, (float)captureCube.MipLevelCount);
        prefilter.Set(SkyPrefilterKeys.PrefilterSamples, PrefilterSamples);
        for (int mip = 1; mip < publishedCube.MipLevelCount; mip++)
        {
            var mipSize = Math.Max(1, captureSize >> mip);
            prefilter.Set(SkyPrefilterKeys.PrefilterOutput, publishedMips[mip]);
            prefilter.Set(SkyPrefilterKeys.PrefilterSize, mipSize);
            prefilter.Set(SkyPrefilterKeys.PrefilterRoughness, (float)mip / publishedCube.MipLevelCount);
            prefilterEffect.ThreadGroupCounts = new Int3((mipSize + 7) / 8, (mipSize + 7) / 8, 6);
            prefilterEffect.Draw(context, "SkyPrefilter");
        }
        commandList.ResourceBarrierTransition(publishedCube, BarrierLayout.ShaderResource);
    }

    // The scene's sky light: set once, the textures it points at are updated in place
    private void Publish(Skybox skybox)
    {
        publishedSkybox = skybox;
        skybox.DiffuseLightingParameters.Set(SkyboxKeys.Shader, new Stride.Shaders.ShaderClassSource("SphericalHarmonicsBufferEnvironmentColor"));
        skybox.DiffuseLightingParameters.Set(SphericalHarmonicsBufferEnvironmentColorKeys.SphericalHarmonicsBuffer, Harmonics);
        skybox.SpecularLightingParameters.Set(SkyboxKeys.Shader, new Stride.Shaders.ShaderClassSource("RoughnessCubeMapEnvironmentColor"));
        skybox.SpecularLightingParameters.Set(SkyboxKeys.CubeMap, publishedCube);
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

    private void EnsureCapture()
    {
        var size = Math.Clamp(MathUtil.NextPowerOfTwo(CaptureSize), 32, 512);
        if (captureCube != null && captureSize == size)
            return;
        DisposeCapture();
        captureSize = size;
        var device = Context.GraphicsDevice;
        captureCube = Texture.NewCube(device, size, MipMapCount.Auto, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        captureCube.Name = "AtmosphereSkyCapture";
        captureMips = new Texture[captureCube.MipLevelCount];
        for (int mip = 0; mip < captureCube.MipLevelCount; mip++)
            captureMips[mip] = captureCube.ToTextureView(ViewType.MipBand, 0, mip);
        captureAtlas = Texture.New2D(device, size * 6, size, PixelFormat.R16G16B16A16_Float, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        captureAtlas.Name = "AtmosphereSkyCaptureFaces";
        Array.Clear(facesCaptured);
        captureStep = 0;
        publishedCube = Texture.NewCube(device, size, MipMapCount.Auto, PixelFormat.R16G16B16A16_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
        publishedCube.Name = "AtmosphereSkyLight";
        publishedMips = new Texture[publishedCube.MipLevelCount];
        for (int mip = 1; mip < publishedCube.MipLevelCount; mip++)
            publishedMips[mip] = publishedCube.ToTextureView(ViewType.MipBand, 0, mip);
        publishedSkybox = null;
    }

    private void DisposeCapture()
    {
        if (captureMips != null)
        {
            foreach (var view in captureMips)
                view?.Dispose();
            captureMips = null;
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
        if (publishedMips != null)
        {
            foreach (var view in publishedMips)
                view?.Dispose();
            publishedMips = null;
        }
        publishedCube?.Dispose();
        publishedCube = null;
    }
}

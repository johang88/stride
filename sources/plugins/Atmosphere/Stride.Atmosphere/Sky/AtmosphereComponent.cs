// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// The sky of a scene: a physically based atmosphere (Rayleigh and Mie scattering, ozone) lit by the sun and the moon,
/// drawn behind everything by the atmosphere renderer of the graphics compositor, with the haze of distance over the
/// surfaces. It colors the sunlight through the atmosphere, lights the scene with the sky (a capture with the clouds,
/// updated as the sky changes) and, with <see cref="TimeOfDay"/>, moves the sun and the moon. One per scene (the first
/// enabled one is used); it replaces a skybox and its light.
/// </summary>
[DataContract("AtmosphereComponent")]
[Display("Sky atmosphere", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(AtmosphereProcessor))]
[ComponentCategory("Environment")]
[ComponentOrder(12000)]
public sealed class AtmosphereComponent : ActivableEntityComponent
{
    /// <summary>
    /// The directional light of the sun. Its color and intensity are the sunlight above the atmosphere; the scene gets it
    /// through the atmosphere (see <see cref="TintSunLight"/>). Without one, the brightest directional light is the sun.
    /// </summary>
    [DataMember(10)]
    public LightComponent Sun { get; set; }

    /// <summary>
    /// Whether the sunlight is colored and dimmed by the atmosphere it crosses (warm and dim at sunset).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(true)]
    public bool TintSunLight { get; set; } = true;

    /// <summary>
    /// Places the sun and the moon from the time of day.
    /// </summary>
    [DataMember(30)]
    public TimeOfDay TimeOfDay { get; set; } = new();

    /// <summary>
    /// Multiplies the light of the sky on the surfaces.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.05, 0.25, 2)]
    public float SkyLightIntensity { get; set; } = 1.0f;

    /// <summary>
    /// World height of altitude zero (sea level), meters.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.0f)]
    public float SeaLevel { get; set; }

    /// <summary>
    /// Multiplies distances for the haze of the atmosphere over the surfaces (more than 1 hazes a small world as if it were larger).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 100, 0.1, 1, 1)]
    public float AerialPerspectiveScale { get; set; } = 1.0f;

    /// <summary>
    /// Apparent diameter of the sun disk, degrees.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.53f)]
    [DataMemberRange(0.05, 10, 0.01, 0.1, 2)]
    public float SunDiskSize { get; set; } = 0.53f;

    /// <summary>
    /// Multiplies the luminance of the sun disk (the real one is far brighter than a screen can show).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.05, 0.25, 2)]
    public float SunDiskIntensity { get; set; } = 1.0f;

    /// <summary>
    /// Moonlight relative to sunlight (the real moon is about 1/400000 of the sun: this is how bright the night is).
    /// </summary>
    [DataMember(90)]
    [DefaultValue(0.06f)]
    [DataMemberRange(0, 1, 0.001, 0.01, 4)]
    public float MoonBrightness { get; set; } = 0.06f;

    /// <summary>
    /// Tint of the moonlight (its brightness is kept): moonlit nights look blue to the eye, though moonlight is reddish
    /// sunlight. White for physical moonlight.
    /// </summary>
    [DataMember(95)]
    public Color3 MoonColor { get; set; } = new Color3(0.55f, 0.72f, 1.0f);

    /// <summary>
    /// Apparent diameter of the moon disk, degrees.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(0.52f)]
    [DataMemberRange(0.05, 20, 0.01, 0.1, 2)]
    public float MoonDiskSize { get; set; } = 0.52f;

    /// <summary>
    /// Brightness of the stars and the Milky Way at night.
    /// </summary>
    [DataMember(110)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.05, 0.25, 2)]
    public float StarBrightness { get; set; } = 1.0f;

    /// <summary>
    /// Brightness of the moonless night sky (airglow), relative to a moonlit one.
    /// </summary>
    [DataMember(120)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0, 5, 0.01, 0.1, 2)]
    public float NightSkyBrightness { get; set; } = 0.3f;

    /// <summary>
    /// The physical description of the atmosphere.
    /// </summary>
    [DataMember(200)]
    [Display("Atmosphere", Expand = ExpandRule.Never)]
    public AtmosphereParameters Atmosphere { get; set; } = new();
}

/// <summary>
/// Physical description of an atmosphere (Hillaire 2020 model), Earth by default. Distances in kilometers, coefficients
/// per kilometer.
/// </summary>
[DataContract("AtmosphereParameters")]
public sealed class AtmosphereParameters
{
    [DataMember(10)]
    [DefaultValue(6360.0f)]
    [DataMemberRange(100, 100000, 10, 100, 0)]
    public float PlanetRadius { get; set; } = 6360.0f;

    [DataMember(20)]
    [DefaultValue(100.0f)]
    [DataMemberRange(1, 1000, 1, 10, 0)]
    public float Height { get; set; } = 100.0f;

    /// <summary>
    /// Rayleigh scattering of the air at sea level (the blue of the sky), per kilometer.
    /// </summary>
    [DataMember(30)]
    public Vector3 RayleighScattering { get; set; } = new(0.005802f, 0.013558f, 0.0331f);

    [DataMember(40)]
    [DefaultValue(8.0f)]
    [DataMemberRange(0.1, 100, 0.1, 1, 2)]
    public float RayleighScaleHeight { get; set; } = 8.0f;

    /// <summary>
    /// Mie scattering of the aerosols at sea level (haze, the glow around the sun), per kilometer. The weather's haze multiplies it.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.004f)]
    [DataMemberRange(0, 1, 0.0001, 0.001, 5)]
    public float MieScattering { get; set; } = 0.004f;

    [DataMember(60)]
    [DefaultValue(0.00044f)]
    [DataMemberRange(0, 1, 0.0001, 0.001, 5)]
    public float MieAbsorption { get; set; } = 0.00044f;

    [DataMember(70)]
    [DefaultValue(1.2f)]
    [DataMemberRange(0.01, 20, 0.05, 0.5, 2)]
    public float MieScaleHeight { get; set; } = 1.2f;

    /// <summary>
    /// Forward scattering of the aerosols (0 even, towards 1 a tight glow around the sun).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 0.999, 0.01, 0.05, 3)]
    public float MieAnisotropy { get; set; } = 0.8f;

    /// <summary>
    /// Ozone absorption (the deep blue of twilight), per kilometer at its peak.
    /// </summary>
    [DataMember(90)]
    public Vector3 OzoneAbsorption { get; set; } = new(0.00065f, 0.001881f, 0.000085f);

    /// <summary>
    /// Average color of the ground, which reflects light back into the sky.
    /// </summary>
    [DataMember(100)]
    public Color3 GroundAlbedo { get; set; } = new(0.15f, 0.16f, 0.12f);
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// The fog of a scene: an exponential height fog, lit by the scene's lights (the sun with its shadows: light shafts) in the
/// volumetric fog of the forward renderer, along with the fog volumes and the fog painted on terrains. Sets how every fog
/// scatters light. One per scene (the first enabled one is used).
/// </summary>
/// <remarks>
/// Densities are extinctions per meter: light crossing d meters of fog of density s keeps exp(-s * d) of its intensity
/// (0.01 halves it every 70 m).
/// </remarks>
[DataContract("VolumetricFogComponent")]
[Display("Volumetric fog", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(VolumetricFogProcessor))]
[ComponentCategory("Environment")]
[ComponentOrder(12100)]
public sealed class VolumetricFogComponent : ActivableEntityComponent
{
    /// <summary>
    /// Density of the height fog at its base height (extinction per meter; 0: only fog volumes and painted fog).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.004f)]
    [DataMemberRange(0, 1, 0.001, 0.01, 4)]
    public float Density { get; set; } = 0.004f;

    /// <summary>
    /// World height where the height fog has its density (it thickens below).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.0f)]
    public float BaseHeight { get; set; }

    /// <summary>
    /// How fast the height fog thins out with height (per meter: the density halves every 0.69 / falloff meters).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.03f)]
    [DataMemberRange(0, 2, 0.005, 0.05, 3)]
    public float HeightFalloff { get; set; } = 0.03f;

    /// <summary>
    /// Color of the light the height fog scatters (the rest is absorbed).
    /// </summary>
    [DataMember(40)]
    public Color3 Albedo { get; set; } = new(0.9f, 0.92f, 0.95f);

    /// <summary>
    /// Light the height fog emits, per unit of density.
    /// </summary>
    [DataMember(50)]
    public Color3 Emissive { get; set; }

    /// <summary>
    /// How much every fog scatters forward: 0 evenly, towards 1 mostly forward (a glow around the sun when looking at it).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.6f)]
    [DataMemberRange(-0.9, 0.9, 0.05, 0.1, 2)]
    public float Anisotropy { get; set; } = 0.6f;

    /// <summary>
    /// How much the scattering spreads out evenly, as light bouncing around inside thick fog does (0-1): brighter fog away from
    /// the sun.
    /// </summary>
    [DataMember(65)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float MultipleScattering { get; set; } = 0.5f;

    /// <summary>
    /// Average color of the ground: the sun and sky light it reflects up lights the fog from below.
    /// </summary>
    [DataMember(67)]
    public Color3 GroundAlbedo { get; set; } = new(0.25f, 0.25f, 0.2f);

    /// <summary>
    /// Multiplies the sun light the fog scatters.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 20, 0.05, 0.5, 2)]
    public float SunScattering { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the sky light the fog scatters.
    /// </summary>
    [DataMember(80)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 20, 0.05, 0.5, 2)]
    public float AmbientScattering { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the light of point and spot lights the fog scatters.
    /// </summary>
    [DataMember(90)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 20, 0.05, 0.5, 2)]
    public float LocalLightScattering { get; set; } = 1.0f;

    /// <summary>
    /// Distance from the camera the volumetric fog covers (meters): fog volumes, painted fog, shadows and local lights end there,
    /// the height fog continues without them.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(160.0f)]
    [DataMemberRange(10, 2000, 1, 10, 0)]
    public float Distance { get; set; } = 160.0f;

    /// <summary>
    /// How much noise breaks up the height fog (0-1). Fog volumes and painted fog have their own strength.
    /// </summary>
    [DataMember(110)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float NoiseStrength { get; set; } = 0.4f;

    /// <summary>
    /// Size of the noise features (meters).
    /// </summary>
    [DataMember(120)]
    [DefaultValue(24.0f)]
    [DataMemberRange(0.5, 500, 0.5, 5, 1)]
    public float NoiseScale { get; set; } = 24.0f;

    /// <summary>
    /// Velocity the noise drifts with (meters per second).
    /// </summary>
    [DataMember(130)]
    public Vector3 Wind { get; set; } = new(1.5f, 0.0f, 0.5f);

    /// <summary>
    /// Whether the sky is fogged, as if it were at the camera's far plane (haze over the horizon).
    /// </summary>
    [DataMember(140)]
    [DefaultValue(true)]
    public bool FogSky { get; set; } = true;
}

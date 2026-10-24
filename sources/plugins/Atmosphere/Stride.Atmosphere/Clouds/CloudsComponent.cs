// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Graphics;

namespace Stride.Atmosphere.Clouds;

/// <summary>
/// Volumetric clouds over a scene with a sky atmosphere: a low layer (stratus to cumulonimbus), a middle one
/// (altostratus, altocumulus) and cirrus high up, shading the ground and the fog below. This sets their look; how much of
/// the sky they cover and with which clouds is the weather's (see the weather component). A cloud map can place clouds
/// by hand over a region. One per scene (the first enabled one is used).
/// </summary>
[DataContract("CloudsComponent")]
[Display("Volumetric clouds", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(CloudsProcessor))]
[ComponentCategory("Environment")]
[ComponentOrder(12010)]
public sealed class CloudsComponent : ActivableEntityComponent
{
    /// <summary>
    /// The low clouds: stratus, stratocumulus, cumulus up to cumulonimbus towers.
    /// </summary>
    [DataMember(10)]
    [Display(Expand = ExpandRule.Once)]
    public LowCloudLayer Low { get; set; } = new();

    /// <summary>
    /// The middle clouds: altostratus sheets, altocumulus fields.
    /// </summary>
    [DataMember(20)]
    public MidCloudLayer Middle { get; set; } = new();

    /// <summary>
    /// The high clouds: cirrostratus veils, cirrus streaks.
    /// </summary>
    [DataMember(30)]
    public HighCloudLayer High { get; set; } = new();

    /// <summary>
    /// Size of the weather pattern (meters): a tile of cloud clusters repeating over the world.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(40000.0f)]
    [DataMemberRange(2000, 500000, 100, 5000, 0)]
    public float WeatherScale { get; set; } = 40000.0f;

    /// <summary>
    /// Cloud cells across the weather pattern: more for smaller, more numerous clouds.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(14)]
    [DataMemberRange(2, 64, 1, 4, 0)]
    public int CellsPerPattern { get; set; } = 14;

    /// <summary>
    /// Seed of the weather pattern.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0)]
    public int Seed { get; set; }

    /// <summary>
    /// Seconds for the clouds to form and dissipate through a full cycle of the pattern (0: the shapes only drift).
    /// </summary>
    [DataMember(70)]
    [DefaultValue(2400.0f)]
    [DataMemberRange(0, 100000, 10, 600, 0)]
    public float EvolutionPeriod { get; set; } = 2400.0f;

    /// <summary>
    /// Size of the cloud droplets (micrometers): the shape of their scattering (larger: a tighter, brighter silver lining).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(15.0f)]
    [DataMemberRange(5, 50, 0.5, 5, 1)]
    public float DropletSize { get; set; } = 15.0f;

    /// <summary>
    /// Multiplies the light the clouds scatter towards the sun's direction (the glowing rims of clouds in front of it).
    /// </summary>
    [DataMember(90)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.05, 0.25, 2)]
    public float SilverLining { get; set; } = 1.0f;

    /// <summary>
    /// Light scattered many times inside the clouds (0-1): lower for darker, more contrasted clouds.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float MultipleScattering { get; set; } = 0.8f;

    /// <summary>
    /// Darkens the thin edges of the clouds where the light has had no room to scatter in yet (0-1): defined, sculpted
    /// clouds with glowing cores facing the sun.
    /// </summary>
    [DataMember(110)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float DarkEdges { get; set; } = 0.6f;

    /// <summary>
    /// Multiplies the light of the sky and the ground on the clouds.
    /// </summary>
    [DataMember(120)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.05, 0.25, 2)]
    public float AmbientLight { get; set; } = 1.0f;

    /// <summary>
    /// How dark the cloud shadows are on the ground (0-1).
    /// </summary>
    [DataMember(130)]
    [DefaultValue(0.9f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float ShadowStrength { get; set; } = 0.9f;

    /// <summary>
    /// How dark the cloud shadows are in the fog (0-1): shafts of light between the clouds.
    /// </summary>
    [DataMember(140)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float FogShadowStrength { get; set; } = 1.0f;

    /// <summary>
    /// Distance (meters) the clouds are drawn to; they fade out before it.
    /// </summary>
    [DataMember(150)]
    [DefaultValue(80000.0f)]
    [DataMemberRange(5000, 300000, 1000, 10000, 0)]
    public float MaxDistance { get; set; } = 80000.0f;

    /// <summary>
    /// Clouds placed by hand: red = coverage, green = cloud type, alpha = how much the map replaces the weather there.
    /// </summary>
    [DataMember(160)]
    public Texture CloudMap { get; set; }

    /// <summary>
    /// World XZ of the center of the cloud map.
    /// </summary>
    [DataMember(170)]
    public Vector2 CloudMapCenter { get; set; }

    /// <summary>
    /// Size of the cloud map along each side (meters).
    /// </summary>
    [DataMember(180)]
    [DefaultValue(20000.0f)]
    [DataMemberRange(100, 500000, 100, 1000, 0)]
    public float CloudMapSize { get; set; } = 20000.0f;

    /// <summary>
    /// How much the cloud map replaces the weather (0-1).
    /// </summary>
    [DataMember(190)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float CloudMapStrength { get; set; } = 1.0f;
}

/// <summary>
/// The look of the low cloud layer.
/// </summary>
[DataContract("LowCloudLayer")]
public sealed class LowCloudLayer
{
    /// <summary>
    /// Altitude of the cloud bases (meters above sea level).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(1400.0f)]
    [DataMemberRange(0, 10000, 10, 100, 0)]
    public float BaseAltitude { get; set; } = 1400.0f;

    /// <summary>
    /// Height of the deepest clouds, the cumulonimbus, over their bases (meters); shallower types reach part of it.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(8000.0f)]
    [DataMemberRange(500, 16000, 50, 500, 0)]
    public float MaxHeight { get; set; } = 8000.0f;

    /// <summary>
    /// Extinction per meter inside the clouds (denser: sharper, more opaque).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.04f)]
    [DataMemberRange(0.001, 1, 0.001, 0.01, 4)]
    public float Density { get; set; } = 0.04f;

    /// <summary>
    /// Size of the shape noise (meters): the lumps the clouds are carved from.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(3200.0f)]
    [DataMemberRange(100, 50000, 50, 500, 0)]
    public float ShapeScale { get; set; } = 3200.0f;

    /// <summary>
    /// Size of the detail noise (meters): the wisps and curls on the edges.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(420.0f)]
    [DataMemberRange(10, 10000, 10, 100, 0)]
    public float DetailScale { get; set; } = 420.0f;

    /// <summary>
    /// How much the noise carves the clouds (0-1): 0 smooth blobs, 1 billowing, cauliflower clouds.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.85f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Erosion { get; set; } = 0.85f;

    /// <summary>
    /// How much smaller than the weather's type (that of the tallest clouds) the clouds can be (0-1): mixed skies of small
    /// and tall clouds.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float TypeVariation { get; set; } = 0.4f;

    /// <summary>
    /// How much tall clouds lean with the wind (meters per meter of height).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(0.15f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float WindShear { get; set; } = 0.15f;
}

/// <summary>
/// The look of the middle cloud layer.
/// </summary>
[DataContract("MidCloudLayer")]
public sealed class MidCloudLayer
{
    [DataMember(10)]
    [DefaultValue(5200.0f)]
    [DataMemberRange(0, 15000, 10, 100, 0)]
    public float BaseAltitude { get; set; } = 5200.0f;

    [DataMember(20)]
    [DefaultValue(700.0f)]
    [DataMemberRange(50, 5000, 10, 100, 0)]
    public float Thickness { get; set; } = 700.0f;

    [DataMember(30)]
    [DefaultValue(0.025f)]
    [DataMemberRange(0.001, 1, 0.001, 0.01, 4)]
    public float Density { get; set; } = 0.025f;

    /// <summary>
    /// Size of the cloudlets' noise (meters).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(3500.0f)]
    [DataMemberRange(100, 50000, 50, 500, 0)]
    public float ShapeScale { get; set; } = 3500.0f;

    [DataMember(50)]
    [DefaultValue(500.0f)]
    [DataMemberRange(10, 10000, 10, 100, 0)]
    public float DetailScale { get; set; } = 500.0f;

    [DataMember(60)]
    [DefaultValue(0.85f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Erosion { get; set; } = 0.85f;

    /// <summary>
    /// Speed of the layer relative to the wind.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(1.6f)]
    [DataMemberRange(0, 10, 0.05, 0.5, 2)]
    public float WindFactor { get; set; } = 1.6f;
}

/// <summary>
/// The look of the high cloud layer.
/// </summary>
[DataContract("HighCloudLayer")]
public sealed class HighCloudLayer
{
    [DataMember(10)]
    [DefaultValue(9500.0f)]
    [DataMemberRange(0, 20000, 10, 100, 0)]
    public float Altitude { get; set; } = 9500.0f;

    /// <summary>
    /// How opaque the ice is (0 invisible).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 2)]
    public float Opacity { get; set; } = 0.4f;

    /// <summary>
    /// Length of the streaks' pattern along the wind (meters).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(60000.0f)]
    [DataMemberRange(1000, 500000, 100, 5000, 0)]
    public float StreakLength { get; set; } = 60000.0f;

    /// <summary>
    /// Width of the streaks' pattern across the wind (meters).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(20000.0f)]
    [DataMemberRange(1000, 500000, 100, 5000, 0)]
    public float StreakWidth { get; set; } = 20000.0f;

    /// <summary>
    /// Speed of the layer relative to the wind.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(2.5f)]
    [DataMemberRange(0, 10, 0.05, 0.5, 2)]
    public float WindFactor { get; set; } = 2.5f;
}

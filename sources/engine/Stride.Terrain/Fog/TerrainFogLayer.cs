// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Rendering.VolumetricFog;

namespace Stride.Terrain.Fog;

/// <summary>
/// Fog painted on the terrain (ground mist, swamp fog, fog filling a valley), drawn by the volumetric fog of the renderer
/// (the scene needs a volumetric fog component). The position of a layer in <see cref="TerrainComponent.Fog"/> is its density
/// channel in the terrain data.
/// </summary>
[DataContract("TerrainFogLayer")]
[Display("Fog layer")]
public sealed class TerrainFogLayer
{
    [DataMember(10)]
    public string Name { get; set; } = "Fog";

    /// <summary>
    /// Density where fully painted (extinction per meter: 0.1 lets a third of the light through 10 m of fog).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.08f)]
    [DataMemberRange(0, 10, 0.005, 0.05, 3)]
    public float Density { get; set; } = 0.08f;

    /// <summary>
    /// How <see cref="Height"/> sets the top of the fog.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(FogLayerHeightMode.AboveGround)]
    public FogLayerHeightMode HeightMode { get; set; } = FogLayerHeightMode.AboveGround;

    /// <summary>
    /// Top of the fog: its height above the ground, or its altitude (relative to the terrain entity) to fill the painted
    /// valleys up to like a lake (meters).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(4.0f)]
    [DataMemberRange(-10000, 10000, 0.5, 5, 1)]
    public float Height { get; set; } = 4.0f;

    /// <summary>
    /// Depth under the top over which the fog thickens to its density (meters): soft or sharp tops.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(3.0f)]
    [DataMemberRange(0.01, 1000, 0.1, 1, 2)]
    public float Softness { get; set; } = 3.0f;

    /// <summary>
    /// Color of the light the fog scatters.
    /// </summary>
    [DataMember(60)]
    public Color3 Albedo { get; set; } = new(0.92f, 0.94f, 0.96f);

    /// <summary>
    /// Light the fog emits per unit of density (glowing swamp gas).
    /// </summary>
    [DataMember(70)]
    public Color3 Emissive { get; set; }

    /// <summary>
    /// How much the noise of the scene's fog breaks it up into wisps (0-1).
    /// </summary>
    [DataMember(80)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float NoiseStrength { get; set; } = 0.6f;
}

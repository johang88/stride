// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Ground cover scattered around the camera on the GPU where its density is painted (grass, ferns, flowers...).
/// The position of a layer in <see cref="TerrainComponent.Grass"/> is its density channel in the terrain data.
/// </summary>
[DataContract("TerrainGrassLayer")]
[Display("Grass layer")]
public sealed class TerrainGrassLayer
{
    [DataMember(10)]
    public string Name { get; set; } = "Grass";

    /// <summary>
    /// The plants scattered, picked at random per instance. Their materials need the grass wind displacement to sway.
    /// </summary>
    [DataMember(20)]
    [MemberCollection(NotNullItems = true)]
    public List<Model> Models { get; } = new();

    /// <summary>
    /// Instances per square meter where the density is fully painted.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.01, 64, 0.1, 1, 2)]
    public float Density { get; set; } = 4.0f;

    [DataMember(40)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0.01, 10, 0.05, 0.1, 2)]
    public float MinScale { get; set; } = 0.8f;

    [DataMember(50)]
    [DefaultValue(1.2f)]
    [DataMemberRange(0.01, 10, 0.05, 0.1, 2)]
    public float MaxScale { get; set; } = 1.2f;

    /// <summary>
    /// Distance from the camera up to which the layer is drawn (meters).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(50.0f)]
    [DataMemberRange(1, 500, 1, 10, 0)]
    public float DrawDistance { get; set; } = 50.0f;

    /// <summary>
    /// Fraction of the draw distance where plants start shrinking away.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.7f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float FadeStart { get; set; } = 0.7f;

    /// <summary>
    /// Tilts the plants with the terrain slope.
    /// </summary>
    [DataMember(80)]
    [DefaultValue(true)]
    public bool AlignToTerrain { get; set; } = true;

    [DataMember(90)]
    [DefaultValue(false)]
    public bool CastShadows { get; set; }
}

/// <summary>
/// Trees and other large plants placed on the terrain: drawn as instanced level of detail meshes up close and as baked
/// impostors (billboards that look the same from every direction) far away. The position of a type in
/// <see cref="TerrainComponent.Vegetation"/> is the type of its instances in the terrain data.
/// </summary>
[DataContract("TerrainVegetationType")]
[Display("Vegetation type")]
public sealed class TerrainVegetationType
{
    [DataMember(10)]
    public string Name { get; set; } = "Tree";

    /// <summary>
    /// The model, with its levels of detail as successive meshes per material (as imported from an FBX LOD group):
    /// the meshes of each material are ordered by triangle count, the n-th of each makes level n.
    /// </summary>
    [DataMember(20)]
    public Model Model { get; set; }

    /// <summary>
    /// Distance where each level of detail hands over to the next (meters), nearest first. When there are fewer than levels,
    /// they're spread up to <see cref="ImpostorDistance"/> (quadratically, as the screen size falls off).
    /// </summary>
    [DataMember(30)]
    public List<float> LodDistances { get; } = new();

    /// <summary>
    /// Distance where the impostor replaces the meshes (meters).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(120.0f)]
    [DataMemberRange(0, 5000, 1, 10, 0)]
    public float ImpostorDistance { get; set; } = 120.0f;

    /// <summary>
    /// Width of the band before <see cref="ImpostorDistance"/> where meshes and impostors dither into each other (meters).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(12.0f)]
    [DataMemberRange(0, 500, 1, 5, 0)]
    public float ImpostorFadeRange { get; set; } = 12.0f;

    /// <summary>
    /// Views along each side of the impostor atlas (hemi-octahedral): more is smoother when turning around it, costs
    /// video memory and baking time quadratically.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(8)]
    [DataMemberRange(4, 16, 1, 2, 0)]
    public int ImpostorViews { get; set; } = 8;

    /// <summary>
    /// Resolution of each impostor view (pixels).
    /// </summary>
    [DataMember(70)]
    [DefaultValue(256)]
    [DataMemberRange(32, 1024, 32, 64, 0)]
    public int ImpostorResolution { get; set; } = 256;

    [DataMember(80)]
    [DefaultValue(true)]
    public bool CastShadows { get; set; } = true;

    /// <summary>
    /// Whether the impostors cast shadows too (the shadow maps reach further than the meshes).
    /// </summary>
    [DataMember(90)]
    [DefaultValue(true)]
    public bool ImpostorsCastShadows { get; set; } = true;
}

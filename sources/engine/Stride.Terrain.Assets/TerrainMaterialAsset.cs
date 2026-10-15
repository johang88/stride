// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Terrain;

namespace Stride.Assets.Terrain;

/// <summary>
/// The material layers a terrain is painted with. The layer textures are compiled into texture arrays.
/// </summary>
/// <remarks>
/// The position of a layer in <see cref="Layers"/> is its id in the terrain splat maps: reordering or removing layers
/// changes what terrains using this material display (use the terrain editor commands to remap).
/// </remarks>
[DataContract("TerrainMaterialAsset")]
[AssetDescription(FileExtension)]
[AssetContentType(typeof(TerrainMaterial))]
[AssetFormatVersion(StrideConfig.LogicalPackageName, CurrentVersion, "1.0.0.0")]
[Display(1950, "Terrain material")]
public sealed class TerrainMaterialAsset : Asset
{
    private const string CurrentVersion = "1.0.0.0";

    public const string FileExtension = ".sdterrainmat";

    /// <summary>
    /// The layers. Their index is the id stored in terrain splat maps.
    /// </summary>
    [DataMember(10)]
    [MemberCollection(NotNullItems = true)]
    public List<TerrainLayerDescription> Layers { get; } = new();

    /// <summary>
    /// Resolution of each layer in the texture arrays (power of two).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(TerrainLayerTextureSize.Size1024)]
    public TerrainLayerTextureSize TextureSize { get; set; } = TerrainLayerTextureSize.Size1024;

    /// <summary>
    /// Controls how sharp height-based transitions between layers are. Smaller is sharper.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.2f)]
    [DataMemberRange(0.001, 2.0, 0.01, 0.1, 3)]
    public float HeightBlendSharpness { get; set; } = 0.2f;

    /// <summary>
    /// Layer drawn with a tri-planar projection on steep slopes (avoids stretching on cliffs). -1 to disable.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(-1)]
    public int CliffLayer { get; set; } = -1;

    [DataMember(50)]
    [DefaultValue(0.35f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float CliffSlopeStart { get; set; } = 0.35f;

    [DataMember(60)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float CliffSlopeEnd { get; set; } = 0.5f;

    /// <summary>
    /// Depth of the parallax occlusion mapping (displacement of the layer height maps seen up close), in meters. 0 disables it.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.05f)]
    [DataMemberRange(0, 0.5, 0.005, 0.02, 3)]
    public float ParallaxDepth { get; set; } = 0.05f;

    /// <summary>
    /// Distance over which the parallax effect fades out, in meters.
    /// </summary>
    [DataMember(80)]
    [DefaultValue(40.0f)]
    [DataMemberRange(1, 500, 1, 10, 1)]
    public float ParallaxFadeDistance { get; set; } = 40.0f;
}

public enum TerrainLayerTextureSize
{
    [Display("256")] Size256 = 256,
    [Display("512")] Size512 = 512,
    [Display("1024")] Size1024 = 1024,
    [Display("2048")] Size2048 = 2048,
}

/// <summary>
/// A material layer of a terrain material.
/// </summary>
[DataContract("TerrainLayerDescription")]
public sealed class TerrainLayerDescription
{
    [DataMember(10)]
    public string Name { get; set; } = "Layer";

    /// <summary>
    /// Albedo (color) texture. When not set, <see cref="Tint"/> is used as a flat color.
    /// </summary>
    [DataMember(20)]
    public Texture Albedo { get; set; }

    /// <summary>
    /// Tangent space normal map (X right, Y up, as compiled by its texture asset: the normal map "Invert Y" option is honored).
    /// </summary>
    [DataMember(30)]
    public Texture Normal { get; set; }

    /// <summary>
    /// Roughness texture (red channel). When not set, <see cref="Roughness"/> is used.
    /// </summary>
    [DataMember(40)]
    public Texture RoughnessMap { get; set; }

    /// <summary>
    /// When true, the roughness map stores glossiness (smoothness) and is inverted.
    /// </summary>
    [DataMember(45)]
    [DefaultValue(false)]
    public bool InvertRoughnessMap { get; set; }

    /// <summary>
    /// Ambient occlusion texture (red channel).
    /// </summary>
    [DataMember(50)]
    public Texture AmbientOcclusion { get; set; }

    /// <summary>
    /// Height (displacement) texture (red channel), used for height-based blending.
    /// </summary>
    [DataMember(60)]
    public Texture Height { get; set; }

    /// <summary>
    /// World size in meters covered by one repetition of the textures.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.05, 1000, 0.1, 1, 2)]
    public float TileSize { get; set; } = 4.0f;

    [DataMember(80)]
    public Color3 Tint { get; set; } = new Color3(1.0f);

    /// <summary>
    /// Roughness used when there's no roughness map, multiplier otherwise.
    /// </summary>
    [DataMember(90)]
    [DefaultValue(0.85f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Roughness { get; set; } = 0.85f;

    [DataMember(100)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 2)]
    public float NormalStrength { get; set; } = 1.0f;

    [DataMember(110)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 2)]
    public float HeightBlendScale { get; set; } = 1.0f;

    [DataMember(120)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-1, 1, 0.01, 0.1, 2)]
    public float HeightBlendOffset { get; set; }

    /// <summary>
    /// Hides the tiling of natural textures by mixing in a second sample at another scale and rotation (0 disables it,
    /// for regular patterns such as cobblestones).
    /// </summary>
    [DataMember(125)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float TilingVariation { get; set; } = 0.5f;

    /// <summary>
    /// Free-form tag available to gameplay (footsteps, VFX).
    /// </summary>
    [DataMember(130)]
    public string SurfaceTag { get; set; }
}

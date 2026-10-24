// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Stride.Terrain.Decals;

namespace Stride.Terrain.Splines;

/// <summary>
/// What a terrain spline represents. It only changes the defaults of the terrain shaping (and the gizmo color).
/// </summary>
public enum TerrainSplineKind
{
    /// <summary>
    /// A flat surface following the curve (road, path, wall top).
    /// </summary>
    Road,

    /// <summary>
    /// A channel carved below the curve, the curve being the water level at the banks.
    /// </summary>
    River,
}

/// <summary>
/// A curve laid over the terrain, used for roads and rivers. The control points are the child entities
/// (in order; the X scale of a point scales the width there). From the scene editor, the terrain tools
/// apply the splines to the terrain: they flatten or carve the height field and paint layers along the curve.
/// A mesh following the curve is generated when a material is set (water surface), and a decal following the curve
/// can be baked into the terrain virtual texture (road surface, tracks).
/// </summary>
[DataContract("TerrainSplineComponent")]
[Display("Terrain spline", Expand = ExpandRule.Once)]
[DefaultEntityComponentProcessor(typeof(TerrainSplineProcessor), ExecutionMode = ExecutionMode.All)]
[ComponentCategory("Terrain")]
[ComponentOrder(11010)]
public sealed class TerrainSplineComponent : EntityComponent
{
    [DataMember(10)]
    [DefaultValue(TerrainSplineKind.Road)]
    public TerrainSplineKind Kind { get; set; } = TerrainSplineKind.Road;

    /// <summary>
    /// Connects the last control point back to the first.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(false)]
    public bool Closed { get; set; }

    /// <summary>
    /// Width in meters (scaled by the X scale of each control point).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.1, 500, 0.1, 1, 2)]
    public float Width { get; set; } = 4.0f;

    /// <summary>
    /// Distance between the samples of the curve in meters.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0.1, 20, 0.1, 1, 2)]
    public float SampleSpacing { get; set; } = 1.0f;

    /// <summary>
    /// Whether applying the splines changes the terrain heights.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(true)]
    [Display("Shape terrain", "Terrain")]
    public bool ShapeTerrain { get; set; } = true;

    /// <summary>
    /// Width of the band beyond the edges over which the shape blends into the existing terrain, in meters.
    /// </summary>
    [DataMember(110)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0, 200, 0.1, 1, 2)]
    [Display("Falloff", "Terrain")]
    public float Falloff { get; set; } = 4.0f;

    /// <summary>
    /// Depth of the channel at the center below the curve (rivers), in meters.
    /// </summary>
    [DataMember(120)]
    [DefaultValue(2.0f)]
    [Display("River depth", "Terrain")]
    public float Depth { get; set; } = 2.0f;

    /// <summary>
    /// Height of the shaped surface relative to the curve.
    /// </summary>
    [DataMember(130)]
    [DefaultValue(0.0f)]
    [Display("Height offset", "Terrain")]
    public float HeightOffset { get; set; }

    /// <summary>
    /// Terrain material layer painted over the width (-1 for none).
    /// </summary>
    [DataMember(140)]
    [DefaultValue(-1)]
    [DataMemberRange(-1, 62, 1, 1, 0)]
    [Display("Paint layer", "Terrain")]
    public int PaintLayer { get; set; } = -1;

    /// <summary>
    /// Width of the painted band relative to the spline width.
    /// </summary>
    [DataMember(150)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.05, 0.25, 2)]
    [Display("Paint width", "Terrain")]
    public float PaintWidthScale { get; set; } = 1.0f;

    /// <summary>
    /// Terrain material layer painted in the falloff band (road shoulders, river banks; -1 for none).
    /// </summary>
    [DataMember(160)]
    [DefaultValue(-1)]
    [DataMemberRange(-1, 62, 1, 1, 0)]
    [Display("Edge layer", "Terrain")]
    public int EdgeLayer { get; set; } = -1;

    /// <summary>
    /// Opacity of the painted layers.
    /// </summary>
    [DataMember(170)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.05, 0.25, 2)]
    [Display("Paint strength", "Terrain")]
    public float PaintStrength { get; set; } = 1.0f;

    /// <summary>
    /// Material of the mesh generated along the curve (none: no mesh).
    /// </summary>
    [DataMember(200)]
    [Display("Material", "Mesh")]
    public Material MeshMaterial { get; set; }

    /// <summary>
    /// Width of the mesh relative to the spline width.
    /// </summary>
    [DataMember(210)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0.01, 10, 0.05, 0.25, 2)]
    [Display("Width", "Mesh")]
    public float MeshWidthScale { get; set; } = 1.0f;

    /// <summary>
    /// Height of the mesh above the terrain (conforming meshes) or relative to the curve (flat meshes such as water).
    /// </summary>
    [DataMember(220)]
    [DefaultValue(0.05f)]
    [Display("Height offset", "Mesh")]
    public float MeshOffset { get; set; } = 0.05f;

    /// <summary>
    /// The mesh follows the terrain surface across its width (roads); otherwise it is flat across at the curve height (water).
    /// </summary>
    [DataMember(230)]
    [DefaultValue(true)]
    [Display("Conform to terrain", "Mesh")]
    public bool ConformToTerrain { get; set; } = true;

    /// <summary>
    /// Length in meters covered by one repetition of the texture along the curve.
    /// </summary>
    [DataMember(240)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.01, 1000, 0.1, 1, 2)]
    [Display("Texture length", "Mesh")]
    public float TextureLength { get; set; } = 4.0f;

    [DataMember(250)]
    [DefaultValue(false)]
    [Display("Cast shadows", "Mesh")]
    public bool CastShadows { get; set; }

    [DataMember(260)]
    [Display("Render group", "Mesh")]
    public RenderGroup RenderGroup { get; set; }

    /// <summary>
    /// Draws a decal along the curve into the terrain virtual texture (U across the width, V along the curve).
    /// </summary>
    [DataMember(300)]
    [DefaultValue(false)]
    [Display("Bake into virtual texture", "Decal")]
    public bool BakeDecal { get; set; }

    [DataMember(310)]
    [NotNull]
    [Display("Surface", "Decal", Expand = ExpandRule.Once)]
    public TerrainDecalSurface Decal { get; set; } = new();

    /// <summary>
    /// Width of the decal relative to the spline width.
    /// </summary>
    [DataMember(320)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0.01, 10, 0.05, 0.25, 2)]
    [Display("Width", "Decal")]
    public float DecalWidthScale { get; set; } = 1.0f;

    /// <summary>
    /// Length in meters covered by one repetition of the decal textures along the curve.
    /// </summary>
    [DataMember(330)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.01, 1000, 0.1, 1, 2)]
    [Display("Texture length", "Decal")]
    public float DecalTextureLength { get; set; } = 4.0f;

    /// <summary>
    /// Decals with a higher order are drawn over the others.
    /// </summary>
    [DataMember(340)]
    [DefaultValue(0)]
    [Display("Order", "Decal")]
    public int DecalOrder { get; set; }

    /// <summary>
    /// Samples the curve through the current world positions of the control points.
    /// </summary>
    public TerrainSplineCurve Evaluate() => TerrainSplineCurve.FromComponent(this);
}

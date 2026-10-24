// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Terrain.Decals;

/// <summary>
/// How a decal changes the normals of the terrain.
/// </summary>
public enum TerrainDecalNormalBlend
{
    /// <summary>
    /// The terrain normals are kept.
    /// </summary>
    Keep,

    /// <summary>
    /// The decal normals replace the terrain normals (flat without a normal map).
    /// </summary>
    Replace,

    /// <summary>
    /// The decal normals are added to the terrain normals (ruts, cracks, footprints).
    /// </summary>
    Detail,
}

/// <summary>
/// What a terrain decal draws into the terrain virtual texture. Each channel blends over the terrain by the decal opacity
/// (albedo alpha x tint alpha x opacity x edge fade).
/// </summary>
[DataContract("TerrainDecalSurface")]
[Display("Terrain decal surface")]
public sealed class TerrainDecalSurface
{
    /// <summary>
    /// Color (rgb) and opacity (alpha). White when not set.
    /// </summary>
    [DataMember(10)]
    public Texture Albedo { get; set; }

    /// <summary>
    /// Multiplies the albedo (alpha multiplies the opacity).
    /// </summary>
    [DataMember(20)]
    public Color4 Tint { get; set; } = Color4.White;

    [DataMember(30)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Width of the band along the edges over which the decal fades out, in meters (0 for a hard edge).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 100, 0.05, 0.5, 2)]
    public float EdgeFade { get; set; } = 0.5f;

    /// <summary>
    /// Whether the decal changes the terrain color (off for decals that only change normals or roughness).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(true)]
    public bool AffectAlbedo { get; set; } = true;

    /// <summary>
    /// Tangent space normal map (same convention as the terrain layers).
    /// </summary>
    [DataMember(60)]
    public Texture Normal { get; set; }

    [DataMember(70)]
    [DefaultValue(TerrainDecalNormalBlend.Replace)]
    public TerrainDecalNormalBlend NormalBlend { get; set; } = TerrainDecalNormalBlend.Replace;

    [DataMember(80)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.05, 0.25, 2)]
    public float NormalStrength { get; set; } = 1.0f;

    /// <summary>
    /// Whether the decal changes the terrain roughness.
    /// </summary>
    [DataMember(90)]
    [DefaultValue(false)]
    public bool AffectRoughness { get; set; }

    /// <summary>
    /// Roughness map (red channel), multiplied by <see cref="Roughness"/>.
    /// </summary>
    [DataMember(100)]
    public Texture RoughnessMap { get; set; }

    [DataMember(110)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Roughness { get; set; } = 0.8f;

    /// <summary>
    /// Whether the decal changes the blended layer height (used by the terrain parallax and height blending).
    /// </summary>
    [DataMember(120)]
    [DefaultValue(false)]
    public bool AffectHeight { get; set; }

    /// <summary>
    /// Layer height under the decal (1 is the terrain surface, lower values sink under parallax).
    /// </summary>
    [DataMember(130)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Height { get; set; } = 1.0f;

    /// <summary>
    /// Hash of the settings, to detect changes that need the virtual texture to be rendered again.
    /// </summary>
    public int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(Albedo);
        hash.Add(Tint);
        hash.Add(Opacity);
        hash.Add(EdgeFade);
        hash.Add(AffectAlbedo);
        hash.Add(Normal);
        hash.Add(NormalBlend);
        hash.Add(NormalStrength);
        hash.Add(AffectRoughness);
        hash.Add(RoughnessMap);
        hash.Add(Roughness);
        hash.Add(AffectHeight);
        hash.Add(Height);
        return hash.ToHashCode();
    }
}

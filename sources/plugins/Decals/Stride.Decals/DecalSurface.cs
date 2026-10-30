// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Decals;

/// <summary>
/// What a decal draws on the surfaces it covers. Every channel blends over the surface by the decal coverage: the alpha of
/// the albedo texture x the alpha of the color x the opacity of the decal x its fades.
/// </summary>
/// <remarks>
/// Decals with the same textures are drawn together (one instanced draw), whatever their other settings.
/// </remarks>
[DataContract("DecalSurface")]
[Display("Decal surface")]
public sealed class DecalSurface
{
    /// <summary>
    /// Color (rgb) and coverage (alpha). White when not set.
    /// </summary>
    /// <userdoc>Color (rgb) and coverage (alpha). Its alpha also masks the other channels.</userdoc>
    [DataMember(10)]
    [Display("Albedo", "Albedo")]
    public Texture Albedo { get; set; }

    /// <summary>
    /// Multiplies the albedo (alpha multiplies the coverage).
    /// </summary>
    [DataMember(20)]
    [Display("Color", "Albedo")]
    public Color4 Color { get; set; } = Color4.White;

    /// <summary>
    /// Whether the decal changes the color of the surface (off for decals that only glow, or only change normals or roughness).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(true)]
    [Display("Affect albedo", "Albedo")]
    public bool AffectAlbedo { get; set; } = true;

    /// <summary>
    /// Tangent space normal map: +X along the decal's X axis, +Y (green up) towards its -Z axis.
    /// </summary>
    [DataMember(40)]
    [Display("Normal map", "Normal")]
    public Texture Normal { get; set; }

    [DataMember(50)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.05, 0.25, 2)]
    [Display("Normal strength", "Normal")]
    public float NormalStrength { get; set; } = 1.0f;

    /// <summary>
    /// Whether the decal changes the normals of the surface: the bumps of its normal map are added to the surface's own
    /// (cracks, footprints, ruts).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(false)]
    [Display("Affect normal", "Normal")]
    public bool AffectNormal { get; set; }

    /// <summary>
    /// Occlusion (red), roughness (green) and metalness (blue), multiplying the values below. White when not set.
    /// </summary>
    [DataMember(70)]
    [Display("Surface map", "Surface")]
    public Texture SurfaceMap { get; set; }

    [DataMember(80)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    [Display("Roughness", "Surface")]
    public float Roughness { get; set; } = 0.6f;

    [DataMember(90)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    [Display("Metalness", "Surface")]
    public float Metalness { get; set; }

    /// <summary>
    /// How much the occlusion of the surface map darkens the light from around.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    [Display("Occlusion", "Surface")]
    public float Occlusion { get; set; } = 1.0f;

    /// <summary>
    /// Whether the decal changes the roughness, metalness and occlusion of the surface (wet patches, rust).
    /// </summary>
    [DataMember(110)]
    [DefaultValue(false)]
    [Display("Affect surface", "Surface")]
    public bool AffectSurface { get; set; }

    /// <summary>
    /// Emitted color, multiplied by <see cref="EmissiveColor"/> and <see cref="EmissiveIntensity"/>. White when not set.
    /// </summary>
    [DataMember(120)]
    [Display("Emissive map", "Emissive")]
    public Texture Emissive { get; set; }

    [DataMember(130)]
    [Display("Emissive color", "Emissive")]
    public Color3 EmissiveColor { get; set; } = new(1.0f, 1.0f, 1.0f);

    /// <summary>
    /// Light emitted where the decal covers the surface (0: none), unaffected by the scene's lighting.
    /// </summary>
    [DataMember(140)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1000, 0.1, 1, 2)]
    [Display("Emissive intensity", "Emissive")]
    public float EmissiveIntensity { get; set; }
}

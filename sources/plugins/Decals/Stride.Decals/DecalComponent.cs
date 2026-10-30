// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Decals;

/// <summary>
/// A decal projected onto the opaque surfaces inside a box centered on the entity, down its Y axis (the texture's top
/// towards the entity's -Z, forward). Drawn every frame by the <see cref="DecalRenderer"/> of the graphics compositor, so it can
/// move and animate freely; surfaces opt out with Receive decals on their model or material, and skinned models never
/// receive decals.
/// </summary>
[DataContract("DecalComponent")]
[Display("Decal", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(DecalProcessor))]
[ComponentCategory("Rendering")]
[ComponentOrder(11500)]
public sealed class DecalComponent : ActivableEntityComponent
{
    /// <summary>
    /// Size of the box in meters: X and Z the area covered, Y the depth it projects through. Scaled by the entity.
    /// </summary>
    [DataMember(10)]
    public Vector3 Size { get; set; } = new(2.0f, 2.0f, 2.0f);

    /// <summary>
    /// Decals with a higher order are drawn over the others (decals of the same order: in the order they were added).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0)]
    public int Order { get; set; }

    /// <summary>
    /// Multiplies the albedo and the emissive light of the surface (alpha multiplies the coverage).
    /// </summary>
    [DataMember(30)]
    public Color4 Color { get; set; } = Color4.White;

    [DataMember(40)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Repetitions of the textures over the box (or the share of a texture atlas it shows).
    /// </summary>
    [DataMember(50)]
    [Display("Texture scale")]
    public Vector2 TextureScale { get; set; } = Vector2.One;

    [DataMember(60)]
    [Display("Texture offset")]
    public Vector2 TextureOffset { get; set; }

    /// <summary>
    /// Share of the depth, near the top and bottom of the box, over which the decal fades out (0 for a hard cut).
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.25f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    [Display("Depth fade")]
    public float DepthFade { get; set; } = 0.25f;

    /// <summary>
    /// Angle (degrees) between the surface and the projection at which the decal starts to fade out.
    /// </summary>
    [DataMember(80)]
    [DefaultValue(55.0f)]
    [DataMemberRange(0, 90, 1, 5, 1)]
    [Display("Angle fade start")]
    public float AngleFadeStart { get; set; } = 55.0f;

    /// <summary>
    /// Angle (degrees) between the surface and the projection at which the decal is gone (steeper surfaces aren't covered).
    /// </summary>
    [DataMember(90)]
    [DefaultValue(75.0f)]
    [DataMemberRange(0, 90, 1, 5, 1)]
    [Display("Angle fade end")]
    public float AngleFadeEnd { get; set; } = 75.0f;

    /// <summary>
    /// Distance from the camera (meters) beyond which the decal isn't drawn; it fades out over the last tenth (0: always drawn).
    /// </summary>
    [DataMember(100)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 10000, 1, 10, 1)]
    [Display("Draw distance")]
    public float DrawDistance { get; set; }

    [DataMember(110)]
    [NotNull]
    [Display(Expand = ExpandRule.Always)]
    public DecalSurface Surface { get; set; } = new();
}

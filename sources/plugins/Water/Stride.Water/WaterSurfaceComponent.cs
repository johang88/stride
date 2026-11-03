// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;

namespace Stride.Water;

/// <summary>
/// A body of water: a flat rectangular surface centered on the entity (its local XZ plane, at the entity's height) drawn with
/// <see cref="Material"/>, and the volume under it down to <see cref="Depth"/> (where characters wade, see
/// <see cref="WaterSurfaceProcessor.TryGetSurface"/>). Characters with a <see cref="WaterRippleEmitterComponent"/> make
/// ripples in it.
/// </summary>
/// <remarks>
/// A water material typically uses the Water transparency, a Water Normal node as normal map, metalness 0 and a high gloss.
/// The texture coordinates of the surface are its local X and Z in meters.
/// </remarks>
[DataContract("WaterSurfaceComponent")]
[Display("Water Surface", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(WaterSurfaceProcessor))]
[ComponentCategory("Water")]
public sealed class WaterSurfaceComponent : ActivableEntityComponent
{
    /// <userdoc>Size of the surface along the entity's X and Z axes (meters).</userdoc>
    [DataMember(10)]
    public Vector2 Size { get; set; } = new(20, 20);

    /// <userdoc>Depth of the water under the surface (meters): positions deeper than this are not in the water.</userdoc>
    [DataMember(20)]
    [DefaultValue(3.0f)]
    [DataMemberRange(0.01, 1000, 0.1, 1, 2)]
    public float Depth { get; set; } = 3.0f;

    /// <userdoc>The water's material (e.g. Water transparency with a Water Normal map).</userdoc>
    [DataMember(30)]
    public Material Material { get; set; }

    /// <userdoc>Characters wading in this water make dynamic ripples.</userdoc>
    [DataMember(40)]
    [DefaultValue(true)]
    public bool Ripples { get; set; } = true;

    /// <userdoc>Render group of the surface.</userdoc>
    [DataMember(50)]
    [DefaultValue(RenderGroup.Group0)]
    public RenderGroup RenderGroup { get; set; } = RenderGroup.Group0;
}

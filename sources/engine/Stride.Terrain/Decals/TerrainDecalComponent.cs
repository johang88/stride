// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Terrain.Decals
{
    /// <summary>
    /// Shape of the area covered by a terrain decal.
    /// </summary>
    public enum TerrainDecalShape
    {
        Rectangle,

        /// <summary>
        /// The ellipse inscribed in the rectangle (the edge fade follows the ellipse).
        /// </summary>
        Ellipse,
    }

    /// <summary>
    /// A decal baked into the terrain virtual texture: projected from above onto the terrain, centered on the entity and
    /// rotated with it (the scale of the entity scales the size). Costs nothing per frame once the pages are rendered.
    /// </summary>
    [DataContract("TerrainDecalComponent")]
    [Display("Terrain decal", Expand = ExpandRule.Once)]
    [DefaultEntityComponentProcessor(typeof(TerrainDecalProcessor), ExecutionMode = ExecutionMode.All)]
    [ComponentCategory("Terrain")]
    [ComponentOrder(11020)]
    public sealed class TerrainDecalComponent : ActivableEntityComponent
    {
        /// <summary>
        /// Size in meters along the local X and Z axes.
        /// </summary>
        [DataMember(10)]
        public Vector2 Size { get; set; } = new(4.0f, 4.0f);

        [DataMember(20)]
        [DefaultValue(TerrainDecalShape.Rectangle)]
        public TerrainDecalShape Shape { get; set; } = TerrainDecalShape.Rectangle;

        /// <summary>
        /// Number of repetitions of the textures over the size.
        /// </summary>
        [DataMember(30)]
        public Vector2 Tiling { get; set; } = Vector2.One;

        /// <summary>
        /// Decals with a higher order are drawn over the others (spline decals and decals of the same order: in the order they were added).
        /// </summary>
        [DataMember(40)]
        [DefaultValue(0)]
        public int Order { get; set; }

        [DataMember(50)]
        [NotNull]
        [Display(Expand = ExpandRule.Always)]
        public TerrainDecalSurface Surface { get; set; } = new();
    }
}

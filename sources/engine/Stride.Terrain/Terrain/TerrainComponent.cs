// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;
using Stride.Terrain.Rendering;

namespace Stride.Terrain
{
    /// <summary>
    /// How the terrain surface is shaded.
    /// </summary>
    public enum TerrainShadingMode
    {
        /// <summary>
        /// Uses the runtime virtual texture when a terrain virtual texture renderer is active, otherwise blends the layers directly.
        /// </summary>
        Automatic,

        /// <summary>
        /// Always blends the layers directly in the terrain shader.
        /// </summary>
        Direct,
    }

    /// <summary>
    /// Renders a terrain height field. The terrain is placed at the entity position (rotation and scale are ignored);
    /// the entity origin is the corner of the terrain at the first sample.
    /// </summary>
    [DataContract("TerrainComponent")]
    [Display("Terrain", Expand = ExpandRule.Once)]
    [DefaultEntityComponentRenderer(typeof(TerrainProcessor))]
    [ComponentCategory("Terrain")]
    [ComponentOrder(11005)]
    public sealed class TerrainComponent : ActivableEntityComponent
    {
        /// <summary>
        /// The terrain (height field, splat map and material layers).
        /// </summary>
        [DataMember(10)]
        public TerrainData Terrain { get; set; }

        /// <summary>
        /// Distance from the camera up to which the full resolution is used. Each further level of detail doubles that range.
        /// </summary>
        [DataMember(20)]
        [DefaultValue(64.0f)]
        [DataMemberRange(1, 10000, 1, 10, 0)]
        public float LodDistance { get; set; } = 64.0f;

        [DataMember(30)]
        [DefaultValue(TerrainShadingMode.Automatic)]
        public TerrainShadingMode Shading { get; set; } = TerrainShadingMode.Automatic;

        [DataMember(40)]
        [DefaultValue(true)]
        public bool CastShadows { get; set; } = true;

        [DataMember(50)]
        public RenderGroup RenderGroup { get; set; }

        /// <summary>
        /// The runtime virtual texture caching the blended layers (also used by objects blending with the terrain).
        /// </summary>
        [DataMember(60)]
        [Display("Virtual texture", Expand = ExpandRule.Once)]
        public VirtualTexturing.TerrainVirtualTextureSettings VirtualTexture { get; set; } = new();

        /// <summary>
        /// Gets the terrain origin in world space.
        /// </summary>
        [DataMemberIgnore]
        public Vector3 Origin => Entity?.Transform.WorldMatrix.TranslationVector ?? Vector3.Zero;

        /// <summary>
        /// Gets the world height of the terrain at a world position, or <c>null</c> if outside of the terrain.
        /// </summary>
        public float? GetHeight(Vector3 worldPosition)
        {
            if (Terrain == null)
                return null;
            var origin = Origin;
            var localX = worldPosition.X - origin.X;
            var localZ = worldPosition.Z - origin.Z;
            if (!Terrain.Contains(localX, localZ))
                return null;
            return Terrain.SampleHeight(localX, localZ) + origin.Y;
        }

        /// <summary>
        /// Gets the world normal of the terrain at a world position.
        /// </summary>
        public Vector3 GetNormal(Vector3 worldPosition)
        {
            if (Terrain == null)
                return Vector3.UnitY;
            var origin = Origin;
            return Terrain.SampleNormal(worldPosition.X - origin.X, worldPosition.Z - origin.Z);
        }

        /// <summary>
        /// Intersects a world ray with the terrain.
        /// </summary>
        public bool Raycast(Ray ray, float maxDistance, out Vector3 hitPoint)
        {
            hitPoint = default;
            if (Terrain == null)
                return false;
            var origin = Origin;
            if (!Terrain.Raycast(ray.Position - origin, ray.Direction, maxDistance, out var distance))
                return false;
            hitPoint = ray.Position + Vector3.Normalize(ray.Direction) * distance;
            return true;
        }

        /// <summary>
        /// Gets the layer with the highest weight at a world position (e.g. for footstep sounds), or -1 outside of the terrain.
        /// </summary>
        public int GetDominantLayer(Vector3 worldPosition)
        {
            if (Terrain == null)
                return -1;
            var origin = Origin;
            var x = (int)System.MathF.Round((worldPosition.X - origin.X) / Terrain.CellSize);
            var z = (int)System.MathF.Round((worldPosition.Z - origin.Z) / Terrain.CellSize);
            if (x < 0 || z < 0 || x >= Terrain.Resolution || z >= Terrain.Resolution)
                return -1;
            return TerrainSplat.GetDominantLayer(Terrain.Splat[z * Terrain.Resolution + x]);
        }
    }
}

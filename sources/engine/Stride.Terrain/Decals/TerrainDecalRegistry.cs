// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Decals
{
    /// <summary>
    /// The decals drawn into the terrain virtual textures (from <see cref="TerrainDecalComponent"/>s and terrain splines).
    /// The virtual textures render the decals over the terrain layers when they render a page, and render the pages again
    /// when a decal changes.
    /// </summary>
    public sealed class TerrainDecalRegistry : IService
    {
        private readonly List<TerrainDecal> decals = new();
        private long nextSequence;

        public static IService NewInstance(IServiceRegistry services) => new TerrainDecalRegistry();

        public IReadOnlyList<TerrainDecal> Decals => decals;

        /// <summary>
        /// Raised with the world XZ bounds (min, max) of a region where the decals changed.
        /// </summary>
        public event Action<Vector2, Vector2> Changed;

        public void Add(TerrainDecal decal)
        {
            if (decal.Registry == this)
                return;
            decal.Registry?.Remove(decal);
            decal.Registry = this;
            decal.Sequence = nextSequence++;
            decals.Add(decal);
            decal.MarkChanged();
        }

        public void Remove(TerrainDecal decal)
        {
            if (decal.Registry != this)
                return;
            decals.Remove(decal);
            decal.MarkChanged();
            decal.Registry = null;
        }

        /// <summary>
        /// Gets the decals overlapping a world XZ region, in drawing order.
        /// </summary>
        public void Query(Vector2 min, Vector2 max, List<TerrainDecal> result)
        {
            result.Clear();
            foreach (var decal in decals)
            {
                if (decal.HasGeometry && decal.Surface != null
                    && decal.BoundsMax.X >= min.X && decal.BoundsMin.X <= max.X
                    && decal.BoundsMax.Y >= min.Y && decal.BoundsMin.Y <= max.Y)
                    result.Add(decal);
            }
            result.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Sequence.CompareTo(b.Sequence));
        }

        /// <summary>
        /// Renders the virtual textures again over a world XZ region.
        /// </summary>
        public void Invalidate(Vector2 min, Vector2 max) => Changed?.Invoke(min, max);
    }
}

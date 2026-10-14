// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing
{
    /// <summary>
    /// The samples of some tiles of a terrain, used to undo and redo edits.
    /// </summary>
    public sealed class TerrainSnapshot
    {
        public const int TileSize = 64;

        private readonly Dictionary<Int2, Tile> tiles = new();

        private sealed class Tile
        {
            public ushort[] Heights;
            public uint[] Splat;
        }

        public bool IsEmpty => tiles.Count == 0;

        public int TileCount => tiles.Count;

        /// <summary>
        /// Captures the tiles overlapping [min, max] that weren't captured yet (their current state).
        /// </summary>
        public void Capture(TerrainData data, Int2 min, Int2 max)
        {
            for (int tz = min.Y / TileSize; tz <= max.Y / TileSize; tz++)
            {
                for (int tx = min.X / TileSize; tx <= max.X / TileSize; tx++)
                {
                    var key = new Int2(tx, tz);
                    if (!tiles.ContainsKey(key))
                        tiles.Add(key, Read(data, key));
                }
            }
        }

        /// <summary>
        /// Captures the current state of the tiles captured by another snapshot (e.g. after a stroke, for redo).
        /// </summary>
        public static TerrainSnapshot CaptureSameTiles(TerrainData data, TerrainSnapshot other)
        {
            var snapshot = new TerrainSnapshot();
            foreach (var key in other.tiles.Keys)
                snapshot.tiles.Add(key, Read(data, key));
            return snapshot;
        }

        /// <summary>
        /// Writes the captured tiles back to the terrain.
        /// </summary>
        public void Restore(TerrainData data)
        {
            if (tiles.Count == 0)
                return;

            var min = new Int2(int.MaxValue);
            var max = new Int2(int.MinValue);
            foreach (var (key, tile) in tiles)
            {
                GetTileBounds(data, key, out var tileMin, out var tileMax);
                var width = tileMax.X - tileMin.X + 1;
                for (int z = tileMin.Y; z <= tileMax.Y; z++)
                {
                    var row = (z - tileMin.Y) * width;
                    Array.Copy(tile.Heights, row, data.Heights, z * data.Resolution + tileMin.X, width);
                    Array.Copy(tile.Splat, row, data.Splat, z * data.Resolution + tileMin.X, width);
                }
                min = Int2.Min(min, tileMin);
                max = Int2.Max(max, tileMax);
            }
            data.NotifyChanged(min, max, TerrainChangeFlags.All);
        }

        private static Tile Read(TerrainData data, Int2 key)
        {
            GetTileBounds(data, key, out var tileMin, out var tileMax);
            var width = tileMax.X - tileMin.X + 1;
            var height = tileMax.Y - tileMin.Y + 1;
            var tile = new Tile { Heights = new ushort[width * height], Splat = new uint[width * height] };
            for (int z = tileMin.Y; z <= tileMax.Y; z++)
            {
                var row = (z - tileMin.Y) * width;
                Array.Copy(data.Heights, z * data.Resolution + tileMin.X, tile.Heights, row, width);
                Array.Copy(data.Splat, z * data.Resolution + tileMin.X, tile.Splat, row, width);
            }
            return tile;
        }

        private static void GetTileBounds(TerrainData data, Int2 key, out Int2 min, out Int2 max)
        {
            min = new Int2(key.X * TileSize, key.Y * TileSize);
            max = Int2.Min(new Int2(min.X + TileSize - 1, min.Y + TileSize - 1), new Int2(data.Resolution - 1));
        }
    }
}

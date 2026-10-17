// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing;

/// <summary>
/// The samples of some tiles of a terrain (heights, splat and grass density) and possibly its vegetation instances,
/// used to undo and redo edits.
/// </summary>
public sealed class TerrainSnapshot
{
    public const int TileSize = 64;

    private readonly Dictionary<Int2, Tile> tiles = new();
    private readonly TerrainChangeFlags channels;
    private List<TerrainVegetationInstance> vegetation;

    /// <param name="channels">What the tiles capture (heights, splat, grass): an edit of one channel needs only that one.</param>
    public TerrainSnapshot(TerrainChangeFlags channels = TerrainChangeFlags.Heights | TerrainChangeFlags.Splat | TerrainChangeFlags.Grass)
    {
        this.channels = channels;
    }

    private sealed class Tile
    {
        public ushort[] Heights;
        public uint[] Splat;
        // Grass density of the tile per layer (null: the layer had none), in grass samples
        public byte[][] Grass;
    }

    public bool IsEmpty => tiles.Count == 0 && vegetation == null;

    public int TileCount => tiles.Count;

    /// <summary>
    /// Captures the tiles overlapping [min, max] (terrain samples) that weren't captured yet (their current state).
    /// </summary>
    public void Capture(TerrainData data, Int2 min, Int2 max)
    {
        for (int tz = min.Y / TileSize; tz <= max.Y / TileSize; tz++)
        {
            for (int tx = min.X / TileSize; tx <= max.X / TileSize; tx++)
            {
                var key = new Int2(tx, tz);
                if (!tiles.ContainsKey(key))
                    tiles.Add(key, Read(data, key, channels));
            }
        }
    }

    /// <summary>
    /// Captures the vegetation instances, if they weren't captured yet.
    /// </summary>
    public void CaptureVegetation(TerrainData data)
    {
        vegetation ??= new List<TerrainVegetationInstance>(data.Vegetation);
    }

    /// <summary>
    /// Captures the current state of what another snapshot captured (e.g. after a stroke, for redo).
    /// </summary>
    public static TerrainSnapshot CaptureSameTiles(TerrainData data, TerrainSnapshot other)
    {
        var snapshot = new TerrainSnapshot(other.channels);
        foreach (var key in other.tiles.Keys)
            snapshot.tiles.Add(key, Read(data, key, other.channels));
        if (other.vegetation != null)
            snapshot.CaptureVegetation(data);
        return snapshot;
    }

    /// <summary>
    /// Writes the captured tiles (and vegetation) back to the terrain.
    /// </summary>
    public void Restore(TerrainData data)
    {
        if (vegetation != null)
        {
            data.Vegetation.Clear();
            data.Vegetation.AddRange(vegetation);
            data.NotifyChanged(Int2.Zero, new Int2(data.Resolution - 1), TerrainChangeFlags.Vegetation);
        }
        if (tiles.Count == 0)
            return;

        var min = new Int2(int.MaxValue);
        var max = new Int2(int.MinValue);
        var grassSize = data.GrassResolution;
        foreach (var (key, tile) in tiles)
        {
            GetTileBounds(data, key, out var tileMin, out var tileMax);
            var width = tileMax.X - tileMin.X + 1;
            for (int z = tileMin.Y; z <= tileMax.Y; z++)
            {
                var row = (z - tileMin.Y) * width;
                if (tile.Heights != null)
                    Array.Copy(tile.Heights, row, data.Heights, z * data.Resolution + tileMin.X, width);
                if (tile.Splat != null)
                    Array.Copy(tile.Splat, row, data.Splat, z * data.Resolution + tileMin.X, width);
            }

            GetGrassTileBounds(data, key, out var grassMin, out var grassMax);
            var grassWidth = grassMax.X - grassMin.X + 1;
            for (int layer = 0; layer < TerrainData.MaxGrassLayers && tile.Grass != null; layer++)
            {
                var source = tile.Grass[layer];
                // A layer the tile had no density for is cleared, unless it still has none at all
                var target = source != null ? data.GetOrCreateGrassDensity(layer) : data.GetGrassDensity(layer);
                if (target == null)
                    continue;
                for (int z = grassMin.Y; z <= grassMax.Y; z++)
                {
                    if (source != null)
                        Array.Copy(source, (z - grassMin.Y) * grassWidth, target, z * grassSize + grassMin.X, grassWidth);
                    else
                        Array.Clear(target, z * grassSize + grassMin.X, grassWidth);
                }
            }

            min = Int2.Min(min, tileMin);
            max = Int2.Max(max, tileMax);
        }
        data.NotifyChanged(min, max, channels & (TerrainChangeFlags.Heights | TerrainChangeFlags.Splat | TerrainChangeFlags.Grass));
    }

    private static Tile Read(TerrainData data, Int2 key, TerrainChangeFlags channels)
    {
        GetTileBounds(data, key, out var tileMin, out var tileMax);
        var width = tileMax.X - tileMin.X + 1;
        var height = tileMax.Y - tileMin.Y + 1;
        var tile = new Tile
        {
            Heights = (channels & TerrainChangeFlags.Heights) != 0 ? new ushort[width * height] : null,
            Splat = (channels & TerrainChangeFlags.Splat) != 0 ? new uint[width * height] : null,
        };
        for (int z = tileMin.Y; z <= tileMax.Y; z++)
        {
            var row = (z - tileMin.Y) * width;
            if (tile.Heights != null)
                Array.Copy(data.Heights, z * data.Resolution + tileMin.X, tile.Heights, row, width);
            if (tile.Splat != null)
                Array.Copy(data.Splat, z * data.Resolution + tileMin.X, tile.Splat, row, width);
        }
        if ((channels & TerrainChangeFlags.Grass) == 0)
            return tile;
        tile.Grass = new byte[TerrainData.MaxGrassLayers][];

        GetGrassTileBounds(data, key, out var grassMin, out var grassMax);
        var grassWidth = grassMax.X - grassMin.X + 1;
        var grassHeight = grassMax.Y - grassMin.Y + 1;
        var grassSize = data.GrassResolution;
        for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
        {
            if (data.GetGrassDensity(layer) is not { } density)
                continue;
            var copy = new byte[grassWidth * grassHeight];
            for (int z = grassMin.Y; z <= grassMax.Y; z++)
                Array.Copy(density, z * grassSize + grassMin.X, copy, (z - grassMin.Y) * grassWidth, grassWidth);
            tile.Grass[layer] = copy;
        }
        return tile;
    }

    private static void GetTileBounds(TerrainData data, Int2 key, out Int2 min, out Int2 max)
    {
        min = new Int2(key.X * TileSize, key.Y * TileSize);
        max = Int2.Min(new Int2(min.X + TileSize - 1, min.Y + TileSize - 1), new Int2(data.Resolution - 1));
    }

    // The grass samples of a tile: one every two terrain samples
    private static void GetGrassTileBounds(TerrainData data, Int2 key, out Int2 min, out Int2 max)
    {
        const int grassTile = TileSize / 2;
        min = new Int2(key.X * grassTile, key.Y * grassTile);
        max = Int2.Min(new Int2(min.X + grassTile - 1, min.Y + grassTile - 1), new Int2(data.GrassResolution - 1));
    }
}

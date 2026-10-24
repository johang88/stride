// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing;

/// <summary>
/// The samples of some tiles of a terrain (heights, splat, grass and fog density) and possibly its vegetation instances,
/// used to undo and redo edits.
/// </summary>
public sealed class TerrainSnapshot
{
    public const int TileSize = 64;

    private readonly Dictionary<Int2, Tile> tiles = new();
    private readonly TerrainChangeFlags channels;
    private List<TerrainVegetationInstance> vegetation;

    /// <param name="channels">What the tiles capture (heights, splat, grass, fog): an edit of one channel needs only that one.</param>
    public TerrainSnapshot(TerrainChangeFlags channels = TerrainChangeFlags.Heights | TerrainChangeFlags.Splat | TerrainChangeFlags.Grass | TerrainChangeFlags.Fog)
    {
        this.channels = channels;
    }

    private sealed class Tile
    {
        public ushort[] Heights;
        public uint[] Splat;
        // Grass density of the tile per layer (null: the layer had none), in grass samples
        public byte[][] Grass;
        // Fog density of the tile per layer (null: the layer had none), in fog samples
        public byte[][] Fog;
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

            if (tile.Grass != null)
            {
                GetLayerTileBounds(key, 2, data.GrassResolution, out var grassMin, out var grassMax);
                for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
                {
                    // A layer the tile had no density for is cleared, unless it still has none at all
                    var target = tile.Grass[layer] != null ? data.GetOrCreateGrassDensity(layer) : data.GetGrassDensity(layer);
                    RestoreLayer(tile.Grass[layer], target, grassSize, grassMin, grassMax);
                }
            }
            if (tile.Fog != null)
            {
                GetLayerTileBounds(key, TerrainData.FogSpacing, data.FogResolution, out var fogMin, out var fogMax);
                for (int layer = 0; layer < TerrainData.MaxFogLayers; layer++)
                {
                    var target = tile.Fog[layer] != null ? data.GetOrCreateFogDensity(layer) : data.GetFogDensity(layer);
                    RestoreLayer(tile.Fog[layer], target, data.FogResolution, fogMin, fogMax);
                }
            }

            min = Int2.Min(min, tileMin);
            max = Int2.Max(max, tileMax);
        }
        data.NotifyChanged(min, max, channels & (TerrainChangeFlags.Heights | TerrainChangeFlags.Splat | TerrainChangeFlags.Grass | TerrainChangeFlags.Fog));
    }

    private static void RestoreLayer(byte[] source, byte[] target, int size, Int2 min, Int2 max)
    {
        if (target == null)
            return;
        var width = max.X - min.X + 1;
        for (int z = min.Y; z <= max.Y; z++)
        {
            if (source != null)
                Array.Copy(source, (z - min.Y) * width, target, z * size + min.X, width);
            else
                Array.Clear(target, z * size + min.X, width);
        }
    }

    // The samples of a tile of a layer with one sample every spacing terrain samples (null where the layer has none)
    private static byte[][] ReadLayers(Int2 key, int spacing, int size, int count, Func<int, byte[]> getLayer)
    {
        GetLayerTileBounds(key, spacing, size, out var min, out var max);
        var width = max.X - min.X + 1;
        var height = max.Y - min.Y + 1;
        var layers = new byte[count][];
        for (int layer = 0; layer < count; layer++)
        {
            if (getLayer(layer) is not { } density)
                continue;
            var copy = new byte[width * height];
            for (int z = min.Y; z <= max.Y; z++)
                Array.Copy(density, z * size + min.X, copy, (z - min.Y) * width, width);
            layers[layer] = copy;
        }
        return layers;
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
        if ((channels & TerrainChangeFlags.Grass) != 0)
            tile.Grass = ReadLayers(key, 2, data.GrassResolution, TerrainData.MaxGrassLayers, data.GetGrassDensity);
        if ((channels & TerrainChangeFlags.Fog) != 0)
            tile.Fog = ReadLayers(key, TerrainData.FogSpacing, data.FogResolution, TerrainData.MaxFogLayers, data.GetFogDensity);
        return tile;
    }

    private static void GetTileBounds(TerrainData data, Int2 key, out Int2 min, out Int2 max)
    {
        min = new Int2(key.X * TileSize, key.Y * TileSize);
        max = Int2.Min(new Int2(min.X + TileSize - 1, min.Y + TileSize - 1), new Int2(data.Resolution - 1));
    }

    // The samples of a tile of a layer with one sample every spacing terrain samples (grass: 2, fog: 4)
    private static void GetLayerTileBounds(Int2 key, int spacing, int size, out Int2 min, out Int2 max)
    {
        var layerTile = TileSize / spacing;
        min = new Int2(key.X * layerTile, key.Y * layerTile);
        max = Int2.Min(new Int2(min.X + layerTile - 1, min.Y + layerTile - 1), new Int2(size - 1));
    }
}

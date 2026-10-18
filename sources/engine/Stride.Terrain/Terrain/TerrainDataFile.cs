// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Stride.Terrain;

/// <summary>
/// Reads and writes the editable samples of a terrain (the source file of a terrain asset).
/// </summary>
/// <remarks>
/// Layout: "SDTD" magic, version, resolution, cell size, min/max height, then the heights (row-delta encoded),
/// the packed splat samples, a mask of the grass layers that have density followed by their density samples,
/// the vegetation instances, and a mask of the fog layers that have density followed by their density samples; each block
/// Brotli-compressed with a length prefix.
/// </remarks>
public static class TerrainDataFile
{
    public const string Extension = ".sdterraindata";

    private const uint Magic = 0x44544453; // "SDTD"
    private const int Version = 3;

    public static void Write(string path, TerrainData data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Write to a temporary file first so a failure never corrupts the existing data
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
            Write(stream, data);
        File.Move(temporaryPath, path, true);
    }

    public static void Write(Stream stream, TerrainData data)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(data.Resolution);
        writer.Write(data.CellSize);
        writer.Write(data.MinHeight);
        writer.Write(data.MaxHeight);

        // Row deltas compress much better than raw heights
        var resolution = data.Resolution;
        var deltas = new ushort[data.Heights.Length];
        for (int z = 0; z < resolution; z++)
        {
            var row = z * resolution;
            ushort previous = 0;
            for (int x = 0; x < resolution; x++)
            {
                var value = data.Heights[row + x];
                deltas[row + x] = (ushort)(value - previous);
                previous = value;
            }
        }

        WriteBlock(writer, MemoryMarshal.AsBytes(deltas.AsSpan()));
        WriteBlock(writer, MemoryMarshal.AsBytes(data.Splat.AsSpan()));

        var grassLayers = 0;
        for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
        {
            if (data.GetGrassDensity(layer) != null)
                grassLayers |= 1 << layer;
        }
        writer.Write(grassLayers);
        for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
        {
            if (data.GetGrassDensity(layer) is { } density)
                WriteBlock(writer, density);
        }

        writer.Write(data.Vegetation.Count);
        WriteBlock(writer, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.Vegetation)));

        var fogLayers = 0;
        for (int layer = 0; layer < TerrainData.MaxFogLayers; layer++)
        {
            if (data.GetFogDensity(layer) != null)
                fogLayers |= 1 << layer;
        }
        writer.Write(fogLayers);
        for (int layer = 0; layer < TerrainData.MaxFogLayers; layer++)
        {
            if (data.GetFogDensity(layer) is { } density)
                WriteBlock(writer, density);
        }
    }

    public static TerrainData Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static TerrainData Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a terrain data file.");
        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported terrain data file version {version}.");

        var resolution = reader.ReadInt32();
        var cellSize = reader.ReadSingle();
        var minHeight = reader.ReadSingle();
        var maxHeight = reader.ReadSingle();
        var data = new TerrainData(resolution, cellSize, minHeight, maxHeight);

        ReadBlock(reader, MemoryMarshal.AsBytes(data.Heights.AsSpan()));
        for (int z = 0; z < resolution; z++)
        {
            var row = z * resolution;
            ushort previous = 0;
            for (int x = 0; x < resolution; x++)
            {
                previous = (ushort)(previous + data.Heights[row + x]);
                data.Heights[row + x] = previous;
            }
        }

        ReadBlock(reader, MemoryMarshal.AsBytes(data.Splat.AsSpan()));

        var grassLayers = reader.ReadInt32();
        for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
        {
            if ((grassLayers & (1 << layer)) != 0)
                ReadBlock(reader, data.GetOrCreateGrassDensity(layer));
        }

        var instanceCount = reader.ReadInt32();
        data.Vegetation.AddRange(new TerrainVegetationInstance[instanceCount]);
        ReadBlock(reader, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.Vegetation)));

        var fogLayers = reader.ReadInt32();
        for (int layer = 0; layer < TerrainData.MaxFogLayers; layer++)
        {
            if ((fogLayers & (1 << layer)) != 0)
                ReadBlock(reader, data.GetOrCreateFogDensity(layer));
        }
        return data;
    }

    /// <summary>
    /// Creates a copy of a terrain with a different resolution, cell size or height range (heights are resampled bilinearly,
    /// splat samples use the nearest sample). The world size changes unless the cell size is adjusted accordingly.
    /// </summary>
    public static TerrainData Resample(TerrainData source, int resolution, float cellSize, float minHeight, float maxHeight)
    {
        var result = new TerrainData(resolution, cellSize, minHeight, maxHeight) { Material = source.Material };
        var sameGrid = resolution == source.Resolution;
        var scale = (source.Resolution - 1) / (float)(resolution - 1);
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                var index = z * resolution + x;
                if (sameGrid)
                {
                    result.Heights[index] = result.Quantize(source.Dequantize(source.Heights[index]));
                    result.Splat[index] = source.Splat[index];
                    continue;
                }

                var sx = x * scale;
                var sz = z * scale;
                result.Heights[index] = result.Quantize(source.SampleHeight(sx * source.CellSize, sz * source.CellSize));
                var nx = Math.Clamp((int)MathF.Round(sx), 0, source.Resolution - 1);
                var nz = Math.Clamp((int)MathF.Round(sz), 0, source.Resolution - 1);
                result.Splat[index] = source.Splat[nz * source.Resolution + nx];
            }
        }

        // Grass and fog density by position (nearest sample), vegetation as is (positions are in meters)
        for (int layer = 0; layer < TerrainData.MaxGrassLayers; layer++)
        {
            if (source.GetGrassDensity(layer) is { } density)
                ResampleNearest(density, source.GrassResolution, source.GrassCellSize, result.GetOrCreateGrassDensity(layer), result.GrassResolution, result.GrassCellSize);
        }
        for (int layer = 0; layer < TerrainData.MaxFogLayers; layer++)
        {
            if (source.GetFogDensity(layer) is { } density)
                ResampleNearest(density, source.FogResolution, source.FogCellSize, result.GetOrCreateFogDensity(layer), result.FogResolution, result.FogCellSize);
        }
        result.Vegetation.AddRange(source.Vegetation);
        return result;
    }

    private static void ResampleNearest(byte[] source, int sourceSize, float sourceCellSize, byte[] target, int targetSize, float targetCellSize)
    {
        for (int z = 0; z < targetSize; z++)
        {
            for (int x = 0; x < targetSize; x++)
            {
                var sx = Math.Clamp((int)MathF.Round(x * targetCellSize / sourceCellSize), 0, sourceSize - 1);
                var sz = Math.Clamp((int)MathF.Round(z * targetCellSize / sourceCellSize), 0, sourceSize - 1);
                target[z * targetSize + x] = source[sz * sourceSize + sx];
            }
        }
    }

    private static void WriteBlock(BinaryWriter writer, ReadOnlySpan<byte> data)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest, true))
            brotli.Write(data);
        writer.Write(data.Length);
        writer.Write((int)compressed.Length);
        writer.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
    }

    private static void ReadBlock(BinaryReader reader, Span<byte> destination)
    {
        var length = reader.ReadInt32();
        var compressedLength = reader.ReadInt32();
        if (length != destination.Length)
            throw new InvalidDataException("Terrain data block size mismatch.");
        var compressed = reader.ReadBytes(compressedLength);
        using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var read = 0;
        while (read < length)
        {
            var n = brotli.Read(destination.Slice(read));
            if (n <= 0)
                throw new InvalidDataException("Truncated terrain data block.");
            read += n;
        }
    }
}

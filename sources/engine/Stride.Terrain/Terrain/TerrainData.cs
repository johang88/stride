// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;

namespace Stride.Terrain;

/// <summary>
/// What changed in a region of a <see cref="TerrainData"/>.
/// </summary>
[Flags]
public enum TerrainChangeFlags
{
    None = 0,
    Heights = 1,
    Splat = 2,
    /// <summary>
    /// Grass density (the region is in terrain samples, see <see cref="TerrainData.GrassResolution"/>).
    /// </summary>
    Grass = 4,
    /// <summary>
    /// The vegetation instances (the region covers the whole terrain).
    /// </summary>
    Vegetation = 8,
    All = Heights | Splat | Grass | Vegetation,
}

/// <summary>
/// A placed tree or other large plant of a terrain (one of the vegetation types of its <see cref="TerrainComponent"/>).
/// </summary>
[DataContract("TerrainVegetationInstance")]
[StructLayout(LayoutKind.Sequential)]
public struct TerrainVegetationInstance
{
    /// <summary>
    /// Index of the vegetation type in <see cref="TerrainComponent.Vegetation"/>.
    /// </summary>
    public int Type;

    /// <summary>
    /// Position on the terrain (local XZ, meters).
    /// </summary>
    public Vector2 Position;

    /// <summary>
    /// Height above the terrain surface (meters): instances follow the terrain when it's sculpted.
    /// </summary>
    public float HeightOffset;

    /// <summary>
    /// Rotation around the vertical axis (radians).
    /// </summary>
    public float Rotation;

    public float Scale;
}

/// <summary>
/// Event raised when a region of a <see cref="TerrainData"/> is modified.
/// </summary>
public readonly record struct TerrainDataChangedEventArgs(Int2 Min, Int2 Max, TerrainChangeFlags Flags);

/// <summary>
/// The height and material-weight samples of a terrain, its grass density and its vegetation instances.
/// </summary>
/// <remarks>
/// Samples are laid out on a square grid of <see cref="Resolution"/> x <see cref="Resolution"/> points spaced by <see cref="CellSize"/>.
/// Sample (x, z) is at local position (x * CellSize, height, z * CellSize). Heights are 16-bit normalized values mapped to
/// [<see cref="MinHeight"/>, <see cref="MaxHeight"/>]; splat values are packed with <see cref="TerrainSplat"/>.
/// </remarks>
[DataContract("TerrainData")]
[ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainData>), Profile = "Content")]
[DataSerializer(typeof(TerrainDataSerializer))]
[ContentSerializer(typeof(DataContentSerializer<TerrainData>))]
public sealed class TerrainData
{
    private const int SerializationVersion = 2;

    /// <summary>
    /// Number of grass density channels (grass layers of <see cref="TerrainComponent.Grass"/>).
    /// </summary>
    public const int MaxGrassLayers = 8;

    private ushort[] heights;
    private uint[] splat;
    private byte[][] grassDensity = new byte[MaxGrassLayers][];
    private List<TerrainVegetationInstance> vegetation = new();
    private float minHeight;
    private float maxHeight;
    private float heightScale;

    public TerrainData()
    {
    }

    public TerrainData(int resolution, float cellSize, float minHeight, float maxHeight)
    {
        if (resolution < 2)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        Resolution = resolution;
        CellSize = cellSize;
        this.minHeight = minHeight;
        this.maxHeight = maxHeight;
        heights = new ushort[resolution * resolution];
        splat = new uint[resolution * resolution];
        Array.Fill(splat, TerrainSplat.Encode(0));
        UpdateScale();
    }

    /// <summary>
    /// The material layers the splat map refers to.
    /// </summary>
    public TerrainMaterial Material { get; set; }

    /// <summary>
    /// Number of samples along each side.
    /// </summary>
    public int Resolution { get; private set; }

    /// <summary>
    /// Distance in meters between two samples.
    /// </summary>
    public float CellSize { get; private set; }

    public float MinHeight => minHeight;

    public float MaxHeight => maxHeight;

    /// <summary>
    /// Size of the terrain in meters along each side.
    /// </summary>
    public float Size => (Resolution - 1) * CellSize;

    /// <summary>
    /// Height samples (row-major, z then x), 16-bit normalized over [<see cref="MinHeight"/>, <see cref="MaxHeight"/>].
    /// </summary>
    public ushort[] Heights => heights;

    /// <summary>
    /// Packed splat samples (row-major), see <see cref="TerrainSplat"/>.
    /// </summary>
    public uint[] Splat => splat;

    /// <summary>
    /// Number of grass density samples along each side: one every two terrain samples.
    /// </summary>
    public int GrassResolution => (Resolution - 1) / 2 + 1;

    /// <summary>
    /// Distance in meters between two grass density samples.
    /// </summary>
    public float GrassCellSize => CellSize * 2;

    /// <summary>
    /// The density samples (0-255, row-major) of a grass layer, or null when nothing was painted on it.
    /// </summary>
    public byte[] GetGrassDensity(int layer) => (uint)layer < MaxGrassLayers ? grassDensity[layer] : null;

    /// <summary>
    /// The density samples of a grass layer, allocated (empty) on first use.
    /// </summary>
    public byte[] GetOrCreateGrassDensity(int layer)
    {
        if ((uint)layer >= MaxGrassLayers)
            throw new ArgumentOutOfRangeException(nameof(layer));
        return grassDensity[layer] ??= new byte[GrassResolution * GrassResolution];
    }

    /// <summary>
    /// The placed trees and other large plants.
    /// </summary>
    public List<TerrainVegetationInstance> Vegetation => vegetation;

    /// <summary>
    /// Incremented each time the data is modified.
    /// </summary>
    [DataMemberIgnore]
    public int Revision { get; private set; }

    /// <summary>
    /// Raised (on the thread that modified the data) after a region was modified.
    /// </summary>
    public event EventHandler<TerrainDataChangedEventArgs> Changed;

    /// <summary>
    /// Notifies listeners (renderer, colliders...) that samples in [min, max] (inclusive) were modified.
    /// </summary>
    public void NotifyChanged(Int2 min, Int2 max, TerrainChangeFlags flags)
    {
        min = Int2.Max(min, Int2.Zero);
        max = Int2.Min(max, new Int2(Resolution - 1));
        if (min.X > max.X || min.Y > max.Y)
            return;
        Revision++;
        Changed?.Invoke(this, new TerrainDataChangedEventArgs(min, max, flags));
    }

    /// <summary>
    /// Creates a copy of the samples, grass and vegetation (the material reference is shared).
    /// </summary>
    public TerrainData Clone()
    {
        var clone = new TerrainData(Resolution, CellSize, minHeight, maxHeight) { Material = Material };
        Array.Copy(heights, clone.heights, heights.Length);
        Array.Copy(splat, clone.splat, splat.Length);
        CopyVegetationFrom(this, clone);
        return clone;
    }

    private static void CopyVegetationFrom(TerrainData source, TerrainData target)
    {
        for (int layer = 0; layer < MaxGrassLayers; layer++)
            target.grassDensity[layer] = (byte[])source.grassDensity[layer]?.Clone();
        target.vegetation = new List<TerrainVegetationInstance>(source.vegetation);
    }

    /// <summary>
    /// Copies the samples of another terrain with the same resolution and height range.
    /// </summary>
    public void CopySamplesFrom(TerrainData other)
    {
        if (other.Resolution != Resolution)
            throw new ArgumentException("Resolutions differ.", nameof(other));
        if (other.minHeight == minHeight && other.maxHeight == maxHeight)
            Array.Copy(other.heights, heights, heights.Length);
        else
            for (int i = 0; i < heights.Length; i++)
                heights[i] = Quantize(other.Dequantize(other.heights[i]));
        Array.Copy(other.splat, splat, splat.Length);
        CopyVegetationFrom(other, this);
        NotifyChanged(Int2.Zero, new Int2(Resolution - 1), TerrainChangeFlags.All);
    }

    /// <summary>
    /// Changes the height range, re-quantizing the existing heights so the terrain keeps its shape (clamped to the new range).
    /// </summary>
    public void SetHeightRange(float newMin, float newMax)
    {
        if (newMax <= newMin)
            throw new ArgumentException("The maximum height must be greater than the minimum height.");
        if (newMin == minHeight && newMax == maxHeight)
            return;

        var oldMin = minHeight;
        var oldScale = heightScale;
        minHeight = newMin;
        maxHeight = newMax;
        UpdateScale();
        for (int i = 0; i < heights.Length; i++)
            heights[i] = Quantize(oldMin + heights[i] * oldScale);
        NotifyChanged(Int2.Zero, new Int2(Resolution - 1), TerrainChangeFlags.Heights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Dequantize(ushort value) => minHeight + value * heightScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Quantize(float height)
    {
        var v = (height - minHeight) / heightScale;
        return (ushort)Math.Clamp((int)MathF.Round(v), 0, ushort.MaxValue);
    }

    /// <summary>
    /// Gets the height of a sample, clamping coordinates to the grid.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetHeight(int x, int z)
    {
        x = Math.Clamp(x, 0, Resolution - 1);
        z = Math.Clamp(z, 0, Resolution - 1);
        return Dequantize(heights[z * Resolution + x]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetHeight(int x, int z, float height) => heights[z * Resolution + x] = Quantize(height);

    /// <summary>
    /// Samples the height at a local position (meters), matching the triangulation used for rendering and collision.
    /// </summary>
    public float SampleHeight(float localX, float localZ)
    {
        var fx = localX / CellSize;
        var fz = localZ / CellSize;
        var x0 = (int)MathF.Floor(fx);
        var z0 = (int)MathF.Floor(fz);
        var tx = fx - x0;
        var tz = fz - z0;
        var h00 = GetHeight(x0, z0);
        var h10 = GetHeight(x0 + 1, z0);
        var h01 = GetHeight(x0, z0 + 1);
        var h11 = GetHeight(x0 + 1, z0 + 1);

        // Each cell is split along its (0,0)-(1,1) diagonal
        return tx >= tz
            ? h00 + (h10 - h00) * tx + (h11 - h10) * tz
            : h00 + (h11 - h01) * tx + (h01 - h00) * tz;
    }

    /// <summary>
    /// Computes the (unnormalized-input, normalized-output) surface normal at a local position from central differences.
    /// </summary>
    public Vector3 SampleNormal(float localX, float localZ)
    {
        var d = CellSize;
        var hl = SampleHeight(localX - d, localZ);
        var hr = SampleHeight(localX + d, localZ);
        var hd = SampleHeight(localX, localZ - d);
        var hu = SampleHeight(localX, localZ + d);
        var normal = new Vector3(hl - hr, 2 * d, hd - hu);
        normal.Normalize();
        return normal;
    }

    /// <summary>
    /// Samples the density (0-1) of a grass layer at a local position (bilinear).
    /// </summary>
    public float SampleGrassDensity(int layer, float localX, float localZ)
    {
        var density = GetGrassDensity(layer);
        if (density == null)
            return 0;
        var size = GrassResolution;
        var fx = Math.Clamp(localX / GrassCellSize, 0, size - 1);
        var fz = Math.Clamp(localZ / GrassCellSize, 0, size - 1);
        var x0 = Math.Min((int)fx, size - 2);
        var z0 = Math.Min((int)fz, size - 2);
        var tx = fx - x0;
        var tz = fz - z0;
        var row0 = z0 * size + x0;
        var row1 = row0 + size;
        var a = density[row0] + (density[row0 + 1] - density[row0]) * tx;
        var b = density[row1] + (density[row1 + 1] - density[row1]) * tx;
        return (a + (b - a) * tz) / 255.0f;
    }

    /// <summary>
    /// Gets whether a local position lies within the terrain bounds (XZ).
    /// </summary>
    public bool Contains(float localX, float localZ) => localX >= 0 && localZ >= 0 && localX <= Size && localZ <= Size;

    /// <summary>
    /// Intersects a ray (in terrain local space) with the height field.
    /// </summary>
    /// <returns><c>true</c> if hit, with the distance along the (normalized) direction.</returns>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float distance)
    {
        distance = 0;
        direction.Normalize();

        // Clip against the terrain box
        var box = new BoundingBox(new Vector3(0, minHeight, 0), new Vector3(Size, maxHeight, Size));
        var ray = new Ray(origin, direction);
        float tEnter;
        if (box.Contains(in origin) == ContainmentType.Contains)
            tEnter = 0;
        else if (!ray.Intersects(in box, out tEnter))
            return false;

        // Walk the grid cells crossed by the ray (2D DDA) and test their two triangles
        var start = origin + direction * tEnter;
        var tMax = maxDistance - tEnter;
        if (tMax <= 0)
            return false;

        var cellX = Math.Clamp((int)MathF.Floor(start.X / CellSize), 0, Resolution - 2);
        var cellZ = Math.Clamp((int)MathF.Floor(start.Z / CellSize), 0, Resolution - 2);
        var stepX = direction.X >= 0 ? 1 : -1;
        var stepZ = direction.Z >= 0 ? 1 : -1;
        var tDeltaX = direction.X != 0 ? MathF.Abs(CellSize / direction.X) : float.PositiveInfinity;
        var tDeltaZ = direction.Z != 0 ? MathF.Abs(CellSize / direction.Z) : float.PositiveInfinity;
        var nextX = (cellX + (stepX > 0 ? 1 : 0)) * CellSize;
        var nextZ = (cellZ + (stepZ > 0 ? 1 : 0)) * CellSize;
        var tNextX = direction.X != 0 ? (nextX - start.X) / direction.X : float.PositiveInfinity;
        var tNextZ = direction.Z != 0 ? (nextZ - start.Z) / direction.Z : float.PositiveInfinity;

        var localRay = new Ray(start, direction);
        var maxSteps = 4 * Resolution;
        for (int i = 0; i < maxSteps; i++)
        {
            if (cellX < 0 || cellZ < 0 || cellX >= Resolution - 1 || cellZ >= Resolution - 1)
                return false;

            if (IntersectCell(in localRay, cellX, cellZ, out var t) && t <= tMax)
            {
                distance = tEnter + t;
                return true;
            }

            if (tNextX < tNextZ)
            {
                if (tNextX > tMax)
                    return false;
                cellX += stepX;
                tNextX += tDeltaX;
            }
            else
            {
                if (tNextZ > tMax)
                    return false;
                cellZ += stepZ;
                tNextZ += tDeltaZ;
            }
        }
        return false;
    }

    private bool IntersectCell(in Ray ray, int x, int z, out float t)
    {
        var p00 = new Vector3(x * CellSize, GetHeight(x, z), z * CellSize);
        var p10 = new Vector3((x + 1) * CellSize, GetHeight(x + 1, z), z * CellSize);
        var p01 = new Vector3(x * CellSize, GetHeight(x, z + 1), (z + 1) * CellSize);
        var p11 = new Vector3((x + 1) * CellSize, GetHeight(x + 1, z + 1), (z + 1) * CellSize);

        var hit0 = CollisionHelper.RayIntersectsTriangle(in ray, in p00, in p11, in p10, out float t0);
        var hit1 = CollisionHelper.RayIntersectsTriangle(in ray, in p00, in p01, in p11, out float t1);
        if (hit0 && (!hit1 || t0 <= t1))
        {
            t = t0;
            return true;
        }
        t = t1;
        return hit1;
    }

    private void UpdateScale()
    {
        heightScale = (maxHeight - minHeight) / ushort.MaxValue;
    }

    internal sealed class TerrainDataSerializer : DataSerializer<TerrainData>
    {
        public override void PreSerialize(ref TerrainData obj, ArchiveMode mode, SerializationStream stream)
        {
            if (mode == ArchiveMode.Deserialize && obj == null)
                obj = new TerrainData();
        }

        public override void Serialize(ref TerrainData obj, ArchiveMode mode, SerializationStream stream)
        {
            var version = SerializationVersion;
            stream.Serialize(ref version);
            if (version != SerializationVersion)
                throw new InvalidOperationException($"Unsupported terrain data version {version}.");

            var resolution = obj.Resolution;
            var cellSize = obj.CellSize;
            stream.Serialize(ref resolution);
            stream.Serialize(ref cellSize);
            stream.Serialize(ref obj.minHeight);
            stream.Serialize(ref obj.maxHeight);

            if (mode == ArchiveMode.Deserialize)
            {
                obj.Resolution = resolution;
                obj.CellSize = cellSize;
                obj.heights = new ushort[resolution * resolution];
                obj.splat = new uint[resolution * resolution];
                obj.UpdateScale();
            }

            var material = obj.Material;
            stream.SerializeExtended(ref material, mode);
            obj.Material = material;

            stream.Serialize(MemoryMarshal.AsBytes(obj.heights.AsSpan()));
            stream.Serialize(MemoryMarshal.AsBytes(obj.splat.AsSpan()));

            // Grass layers that have density, then the vegetation instances
            var grassLayers = 0;
            for (int layer = 0; layer < MaxGrassLayers; layer++)
            {
                if (obj.grassDensity[layer] != null)
                    grassLayers |= 1 << layer;
            }
            stream.Serialize(ref grassLayers);
            for (int layer = 0; layer < MaxGrassLayers; layer++)
            {
                if ((grassLayers & (1 << layer)) == 0)
                    continue;
                if (mode == ArchiveMode.Deserialize)
                    obj.grassDensity[layer] = new byte[obj.GrassResolution * obj.GrassResolution];
                stream.Serialize(obj.grassDensity[layer].AsSpan());
            }

            var instanceCount = obj.vegetation.Count;
            stream.Serialize(ref instanceCount);
            if (mode == ArchiveMode.Deserialize)
                obj.vegetation = new List<TerrainVegetationInstance>(new TerrainVegetationInstance[instanceCount]);
            stream.Serialize(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(obj.vegetation)));
        }
    }
}

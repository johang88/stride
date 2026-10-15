// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Terrain.Rendering;

/// <summary>
/// GPU copies of a <see cref="TerrainData"/>: a mip-mapped R16 height map and an R32 splat map, kept in sync with region updates.
/// </summary>
public sealed class TerrainGpuResources : IDisposable
{
    private readonly object dirtyLock = new();
    private readonly ushort[][] heightMips;
    private readonly int[] mipSizes;
    private Int2 dirtyMin;
    private Int2 dirtyMax;
    private TerrainChangeFlags dirtyFlags;

    public TerrainGpuResources(GraphicsDevice device, TerrainData data)
    {
        Data = data;
        var resolution = data.Resolution;

        var mipCount = 1;
        while ((resolution >> mipCount) > 0)
            mipCount++;
        mipSizes = new int[mipCount];
        heightMips = new ushort[mipCount][];
        for (int mip = 0; mip < mipCount; mip++)
        {
            mipSizes[mip] = Math.Max(1, resolution >> mip);
            heightMips[mip] = mip == 0 ? data.Heights : new ushort[mipSizes[mip] * mipSizes[mip]];
        }
        for (int mip = 1; mip < mipCount; mip++)
            Downsample(mip, Int2.Zero, new Int2(mipSizes[mip] - 1));

        Heightmap = Texture.New2D(device, resolution, resolution, mipCount, PixelFormat.R16_UNorm, TextureFlags.ShaderResource);
        Heightmap.Name = "TerrainHeightmap";
        SplatMap = Texture.New2D(device, resolution, resolution, PixelFormat.R32_UInt, TextureFlags.ShaderResource);
        SplatMap.Name = "TerrainSplatMap";

        MarkDirty(Int2.Zero, new Int2(resolution - 1), TerrainChangeFlags.All);
        data.Changed += OnDataChanged;
    }

    public TerrainData Data { get; }

    public Texture Heightmap { get; }

    public Texture SplatMap { get; }

    public void Dispose()
    {
        Data.Changed -= OnDataChanged;
        Heightmap.Dispose();
        SplatMap.Dispose();
    }

    private void OnDataChanged(object sender, TerrainDataChangedEventArgs e) => MarkDirty(e.Min, e.Max, e.Flags);

    private void MarkDirty(Int2 min, Int2 max, TerrainChangeFlags flags)
    {
        lock (dirtyLock)
        {
            if (dirtyFlags == TerrainChangeFlags.None)
            {
                dirtyMin = min;
                dirtyMax = max;
            }
            else
            {
                dirtyMin = Int2.Min(dirtyMin, min);
                dirtyMax = Int2.Max(dirtyMax, max);
            }
            dirtyFlags |= flags;
        }
    }

    /// <summary>
    /// Uploads the regions modified since the last call.
    /// </summary>
    public unsafe void Upload(CommandList commandList)
    {
        Int2 min, max;
        TerrainChangeFlags flags;
        lock (dirtyLock)
        {
            if (dirtyFlags == TerrainChangeFlags.None)
                return;
            min = dirtyMin;
            max = dirtyMax;
            flags = dirtyFlags;
            dirtyFlags = TerrainChangeFlags.None;
        }

        var resolution = Data.Resolution;
        if ((flags & TerrainChangeFlags.Heights) != 0)
        {
            var mipMin = min;
            var mipMax = max;
            for (int mip = 0; mip < mipSizes.Length; mip++)
            {
                if (mip > 0)
                {
                    mipMin = new Int2(mipMin.X / 2, mipMin.Y / 2);
                    mipMax = new Int2(Math.Min(mipSizes[mip] - 1, mipMax.X / 2), Math.Min(mipSizes[mip] - 1, mipMax.Y / 2));
                    Downsample(mip, mipMin, mipMax);
                }
                UploadRegion(commandList, Heightmap, heightMips[mip], mipSizes[mip], mip, mipMin, mipMax);
            }
        }

        if ((flags & TerrainChangeFlags.Splat) != 0)
        {
            UploadRegion(commandList, SplatMap, Data.Splat, resolution, 0, min, max);
        }
    }

    private static unsafe void UploadRegion<T>(CommandList commandList, Texture texture, T[] source, int width, int mip, Int2 min, Int2 max) where T : unmanaged
    {
        var regionWidth = max.X - min.X + 1;
        var regionHeight = max.Y - min.Y + 1;
        if (regionWidth <= 0 || regionHeight <= 0)
            return;

        if (regionWidth == width && regionHeight == width)
        {
            texture.SetData(commandList, (ReadOnlySpan<T>)source, 0, mip);
            return;
        }

        var region = new T[regionWidth * regionHeight];
        for (int z = 0; z < regionHeight; z++)
            Array.Copy(source, (min.Y + z) * width + min.X, region, z * regionWidth, regionWidth);
        texture.SetData(commandList, (ReadOnlySpan<T>)region, 0, mip, new ResourceRegion(min.X, min.Y, 0, max.X + 1, max.Y + 1, 1));
    }

    private void Downsample(int mip, Int2 min, Int2 max)
    {
        var src = heightMips[mip - 1];
        var srcSize = mipSizes[mip - 1];
        var dst = heightMips[mip];
        var dstSize = mipSizes[mip];
        for (int z = min.Y; z <= max.Y; z++)
        {
            var z0 = Math.Min(z * 2, srcSize - 1);
            var z1 = Math.Min(z * 2 + 1, srcSize - 1);
            for (int x = min.X; x <= max.X; x++)
            {
                var x0 = Math.Min(x * 2, srcSize - 1);
                var x1 = Math.Min(x * 2 + 1, srcSize - 1);
                var sum = src[z0 * srcSize + x0] + src[z0 * srcSize + x1] + src[z1 * srcSize + x0] + src[z1 * srcSize + x1];
                dst[z * dstSize + x] = (ushort)((sum + 2) >> 2);
            }
        }
    }
}

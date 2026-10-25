// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using BitOperations = System.Numerics.BitOperations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using Stride.Terrain.Decals;
using Stride.Terrain.Rendering;

namespace Stride.Terrain.VirtualTexturing;

/// <summary>
/// Runtime virtual texture of a terrain: pages of the blended terrain layers and the terrain decals rendered on demand
/// (from GPU feedback) into a physical page cache, addressed through a mip-mapped page table.
/// </summary>
/// <remarks>
/// Page table entries (R16_UInt): physical page x in bits 0-5, y in bits 6-11 and the mip of the resident page in
/// bits 12-15 (a missing page points to its closest resident ancestor). See TerrainVirtualTextureBase.sdsl.
/// </remarks>
public sealed class TerrainVirtualTexture : IDisposable
{
    public const int PageContentSize = 128;
    public const int PageBorder = 4;
    public const int PhysicalPageSize = PageContentSize + 2 * PageBorder;
    public const int MaxCachePagesPerSide = 64;

    // Coarse mips kept resident (a page at these levels covers a large part of the terrain)
    private const int LockedMipCount = 3;
    private const ushort InvalidEntry = 15 << 12;

    private readonly TerrainRenderObject terrain;
    private readonly ushort[][] table;
    private readonly Rectangle[] dirtyRects;
    private readonly bool[] dirtyMips;
    private readonly Slot[] slots;
    private readonly Dictionary<long, int> residentPages = new();
    private readonly Stack<int> freeSlots = new();
    private readonly HashSet<long> refreshPages = new();
    private readonly List<PageRequest> pendingPages = new();
    private readonly Dictionary<long, int> wantedPages = new();
    private readonly ImageEffectShader composeEffect;
    private readonly TerrainDecalRegistry decalRegistry;
    private readonly TerrainVirtualTextureDecals decals;
    // Feedbacks processed (a frame whose readback is not ready has none), and how many of the last ones keep a page
    // resident: pages on the edges of the visible set (mip transitions, the low resolution feedback) drop out of single
    // feedbacks, and a full cache evicting them reloaded them a frame later, rendering pages every frame
    private int feedbackCount;
    private const int VisibleFeedbacks = 8;
    private int updateCount, appliedUpdate = -1;
    // The dirty part of a page table level, copied for its upload
    private ushort[] uploadRegion = [];
    private int topPagesResident;
    private bool layersDirty = true;

    private struct Slot
    {
        public long Key;
        public int LastUsed;
        public bool Locked;
        public bool InUse;
    }

    private struct PageRequest
    {
        public long Key;
        public int Mip;
        public int Count;
    }

    /// <param name="decalRegistry">The decals drawn over the terrain layers (optional).</param>
    public TerrainVirtualTexture(GraphicsDevice device, TerrainRenderObject terrain, TerrainVirtualTextureSettings settings, TerrainDecalRegistry decalRegistry = null)
    {
        this.terrain = terrain;
        TexelsPerMeter = (int)settings.Density;
        MaxPageUpdatesPerFrame = settings.MaxPageUpdatesPerFrame;
        MipBias = settings.MipBias;
        FeedbackDownscale = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, settings.FeedbackDownscale));

        var data = terrain.Data;
        var pagesNeeded = (int)MathF.Ceiling(data.Size * TexelsPerMeter / PageContentSize);
        PagesAtMip0 = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(pagesNeeded, 1));
        // Entries store at most 4096 pages per side (12 bits in the feedback) and 15 mips
        while (PagesAtMip0 > 4096)
        {
            PagesAtMip0 >>= 1;
            TexelsPerMeter = Math.Max(1, TexelsPerMeter / 2);
        }
        MipCount = BitOperations.Log2((uint)PagesAtMip0) + 1;
        VirtualWorldSize = PagesAtMip0 * PageContentSize / (float)TexelsPerMeter;

        table = new ushort[MipCount][];
        dirtyRects = new Rectangle[MipCount];
        dirtyMips = new bool[MipCount];
        for (int mip = 0; mip < MipCount; mip++)
        {
            var size = PagesAtMip0 >> mip;
            table[mip] = new ushort[size * size];
            Array.Fill(table[mip], InvalidEntry);
            MarkDirty(mip, 0, 0, size - 1, size - 1);
        }

        CachePagesPerSide = Math.Clamp(settings.CachePagesPerSide, 8, MaxCachePagesPerSide);
        slots = new Slot[CachePagesPerSide * CachePagesPerSide];
        for (int i = slots.Length - 1; i >= 0; i--)
            freeSlots.Push(i);

        var atlasSize = CachePagesPerSide * PhysicalPageSize;
        PageTable = Texture.New2D(device, PagesAtMip0, PagesAtMip0, MipCount, PixelFormat.R16_UInt, TextureFlags.ShaderResource);
        PageTable.Name = "TerrainVTPageTable";
        AlbedoAtlas = Texture.New2D(device, atlasSize, atlasSize, PixelFormat.R8G8B8A8_UNorm_SRgb, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
        AlbedoAtlas.Name = "TerrainVTAlbedo";
        NormalAtlas = Texture.New2D(device, atlasSize, atlasSize, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
        NormalAtlas.Name = "TerrainVTNormal";

        composeEffect = new ImageEffectShader("TerrainVTComposeShader");
        this.decalRegistry = decalRegistry;
        decals = new TerrainVirtualTextureDecals(device, decalRegistry);
        if (decalRegistry != null)
            decalRegistry.Changed += OnDecalsChanged;

        // The coarsest levels are always resident so every virtual page has a fallback
        for (int mip = Math.Max(0, MipCount - LockedMipCount); mip < MipCount; mip++)
        {
            var size = PagesAtMip0 >> mip;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                    pendingPages.Add(new PageRequest { Key = MakeKey(x, y, mip), Mip = mip, Count = int.MaxValue });
            }
        }
    }

    public TerrainRenderObject Terrain => terrain;

    public int TexelsPerMeter { get; }

    public int PagesAtMip0 { get; }

    public int MipCount { get; }

    public float VirtualWorldSize { get; }

    public int CachePagesPerSide { get; }

    public int MaxPageUpdatesPerFrame { get; set; }

    public float MipBias { get; set; }

    public int FeedbackDownscale { get; }

    public Texture PageTable { get; }

    public Texture AlbedoAtlas { get; }

    public Texture NormalAtlas { get; }

    /// <summary>
    /// Whether the whole terrain can be sampled (the coarsest page is resident).
    /// </summary>
    public bool IsReady => topPagesResident > 0;

    /// <summary>
    /// Number of pages currently resident.
    /// </summary>
    public int ResidentPageCount => residentPages.Count;

    /// <summary>
    /// Number of pages waiting to be rendered.
    /// </summary>
    public int PendingPageCount => pendingPages.Count + refreshPages.Count;

    public void Dispose()
    {
        if (decalRegistry != null)
            decalRegistry.Changed -= OnDecalsChanged;
        decals.Dispose();
        composeEffect.Dispose();
        PageTable.Dispose();
        AlbedoAtlas.Dispose();
        NormalAtlas.Dispose();
    }

    /// <summary>
    /// Re-renders the resident pages covering a region of the terrain (local XZ, meters), e.g. after editing.
    /// </summary>
    public void Invalidate(Vector2 localMin, Vector2 localMax)
    {
        for (int mip = 0; mip < MipCount; mip++)
        {
            var pageWorldSize = VirtualWorldSize / (PagesAtMip0 >> mip);
            // Include the border texels of neighbor pages
            var margin = pageWorldSize * PageBorder / PageContentSize;
            var x0 = Math.Max(0, (int)MathF.Floor((localMin.X - margin) / pageWorldSize));
            var y0 = Math.Max(0, (int)MathF.Floor((localMin.Y - margin) / pageWorldSize));
            var max = (PagesAtMip0 >> mip) - 1;
            var x1 = Math.Min(max, (int)MathF.Floor((localMax.X + margin) / pageWorldSize));
            var y1 = Math.Min(max, (int)MathF.Floor((localMax.Y + margin) / pageWorldSize));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var key = MakeKey(x, y, mip);
                    if (residentPages.ContainsKey(key))
                        refreshPages.Add(key);
                }
            }
        }
    }

    private void OnDecalsChanged(Vector2 worldMin, Vector2 worldMax)
    {
        var position = terrain.World.TranslationVector;
        var offset = new Vector2(position.X, position.Z);
        Invalidate(worldMin - offset, worldMax - offset);
    }

    /// <summary>
    /// Re-renders all the resident pages (e.g. the material layers changed).
    /// </summary>
    public void InvalidateAll()
    {
        layersDirty = true;
        foreach (var key in residentPages.Keys)
            refreshPages.Add(key);
    }

    /// <summary>
    /// Processes a feedback buffer (packed RGBA8 page requests, see TerrainVTEncodeFeedback).
    /// </summary>
    public void ProcessFeedback(ReadOnlySpan<uint> feedback)
    {
        feedbackCount++;
        wantedPages.Clear();
        uint previous = 0;
        foreach (var value in feedback)
        {
            // Neighbor pixels usually request the same page
            if (value == previous || (value >> 24) == 0)
                continue;
            previous = value;

            var x = (int)(value & 255) | (int)((value >> 8) & 15) << 8;
            var y = (int)((value >> 12) & 15) | (int)((value >> 16) & 255) << 4;
            var mip = (int)(value >> 24) - 1;
            if (mip >= MipCount)
                continue;
            // Texture coordinates on the far edge (or a stale readback after a resize) land outside the table
            var pagesAtMip = PagesAtMip0 >> mip;
            if (x >= pagesAtMip || y >= pagesAtMip)
                continue;

            // Request the page and its missing ancestors (coarser fallbacks), keep resident ones alive
            for (int m = mip; m < MipCount; m++)
            {
                var key = MakeKey(x >> (m - mip), y >> (m - mip), m);
                if (residentPages.TryGetValue(key, out var slot))
                {
                    slots[slot].LastUsed = feedbackCount;
                }
                else
                {
                    wantedPages.TryGetValue(key, out var count);
                    wantedPages[key] = count + 1;
                }
            }
        }

        // Keep the locked pages (not yet loaded at startup) and replace the rest of the queue
        pendingPages.RemoveAll(x => x.Count != int.MaxValue);
        foreach (var (key, count) in wantedPages)
            pendingPages.Add(new PageRequest { Key = key, Mip = GetMip(key), Count = count });

        // Coarser pages first (they're fallbacks for more of the screen), then the most requested
        pendingPages.Sort((a, b) => a.Mip != b.Mip ? b.Mip.CompareTo(a.Mip) : b.Count.CompareTo(a.Count));
    }

    /// <summary>
    /// Renders requested pages and uploads the page table changes.
    /// </summary>
    public void Update(RenderDrawContext context)
    {
        updateCount++;
        var budget = MaxPageUpdatesPerFrame;
        var rendered = false;

        // Refresh modified resident pages first
        if (refreshPages.Count > 0)
        {
            var keys = new List<long>(refreshPages);
            keys.Sort((a, b) => GetMip(b).CompareTo(GetMip(a)));
            foreach (var key in keys)
            {
                if (budget <= 0)
                    break;
                refreshPages.Remove(key);
                if (!residentPages.TryGetValue(key, out var slot))
                    continue;
                RenderPage(context, key, slot);
                rendered = true;
                budget--;
            }
        }

        // Then load missing pages
        var index = 0;
        while (budget > 0 && index < pendingPages.Count)
        {
            var request = pendingPages[index];
            if (residentPages.ContainsKey(request.Key))
            {
                pendingPages.RemoveAt(index);
                continue;
            }

            var locked = request.Count == int.MaxValue;
            var slot = AllocateSlot(locked);
            if (slot < 0)
                break;

            pendingPages.RemoveAt(index);
            RenderPage(context, request.Key, slot);
            MakeResident(request.Key, slot, locked);
            rendered = true;
            budget--;
        }

        if (rendered)
        {
            context.CommandList.ResourceBarrierTransition(AlbedoAtlas, BarrierLayout.ShaderResource);
            context.CommandList.ResourceBarrierTransition(NormalAtlas, BarrierLayout.ShaderResource);
        }

        UploadPageTable(context.CommandList);
    }

    private void RenderPage(RenderDrawContext context, long key, int slot)
    {
        // Applied every frame pages are rendered: the parameter layout is rebuilt once the effect is compiled
        if (layersDirty || appliedUpdate != updateCount)
        {
            TerrainMaterialParameters.Apply(composeEffect.Parameters, terrain);
            layersDirty = false;
            appliedUpdate = updateCount;
        }

        var mip = GetMip(key);
        var x = GetX(key);
        var y = GetY(key);
        var texelSize = (1 << mip) / (float)TexelsPerMeter;
        var origin = new Vector2((x * PageContentSize - PageBorder + 0.5f) * texelSize, (y * PageContentSize - PageBorder + 0.5f) * texelSize);
        composeEffect.Parameters.Set(TerrainVTComposeShaderKeys.TerrainVTPage, new Vector4(origin, texelSize, PhysicalPageSize));

        var slotX = slot % CachePagesPerSide;
        var slotY = slot / CachePagesPerSide;
        using (context.PushRenderTargetsAndRestore())
        {
            composeEffect.SetOutput(AlbedoAtlas, NormalAtlas);
            composeEffect.SetViewport(new Viewport(slotX * PhysicalPageSize, slotY * PhysicalPageSize, PhysicalPageSize, PhysicalPageSize));
            composeEffect.Draw(context);
        }

        var position = terrain.World.TranslationVector;
        var pageMin = new Vector2(position.X, position.Z) + origin - new Vector2(0.5f * texelSize);
        var slotRect = new Rectangle(slotX * PhysicalPageSize, slotY * PhysicalPageSize, PhysicalPageSize, PhysicalPageSize);
        decals.Draw(context, pageMin, PhysicalPageSize * texelSize, texelSize, slotRect, AlbedoAtlas, NormalAtlas);
    }

    private int AllocateSlot(bool locked)
    {
        if (freeSlots.Count > 0)
            return freeSlots.Pop();

        // Evict the least recently used page not requested by the last feedbacks
        var best = -1;
        var bestFrame = int.MaxValue;
        for (int i = 0; i < slots.Length; i++)
        {
            ref var slot = ref slots[i];
            if (!slot.InUse || slot.Locked || slot.LastUsed > feedbackCount - VisibleFeedbacks)
                continue;
            if (slot.LastUsed < bestFrame)
            {
                best = i;
                bestFrame = slot.LastUsed;
            }
        }
        if (best >= 0)
            Evict(best);
        return best;
    }

    private void MakeResident(long key, int slot, bool locked)
    {
        slots[slot] = new Slot { Key = key, LastUsed = feedbackCount, Locked = locked, InUse = true };
        residentPages[key] = slot;

        var mip = GetMip(key);
        var x = GetX(key);
        var y = GetY(key);
        var entry = (ushort)((slot % CachePagesPerSide) | ((slot / CachePagesPerSide) << 6) | (mip << 12));

        // The page becomes the best resident page for its area at its own and every finer level, unless finer pages are resident
        for (int m = mip; m >= 0; m--)
        {
            var scale = 1 << (mip - m);
            var size = PagesAtMip0 >> m;
            var level = table[m];
            for (int py = y * scale; py < (y + 1) * scale; py++)
            {
                for (int px = x * scale; px < (x + 1) * scale; px++)
                {
                    ref var current = ref level[py * size + px];
                    if ((current >> 12) >= mip)
                        current = entry;
                }
            }
            MarkDirty(m, x * scale, y * scale, (x + 1) * scale - 1, (y + 1) * scale - 1);
        }

        if (mip == MipCount - 1)
            topPagesResident++;
    }

    private void Evict(int slotIndex)
    {
        ref var slot = ref slots[slotIndex];
        var key = slot.Key;
        residentPages.Remove(key);
        refreshPages.Remove(key);
        slot.InUse = false;

        var mip = GetMip(key);
        var x = GetX(key);
        var y = GetY(key);
        var evicted = (ushort)((slotIndex % CachePagesPerSide) | ((slotIndex / CachePagesPerSide) << 6) | (mip << 12));
        var fallback = mip + 1 < MipCount ? table[mip + 1][(y >> 1) * (PagesAtMip0 >> (mip + 1)) + (x >> 1)] : InvalidEntry;

        for (int m = mip; m >= 0; m--)
        {
            var scale = 1 << (mip - m);
            var size = PagesAtMip0 >> m;
            var level = table[m];
            for (int py = y * scale; py < (y + 1) * scale; py++)
            {
                for (int px = x * scale; px < (x + 1) * scale; px++)
                {
                    ref var current = ref level[py * size + px];
                    if (current == evicted)
                        current = fallback;
                }
            }
            MarkDirty(m, x * scale, y * scale, (x + 1) * scale - 1, (y + 1) * scale - 1);
        }
    }

    private void MarkDirty(int mip, int x0, int y0, int x1, int y1)
    {
        if (!dirtyMips[mip])
        {
            dirtyRects[mip] = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            dirtyMips[mip] = true;
            return;
        }
        var r = dirtyRects[mip];
        var minX = Math.Min(r.X, x0);
        var minY = Math.Min(r.Y, y0);
        var maxX = Math.Max(r.Right - 1, x1);
        var maxY = Math.Max(r.Bottom - 1, y1);
        dirtyRects[mip] = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private void UploadPageTable(CommandList commandList)
    {
        var uploaded = false;
        for (int mip = 0; mip < MipCount; mip++)
        {
            if (!dirtyMips[mip])
                continue;
            dirtyMips[mip] = false;
            uploaded = true;

            var rect = dirtyRects[mip];
            var size = PagesAtMip0 >> mip;
            var count = rect.Width * rect.Height;
            if (uploadRegion.Length < count)
                uploadRegion = new ushort[PagesAtMip0 * PagesAtMip0];
            for (int y = 0; y < rect.Height; y++)
                Array.Copy(table[mip], (rect.Y + y) * size + rect.X, uploadRegion, y * rect.Width, rect.Width);
            PageTable.SetData(commandList, new ReadOnlySpan<ushort>(uploadRegion, 0, count), 0, mip, new ResourceRegion(rect.X, rect.Y, 0, rect.Right, rect.Bottom, 1));
        }
        if (uploaded)
            commandList.ResourceBarrierTransition(PageTable, BarrierLayout.ShaderResource);
    }

    private static long MakeKey(int x, int y, int mip) => ((long)mip << 40) | ((long)y << 20) | (uint)x;

    private static int GetMip(long key) => (int)(key >> 40);

    private static int GetX(long key) => (int)(key & 0xFFFFF);

    private static int GetY(long key) => (int)((key >> 20) & 0xFFFFF);
}

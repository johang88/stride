// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Rendering;

namespace Stride.Terrain.Rendering;

/// <summary>
/// The render object of a terrain: one instanced draw of the grid patch per view, with view-dependent node selection.
/// </summary>
public sealed class TerrainRenderObject : RenderMesh
{
    internal readonly Dictionary<RenderView, NodeRange> ViewNodes = new();

    internal TerrainComponent Component;

    internal TerrainData Data;

    internal TerrainMaterial TerrainMaterial;

    internal TerrainQuadTree QuadTree;

    internal TerrainGpuResources Gpu;

    internal Material Material;

    internal bool UsesVirtualTexture;

    internal VirtualTexturing.TerrainVirtualTexture VirtualTexture;

    internal (bool Enabled, int Density, int Cache, int Feedback) VirtualTextureConfig;

    internal void OnDataChanged(object sender, TerrainDataChangedEventArgs e)
    {
        if ((e.Flags & TerrainChangeFlags.Heights) != 0)
            QuadTree?.UpdateBounds(e.Min, e.Max);
        if (Data != null)
        {
            // Heights change the normals of the neighbor samples too
            var cell = Data.CellSize;
            VirtualTexture?.Invalidate(new Core.Mathematics.Vector2((e.Min.X - 1) * cell, (e.Min.Y - 1) * cell), new Core.Mathematics.Vector2((e.Max.X + 1) * cell, (e.Max.Y + 1) * cell));
        }
    }

    public override int GetViewInstanceCount(RenderView renderView)
    {
        return ViewNodes.TryGetValue(renderView, out var range) && range.Count > 0 ? range.Count : -1;
    }

    internal struct NodeRange
    {
        public int Offset;
        public int Count;
    }
}

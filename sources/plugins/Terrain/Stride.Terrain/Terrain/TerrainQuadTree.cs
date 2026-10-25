// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Numerics;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Vector3 = Stride.Core.Mathematics.Vector3;
using Vector4 = Stride.Core.Mathematics.Vector4;

namespace Stride.Terrain;

/// <summary>
/// Continuous distance-dependent LOD (CDLOD) quadtree over a <see cref="TerrainData"/>.
/// </summary>
/// <remarks>
/// Every selected node is drawn with the same grid patch of <see cref="PatchQuads"/> quads. Level 0 nodes cover
/// <see cref="PatchQuads"/> terrain cells; each level up doubles the node size. Vertices morph to the next coarser
/// grid as they approach the end of their level's distance range, so neighboring levels always match.
/// </remarks>
public sealed class TerrainQuadTree
{
    public const int MaxLevels = 16;

    // Fraction of a level's range where vertices start morphing to the next level.
    private const float MorphStartRatio = 0.7f;

    private readonly float[][] minHeights;
    private readonly float[][] maxHeights;
    private readonly int[] nodesPerSide;
    private readonly float[] ranges = new float[MaxLevels];

    public TerrainQuadTree(TerrainData data, int patchQuads)
    {
        Data = data;
        PatchQuads = patchQuads;

        var quads = data.Resolution - 1;
        if (quads % patchQuads != 0 || !BitOperations.IsPow2(quads / patchQuads))
            throw new ArgumentException($"Terrain resolution - 1 ({quads}) must be the patch size ({patchQuads}) times a power of two.");

        var leavesPerSide = quads / patchQuads;
        LevelCount = BitOperations.Log2((uint)leavesPerSide) + 1;
        if (LevelCount > MaxLevels)
            throw new ArgumentException("Too many terrain LOD levels.");

        minHeights = new float[LevelCount][];
        maxHeights = new float[LevelCount][];
        nodesPerSide = new int[LevelCount];
        for (int level = 0; level < LevelCount; level++)
        {
            var n = leavesPerSide >> level;
            nodesPerSide[level] = n;
            minHeights[level] = new float[n * n];
            maxHeights[level] = new float[n * n];
        }

        UpdateBounds(Int2.Zero, new Int2(data.Resolution - 1));
        SetLodDistance(64.0f);
    }

    public TerrainData Data { get; }

    /// <summary>
    /// Number of quads along each side of the grid patch drawn for each node.
    /// </summary>
    public int PatchQuads { get; }

    public int LevelCount { get; }

    /// <summary>
    /// Distance from the viewer up to which the most detailed level is used. Each level doubles that range.
    /// </summary>
    public float LodDistance { get; private set; }

    /// <summary>
    /// The distance last given to <see cref="SetLodDistance"/> (<see cref="LodDistance"/> may be raised from it).
    /// </summary>
    public float RequestedLodDistance { get; private set; }

    public void SetLodDistance(float lodDistance)
    {
        // Nodes at the edge of a level must not reach the morph area of the next level (which would open cracks)
        var minDistance = PatchQuads * Data.CellSize * 4.5f;
        RequestedLodDistance = lodDistance;
        LodDistance = Math.Max(lodDistance, minDistance);
        for (int level = 0; level < LevelCount; level++)
            ranges[level] = LodDistance * (1 << level);
        ranges[LevelCount - 1] = float.MaxValue;
    }

    /// <summary>
    /// Fills the morph parameters of each level: (morph start, 1 / (morph end - morph start), node size, unused).
    /// </summary>
    public void GetMorphConstants(Span<Vector4> constants)
    {
        for (int level = 0; level < MaxLevels; level++)
        {
            var size = Data.CellSize * (PatchQuads << Math.Min(level, LevelCount - 1));
            if (level >= LevelCount - 1)
            {
                // Top level never morphs
                constants[level] = new Vector4(float.MaxValue, 0, size, 0);
                continue;
            }

            var end = ranges[level];
            var start = end * MorphStartRatio;
            constants[level] = new Vector4(start, 1.0f / (end - start), size, 0);
        }
    }

    /// <summary>
    /// Recomputes node height bounds after samples in [min, max] changed.
    /// </summary>
    public void UpdateBounds(Int2 min, Int2 max)
    {
        var leafMin = new Int2(Math.Max(0, (min.X - 1) / PatchQuads), Math.Max(0, (min.Y - 1) / PatchQuads));
        var leafMax = new Int2(Math.Min(nodesPerSide[0] - 1, max.X / PatchQuads), Math.Min(nodesPerSide[0] - 1, max.Y / PatchQuads));

        var n0 = nodesPerSide[0];
        for (int z = leafMin.Y; z <= leafMax.Y; z++)
        {
            for (int x = leafMin.X; x <= leafMax.X; x++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                var x0 = x * PatchQuads;
                var z0 = z * PatchQuads;
                for (int sz = z0; sz <= z0 + PatchQuads; sz++)
                {
                    for (int sx = x0; sx <= x0 + PatchQuads; sx++)
                    {
                        var h = Data.GetHeight(sx, sz);
                        lo = Math.Min(lo, h);
                        hi = Math.Max(hi, h);
                    }
                }
                minHeights[0][z * n0 + x] = lo;
                maxHeights[0][z * n0 + x] = hi;
            }
        }

        for (int level = 1; level < LevelCount; level++)
        {
            leafMin = new Int2(leafMin.X / 2, leafMin.Y / 2);
            leafMax = new Int2(leafMax.X / 2, leafMax.Y / 2);
            var n = nodesPerSide[level];
            var nChild = nodesPerSide[level - 1];
            var childMin = minHeights[level - 1];
            var childMax = maxHeights[level - 1];
            for (int z = leafMin.Y; z <= leafMax.Y; z++)
            {
                for (int x = leafMin.X; x <= leafMax.X; x++)
                {
                    var c = (z * 2) * nChild + x * 2;
                    minHeights[level][z * n + x] = Math.Min(Math.Min(childMin[c], childMin[c + 1]), Math.Min(childMin[c + nChild], childMin[c + nChild + 1]));
                    maxHeights[level][z * n + x] = Math.Max(Math.Max(childMax[c], childMax[c + 1]), Math.Max(childMax[c + nChild], childMax[c + nChild + 1]));
                }
            }
        }
    }

    /// <summary>
    /// Gets the local bounding box of the whole terrain.
    /// </summary>
    public BoundingBox GetBounds()
    {
        var top = LevelCount - 1;
        return new BoundingBox(new Vector3(0, minHeights[top][0], 0), new Vector3(Data.Size, maxHeights[top][0], Data.Size));
    }

    /// <summary>
    /// Selects the nodes to draw for a view.
    /// </summary>
    /// <param name="terrainOffset">World position of the terrain origin.</param>
    /// <param name="lodOrigin">World position the LOD distances are measured from (usually the main camera).</param>
    /// <param name="frustum">World space frustum to cull nodes against.</param>
    /// <param name="ignoreDepthPlanes">Whether to ignore the near and far planes of the frustum (shadow casters).</param>
    /// <param name="output">Receives one (x, z, size, level) entry per node, in terrain local space.</param>
    public void Select(Vector3 terrainOffset, Vector3 lodOrigin, ref BoundingFrustum frustum, bool ignoreDepthPlanes, List<Vector4> output)
    {
        var context = new SelectionContext
        {
            Offset = terrainOffset,
            LodOrigin = lodOrigin,
            Frustum = frustum,
            IgnoreDepthPlanes = ignoreDepthPlanes,
            Output = output,
        };
        SelectNode(ref context, LevelCount - 1, 0, 0, false);
    }

    private struct SelectionContext
    {
        public Vector3 Offset;
        public Vector3 LodOrigin;
        public BoundingFrustum Frustum;
        public bool IgnoreDepthPlanes;
        public List<Vector4> Output;
    }

    private enum SelectResult
    {
        OutOfRange,
        Culled,
        Selected,
    }

    private SelectResult SelectNode(ref SelectionContext context, int level, int x, int z, bool parentFullyVisible)
    {
        var n = nodesPerSide[level];
        var nodeSize = Data.CellSize * (PatchQuads << level);
        var min = new Vector3(x * nodeSize, minHeights[level][z * n + x], z * nodeSize) + context.Offset;
        var max = new Vector3(min.X + nodeSize, maxHeights[level][z * n + x] + context.Offset.Y, min.Z + nodeSize);
        var box = new BoundingBox(min, max);

        if (!BoxIntersectsSphere(ref box, context.LodOrigin, ranges[level]))
            return SelectResult.OutOfRange;

        if (!parentFullyVisible)
        {
            var boxExt = new BoundingBoxExt(box);
            if (!VisibilityGroup.FrustumContainsBox(ref context.Frustum, ref boxExt, context.IgnoreDepthPlanes))
                return SelectResult.Culled;
        }

        if (level == 0 || !BoxIntersectsSphere(ref box, context.LodOrigin, ranges[level - 1]))
        {
            // Whole node at this level
            context.Output.Add(new Vector4(x * nodeSize, z * nodeSize, nodeSize, level));
            return SelectResult.Selected;
        }

        // Children: those out of range of the finer level are covered by this level (as quarter nodes, fully morphed)
        var childSize = nodeSize * 0.5f;
        for (int i = 0; i < 4; i++)
        {
            var cx = x * 2 + (i & 1);
            var cz = z * 2 + (i >> 1);
            var result = SelectNode(ref context, level - 1, cx, cz, parentFullyVisible);
            if (result == SelectResult.OutOfRange)
            {
                context.Output.Add(new Vector4(cx * childSize, cz * childSize, childSize, level - 1));
            }
        }
        return SelectResult.Selected;
    }

    private static bool BoxIntersectsSphere(ref BoundingBox box, Vector3 center, float radius)
    {
        if (radius == float.MaxValue)
            return true;
        var clamped = Vector3.Clamp(center, box.Minimum, box.Maximum);
        return Vector3.DistanceSquared(clamped, center) <= radius * radius;
    }
}

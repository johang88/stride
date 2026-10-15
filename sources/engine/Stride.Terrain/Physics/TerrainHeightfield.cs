// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;

namespace Stride.Terrain.Physics;

/// <summary>
/// Bepu shape for a terrain height field: a grid of triangles generated on demand from 16-bit heights,
/// so a 4097x4097 terrain costs its height samples instead of tens of millions of stored triangles.
/// </summary>
/// <remarks>
/// Child i is triangle (i &amp; 1) of cell (i / 2) % quads, (i / 2) / quads; cells are split along their (0,0)-(1,1)
/// diagonal like the rendered mesh. Triangles face up (Bepu triangles are one-sided).
/// </remarks>
public struct TerrainHeightfield : IHomogeneousCompoundShape<Triangle, TriangleWide>
{
    public const int Id = 42;

    /// <summary>
    /// Height samples (row-major, z then x), normalized 16-bit.
    /// </summary>
    public Buffer<ushort> Heights;

    /// <summary>
    /// Number of samples along each side.
    /// </summary>
    public int Resolution;

    public float CellSize;

    public float MinHeight;

    /// <summary>
    /// Height per unit of the normalized samples ((max - min) / 65535).
    /// </summary>
    public float HeightScale;

    public static int TypeId => Id;

    public readonly int Quads => Resolution - 1;

    public readonly int ChildCount => Quads * Quads * 2;

    public readonly float MaxHeight => MinHeight + HeightScale * ushort.MaxValue;

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches)
    {
        return new HomogeneousCompoundShapeBatch<TerrainHeightfield, Triangle, TriangleWide>(pool, initialCapacity);
    }

    public void Dispose(BufferPool pool)
    {
        pool.Return(ref Heights);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 GetVertex(int x, int z)
    {
        return new Vector3(x * CellSize, MinHeight + Heights[z * Resolution + x] * HeightScale, z * CellSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void GetLocalChild(int childIndex, out Triangle target)
    {
        var cell = childIndex >> 1;
        var x = cell % Quads;
        var z = cell / Quads;
        var p00 = GetVertex(x, z);
        var p11 = GetVertex(x + 1, z + 1);
        target.A = p00;
        if ((childIndex & 1) == 0)
        {
            target.B = GetVertex(x + 1, z);
            target.C = p11;
        }
        else
        {
            target.B = p11;
            target.C = GetVertex(x, z + 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void GetPosedLocalChild(int childIndex, out Triangle target, out RigidPose childPose)
    {
        GetLocalChild(childIndex, out target);
        childPose = (target.A + target.B + target.C) * (1f / 3f);
        target.A -= childPose.Position;
        target.B -= childPose.Position;
        target.C -= childPose.Position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void GetLocalChild(int childIndex, ref TriangleWide target)
    {
        GetLocalChild(childIndex, out Triangle triangle);
        Vector3Wide.WriteFirst(triangle.A, ref target.A);
        Vector3Wide.WriteFirst(triangle.B, ref target.B);
        Vector3Wide.WriteFirst(triangle.C, ref target.C);
    }

    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        var size = Quads * CellSize;
        var localMin = new Vector3(0, MinHeight, 0);
        var localMax = new Vector3(size, MaxHeight, size);
        Matrix3x3.CreateFromQuaternion(orientation, out var rotation);
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) != 0 ? localMax.X : localMin.X, (i & 2) != 0 ? localMax.Y : localMin.Y, (i & 4) != 0 ? localMax.Z : localMin.Z);
            Matrix3x3.Transform(corner, rotation, out var rotated);
            min = Vector3.Min(min, rotated);
            max = Vector3.Max(max, rotated);
        }
    }

    /// <summary>
    /// Calls <paramref name="onChild"/> for each triangle whose cell overlaps a local box.
    /// </summary>
    private readonly void EnumerateOverlaps<TEnumerator>(Vector3 min, Vector3 max, ref TEnumerator enumerator) where TEnumerator : IBreakableForEach<int>
    {
        if (max.Y < MinHeight || min.Y > MaxHeight)
            return;
        var inverseCell = 1.0f / CellSize;
        var x0 = Math.Max(0, (int)MathF.Floor(min.X * inverseCell));
        var z0 = Math.Max(0, (int)MathF.Floor(min.Z * inverseCell));
        var x1 = Math.Min(Quads - 1, (int)MathF.Floor(max.X * inverseCell));
        var z1 = Math.Min(Quads - 1, (int)MathF.Floor(max.Z * inverseCell));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                // Reject cells entirely above or below the box
                var h00 = Heights[z * Resolution + x];
                var h10 = Heights[z * Resolution + x + 1];
                var h01 = Heights[(z + 1) * Resolution + x];
                var h11 = Heights[(z + 1) * Resolution + x + 1];
                var lo = MinHeight + Math.Min(Math.Min(h00, h10), Math.Min(h01, h11)) * HeightScale;
                var hi = MinHeight + Math.Max(Math.Max(h00, h10), Math.Max(h01, h11)) * HeightScale;
                if (hi < min.Y || lo > max.Y)
                    continue;
                var cell = (z * Quads + x) * 2;
                if (!enumerator.LoopBody(cell) || !enumerator.LoopBody(cell + 1))
                    return;
            }
        }
    }

    private unsafe struct OverlapCollector<TSubpairOverlaps> : IBreakableForEach<int> where TSubpairOverlaps : ICollisionTaskSubpairOverlaps
    {
        public BufferPool Pool;
        public void* Overlaps;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool LoopBody(int i)
        {
            Unsafe.AsRef<TSubpairOverlaps>(Overlaps).Allocate(Pool) = i;
            return true;
        }
    }

    public readonly unsafe void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
    {
        OverlapCollector<TSubpairOverlaps> collector;
        collector.Pool = pool;
        for (int i = 0; i < pairs.Length; ++i)
        {
            ref var pair = ref pairs[i];
            ref var heightfield = ref Unsafe.AsRef<TerrainHeightfield>(pair.Container);
            collector.Overlaps = Unsafe.AsPointer(ref overlaps.GetOverlapsForPair(i));
            heightfield.EnumerateOverlaps(pair.Min, pair.Max, ref collector);
        }
    }

    public readonly unsafe void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
    {
        // Conservative: everything the swept box can touch
        var displacement = sweep * maximumT;
        var sweptMin = Vector3.Min(min, min + displacement);
        var sweptMax = Vector3.Max(max, max + displacement);
        OverlapCollector<TOverlaps> collector;
        collector.Pool = pool;
        collector.Overlaps = overlaps;
        EnumerateOverlaps(sweptMin, sweptMax, ref collector);
    }

    public readonly void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
    {
        EnumerateOverlaps(min, max, ref enumerator);
    }

    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
        Matrix3x3.TransformTranspose(ray.Origin - pose.Position, orientation, out var localOrigin);
        Matrix3x3.TransformTranspose(ray.Direction, orientation, out var localDirection);
        RayTestLocal(localOrigin, localDirection, orientation, ray, ref maximumT, ref hitHandler);
    }

    public readonly unsafe void RayTest<TRayHitHandler>(in RigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
        for (int i = 0; i < rays.RayCount; ++i)
        {
            rays.GetRay(i, out var ray, out var maximumT);
            Matrix3x3.TransformTranspose(ray->Origin - pose.Position, orientation, out var localOrigin);
            Matrix3x3.TransformTranspose(ray->Direction, orientation, out var localDirection);
            RayTestLocal(localOrigin, localDirection, orientation, *ray, ref *maximumT, ref hitHandler);
        }
    }

    // Walks the cells crossed by the ray (2D DDA in XZ) and tests their two triangles
    private readonly void RayTestLocal<TRayHitHandler>(Vector3 origin, Vector3 direction, in Matrix3x3 orientation, in RayData originalRay, ref float maximumT, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        var size = Quads * CellSize;
        var boxMin = new Vector3(0, MinHeight, 0);
        var boxMax = new Vector3(size, MaxHeight, size);

        // Clip the ray against the terrain box
        float tMin = 0, tMax = maximumT;
        for (int axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var lo = axis == 0 ? boxMin.X : axis == 1 ? boxMin.Y : boxMin.Z;
            var hi = axis == 0 ? boxMax.X : axis == 1 ? boxMax.Y : boxMax.Z;
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < lo || o > hi)
                    return;
                continue;
            }
            var t0 = (lo - o) / d;
            var t1 = (hi - o) / d;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            tMin = MathF.Max(tMin, t0);
            tMax = MathF.Min(tMax, t1);
            if (tMin > tMax)
                return;
        }

        var start = origin + direction * tMin;
        var x = Math.Clamp((int)MathF.Floor(start.X / CellSize), 0, Quads - 1);
        var z = Math.Clamp((int)MathF.Floor(start.Z / CellSize), 0, Quads - 1);
        var stepX = direction.X >= 0 ? 1 : -1;
        var stepZ = direction.Z >= 0 ? 1 : -1;
        var tDeltaX = direction.X != 0 ? MathF.Abs(CellSize / direction.X) : float.PositiveInfinity;
        var tDeltaZ = direction.Z != 0 ? MathF.Abs(CellSize / direction.Z) : float.PositiveInfinity;
        var tNextX = direction.X != 0 ? tMin + ((x + (stepX > 0 ? 1 : 0)) * CellSize - start.X) / direction.X : float.PositiveInfinity;
        var tNextZ = direction.Z != 0 ? tMin + ((z + (stepZ > 0 ? 1 : 0)) * CellSize - start.Z) / direction.Z : float.PositiveInfinity;

        while (x >= 0 && z >= 0 && x < Quads && z < Quads)
        {
            var cell = (z * Quads + x) * 2;
            for (int t = 0; t < 2; t++)
            {
                GetLocalChild(cell + t, out Triangle triangle);
                if (Triangle.RayTest(triangle.A, triangle.B, triangle.C, origin, direction, out var hitT, out var normal) && hitT <= maximumT)
                {
                    Matrix3x3.Transform(normal, orientation, out var worldNormal);
                    hitHandler.OnRayHit(originalRay, ref maximumT, hitT, Vector3.Normalize(worldNormal), cell + t);
                }
            }

            var next = MathF.Min(tNextX, tNextZ);
            if (next > maximumT || next > tMax)
                return;
            if (tNextX < tNextZ)
            {
                x += stepX;
                tNextX += tDeltaX;
            }
            else
            {
                z += stepZ;
                tNextZ += tDeltaZ;
            }
        }
    }
}

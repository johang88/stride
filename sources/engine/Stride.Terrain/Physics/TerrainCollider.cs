// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.CollisionDetection.CollisionTasks;
using BepuPhysics.CollisionDetection.SweepTasks;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Mathematics;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.Terrain.Physics;

/// <summary>
/// Collider of the <see cref="TerrainComponent"/> on the same entity (use it on a static collidable).
/// Collisions use the full resolution height field and follow terrain edits.
/// </summary>
[DataContract("TerrainCollider")]
[Display("Terrain")]
public sealed class TerrainCollider : ICollider, INavigationGeometryProvider
{
    private static readonly ConditionalWeakTable<Simulation, object> RegisteredSimulations = new();

    private CollidableComponent component;
    private TerrainData attachedData;
    private Shapes attachedShapes;
    private TypedIndex attachedIndex;

    CollidableComponent ICollider.Component
    {
        get => component;
        set => component = value;
    }

    /// <summary>
    /// Height of the triangles generated for navigation mesh building and debug display, in samples (1 = full resolution).
    /// </summary>
    [DataMember(10)]
    [System.ComponentModel.DefaultValue(4)]
    public int DisplayDecimation { get; set; } = 4;

    public int Transforms => 1;

    public void GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        transforms[0].PositionLocal = Vector3.Zero;
        transforms[0].RotationLocal = Quaternion.Identity;
        transforms[0].Scale = Vector3.One;
    }

    // Null until the terrain samples are loaded (the editor shows a scene before its terrains are compiled)
    private TerrainData FindData() => component?.Entity?.Get<TerrainComponent>()?.Terrain is { Resolution: >= 2 } data ? data : null;

    unsafe bool ICollider.TryAttach(Shapes shapes, BufferPool pool, ShapeCacheSystem shapeCache, bool shouldCalculateInertia, out TypedIndex index, out Vector3 centerOfMass, out BodyInertia inertia)
    {
        index = default;
        centerOfMass = Vector3.Zero;
        inertia = default;

        var data = FindData();
        if (data == null || shouldCalculateInertia)
            return false; // Terrains are static only

        RegisterCollisionTasks(shapes);

        pool.Take<ushort>(data.Heights.Length, out var heights);
        data.Heights.AsSpan().CopyTo(new Span<ushort>(heights.Memory, heights.Length));
        var heightfield = new TerrainHeightfield
        {
            Heights = heights,
            Resolution = data.Resolution,
            CellSize = data.CellSize,
            MinHeight = data.MinHeight,
            HeightScale = (data.MaxHeight - data.MinHeight) / ushort.MaxValue,
        };
        index = shapes.Add(heightfield);

        attachedData = data;
        attachedShapes = shapes;
        attachedIndex = index;
        data.Changed += OnDataChanged;
        return true;
    }

    void ICollider.Detach(Shapes shapes, BufferPool pool, TypedIndex index)
    {
        if (attachedData != null)
            attachedData.Changed -= OnDataChanged;
        attachedData = null;
        attachedShapes = null;
        shapes.RemoveAndDispose(index, pool);
    }

    private unsafe void OnDataChanged(object sender, TerrainDataChangedEventArgs e)
    {
        if ((e.Flags & TerrainChangeFlags.Heights) == 0 || attachedShapes == null)
            return;

        // Edits write straight into the shape (same layout); the bounds use the fixed height range so they stay valid
        ref var heightfield = ref attachedShapes.GetShape<TerrainHeightfield>(attachedIndex.Index);
        var data = attachedData;
        if (heightfield.Resolution != data.Resolution)
        {
            component?.TryUpdateFeatures();
            return;
        }
        for (int z = e.Min.Y; z <= e.Max.Y; z++)
        {
            var row = z * data.Resolution;
            data.Heights.AsSpan(row + e.Min.X, e.Max.X - e.Min.X + 1).CopyTo(new Span<ushort>(heightfield.Heights.Memory + row + e.Min.X, e.Max.X - e.Min.X + 1));
        }
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object cache)
    {
        cache = null;
        var data = FindData();
        if (data == null)
        {
            // One entry per collider transform is expected (empty until the terrain is loaded)
            buffer.Add(new BasicMeshBuffers { Vertices = Array.Empty<VertexPosition3>(), Indices = Array.Empty<int>() });
            return;
        }

        var step = Math.Max(1, DisplayDecimation);
        var quads = (data.Resolution - 1) / step;
        var side = quads + 1;
        var vertices = new VertexPosition3[side * side];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                var sx = Math.Min(x * step, data.Resolution - 1);
                var sz = Math.Min(z * step, data.Resolution - 1);
                vertices[z * side + x] = new VertexPosition3 { Position = new Vector3(sx * data.CellSize, data.GetHeight(sx, sz), sz * data.CellSize) };
            }
        }
        var indices = new int[quads * quads * 6];
        var i = 0;
        for (int z = 0; z < quads; z++)
        {
            for (int x = 0; x < quads; x++)
            {
                var i00 = z * side + x;
                indices[i++] = i00; indices[i++] = i00 + 1; indices[i++] = i00 + side + 1;
                indices[i++] = i00; indices[i++] = i00 + side + 1; indices[i++] = i00 + side;
            }
        }
        buffer.Add(new BasicMeshBuffers { Vertices = vertices, Indices = indices });
    }

    void ICollider.RayTest<TRayHitHandler>(Shapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        shapes.GetShape<TerrainHeightfield>(shapeIndex.Index).RayTest(pose, ray, ref maximumT, pool, ref hitHandler);
    }

    public void GetNavigationTriangles(CollidableComponent collidable, BoundingBox bounds, List<Vector3> vertices, List<int> indices)
    {
        var data = FindData();
        if (data == null)
            return;
        var origin = collidable.Entity.Transform.WorldMatrix.TranslationVector;
        var x0 = Math.Clamp((int)MathF.Floor((bounds.Minimum.X - origin.X) / data.CellSize), 0, data.Resolution - 1);
        var z0 = Math.Clamp((int)MathF.Floor((bounds.Minimum.Z - origin.Z) / data.CellSize), 0, data.Resolution - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling((bounds.Maximum.X - origin.X) / data.CellSize), 0, data.Resolution - 1);
        var z1 = Math.Clamp((int)MathF.Ceiling((bounds.Maximum.Z - origin.Z) / data.CellSize), 0, data.Resolution - 1);
        if (x1 <= x0 || z1 <= z0)
            return;

        var start = vertices.Count;
        var width = x1 - x0 + 1;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
                vertices.Add(new Vector3(x * data.CellSize, data.GetHeight(x, z), z * data.CellSize) + origin);
        }
        var resolution = data.Resolution;
        for (int z = 0; z < z1 - z0; z++)
        {
            for (int x = 0; x < x1 - x0; x++)
            {
                // Holes are not walkable
                var splat = data.Splat[(z0 + z) * resolution + x0 + x];
                if (TerrainSplat.GetWeight(splat, TerrainSplat.HoleLayer) > 0.5f)
                    continue;
                var i00 = start + z * width + x;
                var i10 = i00 + 1;
                var i01 = i00 + width;
                var i11 = i01 + 1;
                indices.Add(i00); indices.Add(i11); indices.Add(i10);
                indices.Add(i00); indices.Add(i01); indices.Add(i11);
            }
        }
    }

    public BoundingBox GetNavigationBounds(CollidableComponent collidable)
    {
        var data = FindData();
        if (data == null)
            return BoundingBox.Empty;
        var origin = collidable.Entity.Transform.WorldMatrix.TranslationVector;
        return new BoundingBox(origin + new Vector3(0, data.MinHeight, 0), origin + new Vector3(data.Size, data.MaxHeight, data.Size));
    }

    private void RegisterCollisionTasks(Shapes shapes)
    {
        var configuration = component?.Processor?.BepuConfiguration;
        if (configuration == null)
            return;
        foreach (var bepuSimulation in configuration.BepuSimulations)
        {
            var simulation = bepuSimulation.Simulation;
            if (simulation.Shapes != shapes)
                continue;
            lock (RegisteredSimulations)
            {
                if (RegisteredSimulations.TryGetValue(simulation, out _))
                    return;
                RegisterCollisionTasks(simulation.NarrowPhase.CollisionTaskRegistry, simulation.NarrowPhase.SweepTaskRegistry);
                RegisteredSimulations.Add(simulation, null);
            }
        }
    }

    /// <summary>
    /// Registers the collision and sweep tasks between the height field and the other shape types.
    /// </summary>
    public static void RegisterCollisionTasks(CollisionTaskRegistry collisions, SweepTaskRegistry sweeps)
    {
        collisions.Register(new ConvexCompoundCollisionTask<Sphere, TerrainHeightfield, ConvexCompoundOverlapFinder<Sphere, SphereWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Capsule, TerrainHeightfield, ConvexCompoundOverlapFinder<Capsule, CapsuleWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Box, TerrainHeightfield, ConvexCompoundOverlapFinder<Box, BoxWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Triangle, TerrainHeightfield, ConvexCompoundOverlapFinder<Triangle, TriangleWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<Cylinder, TerrainHeightfield, ConvexCompoundOverlapFinder<Cylinder, CylinderWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new ConvexCompoundCollisionTask<ConvexHull, TerrainHeightfield, ConvexCompoundOverlapFinder<ConvexHull, ConvexHullWide, TerrainHeightfield>, ConvexMeshContinuations<TerrainHeightfield>, MeshReduction>());
        collisions.Register(new CompoundPairCollisionTask<Compound, TerrainHeightfield, CompoundPairOverlapFinder<Compound, TerrainHeightfield>, CompoundMeshContinuations<Compound, TerrainHeightfield>, CompoundMeshReduction>());
        collisions.Register(new CompoundPairCollisionTask<BigCompound, TerrainHeightfield, CompoundPairOverlapFinder<BigCompound, TerrainHeightfield>, CompoundMeshContinuations<BigCompound, TerrainHeightfield>, CompoundMeshReduction>());
        collisions.Register(new CompoundPairCollisionTask<Mesh, TerrainHeightfield, MeshPairOverlapFinder<Mesh, TerrainHeightfield>, MeshPairContinuations<Mesh, TerrainHeightfield>, CompoundMeshReduction>());

        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Sphere, SphereWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<Sphere, TerrainHeightfield>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Capsule, CapsuleWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<Capsule, TerrainHeightfield>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Cylinder, CylinderWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<Cylinder, TerrainHeightfield>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Box, BoxWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<Box, TerrainHeightfield>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<Triangle, TriangleWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<Triangle, TerrainHeightfield>>());
        sweeps.Register(new ConvexHomogeneousCompoundSweepTask<ConvexHull, ConvexHullWide, TerrainHeightfield, Triangle, TriangleWide, ConvexCompoundSweepOverlapFinder<ConvexHull, TerrainHeightfield>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<Compound, TerrainHeightfield, Triangle, TriangleWide, CompoundPairSweepOverlapFinder<Compound, TerrainHeightfield>>());
        sweeps.Register(new CompoundHomogeneousCompoundSweepTask<BigCompound, TerrainHeightfield, Triangle, TriangleWide, CompoundPairSweepOverlapFinder<BigCompound, TerrainHeightfield>>());
    }
}

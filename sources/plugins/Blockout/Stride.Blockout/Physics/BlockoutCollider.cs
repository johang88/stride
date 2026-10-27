// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using BepuUtilities.Memory;
using Stride.BepuPhysics;
using Stride.BepuPhysics.Definitions;
using Stride.BepuPhysics.Definitions.Colliders;
using Stride.BepuPhysics.Systems;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using BufferPool = BepuUtilities.Memory.BufferPool;
using Mesh = BepuPhysics.Collidables.Mesh;
using BepuShapes = BepuPhysics.Collidables.Shapes;
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.Blockout.Physics;

/// <summary>
/// Collider of the <see cref="BlockoutComponent"/> on the same entity: its triangles on a static collidable, their convex
/// hull on a body. Follows the changes of the shape.
/// </summary>
[DataContract("BlockoutCollider")]
[Display("Blockout shape")]
public sealed class BlockoutCollider : ICollider
{
    private CollidableComponent component;
    private float mass = 1.0f;

    CollidableComponent ICollider.Component
    {
        get => component;
        set => component = value;
    }

    /// <summary>
    /// The mass on a body.
    /// </summary>
    [DataMember(10)]
    public float Mass
    {
        get => mass;
        set
        {
            if (mass == value)
                return;
            mass = Math.Max(value, 1e-4f);
            component?.TryUpdateFeatures();
        }
    }

    /// <summary>
    /// The <see cref="BlockoutComponent.GeometryHash"/> of the shape attached.
    /// </summary>
    internal int AttachedHash { get; private set; }

    public int Transforms => 1;

    private BlockoutComponent FindShape() => component?.Entity?.Get<BlockoutComponent>();

    public void GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        transforms[0].PositionLocal = Vector3.Zero;
        transforms[0].RotationLocal = Quaternion.Identity;
        transforms[0].Scale = MeshCollider.ComputeMeshScale(collidable);
    }

    bool ICollider.TryAttach(BepuShapes shapes, BufferPool pool, ShapeCacheSystem shapeCache, bool shouldCalculateInertia, out TypedIndex index, out Vector3 centerOfMass, out BodyInertia inertia)
    {
        index = default;
        centerOfMass = Vector3.Zero;
        inertia = default;

        var shape = FindShape();
        if (shape == null || !shape.Enabled)
            return false;
        AttachedHash = shape.GeometryHash;
        var geometry = shape.GetCollisionGeometry();
        if (geometry.IsEmpty)
            return false;
        var scale = MeshCollider.ComputeMeshScale(component);

        if (!shouldCalculateInertia)
        {
            pool.Take<Triangle>(geometry.TriangleCount, out var triangles);
            for (int i = 0; i < geometry.TriangleCount; i++)
            {
                triangles[i] = new Triangle(
                    geometry.Vertices[geometry.Indices[i * 3]].Position.ToNumeric(),
                    geometry.Vertices[geometry.Indices[i * 3 + 1]].Position.ToNumeric(),
                    geometry.Vertices[geometry.Indices[i * 3 + 2]].Position.ToNumeric());
            }
            index = shapes.Add(new Mesh(triangles, scale.ToNumeric(), pool));
            return true;
        }

        // A body: the convex hull of the shape
        var points = new System.Numerics.Vector3[geometry.Vertices.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = (geometry.Vertices[i].Position * scale).ToNumeric();
        ConvexHullHelper.CreateShape(points, pool, out var center, out ConvexHull hull);
        index = shapes.Add(hull);
        centerOfMass = center.ToStride();
        inertia = hull.ComputeInertia(Mass);
        return true;
    }

    void ICollider.Detach(BepuShapes shapes, BufferPool pool, TypedIndex index)
    {
        shapes.RemoveAndDispose(index, pool);
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object cache)
    {
        cache = null;
        var geometry = FindShape()?.GetCollisionGeometry() ?? Blockout.Shapes.BlockoutGeometry.Empty;
        var vertices = new VertexPosition3[geometry.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new VertexPosition3 { Position = geometry.Vertices[i].Position };
        buffer.Add(new BasicMeshBuffers { Vertices = vertices, Indices = (int[])geometry.Indices.Clone() });
    }

    void ICollider.RayTest<TRayHitHandler>(BepuShapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        if (shapeIndex.Type == Mesh.TypeId)
        {
            shapes.GetShape<Mesh>(shapeIndex.Index).RayTest(pose, in ray, ref maximumT, pool, ref hitHandler);
            return;
        }
        ref var hull = ref shapes.GetShape<ConvexHull>(shapeIndex.Index);
        if (hitHandler.AllowTest(0) && hull.RayTest(pose, ray.Origin, ray.Direction, out var t, out var normal) && t <= maximumT)
            hitHandler.OnRayHit(ray, ref maximumT, t, normal, 0);
    }
}

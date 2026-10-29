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
using NRigidPose = BepuPhysics.RigidPose;

namespace Stride.Terrain.Physics;

/// <summary>
/// The trunks of the placed trees of the <see cref="TerrainComponent"/> on the same entity or a parent (use it on a static
/// collidable): one cylinder per instance of the vegetation types with a <see cref="Vegetation.TerrainVegetationType.TrunkRadius"/>.
/// Navigation meshes go around them. Follows the edits of the vegetation and the heights.
/// </summary>
[DataContract("TerrainVegetationCollider")]
[Display("Terrain trees")]
public sealed class TerrainVegetationCollider : ICollider, INavigationGeometryProvider
{
    private const int PrismSides = 6;

    private CollidableComponent component;
    private TerrainData attachedData;
    private readonly List<TypedIndex> attachedCylinders = new();

    CollidableComponent ICollider.Component
    {
        get => component;
        set => component = value;
    }

    public int Transforms => 1;

    public void GetLocalTransforms(CollidableComponent collidable, Span<ShapeTransform> transforms)
    {
        transforms[0].PositionLocal = Vector3.Zero;
        transforms[0].RotationLocal = Quaternion.Identity;
        transforms[0].Scale = Vector3.One;
    }

    private TerrainComponent FindTerrain()
    {
        for (var entity = component?.Entity; entity != null; entity = entity.Transform.Parent?.Entity)
        {
            if (entity.Get<TerrainComponent>() is { } terrain)
                return terrain;
        }
        return null;
    }

    /// <summary>
    /// The trunks in the local space of the terrain: base center, radius, height.
    /// </summary>
    private static IEnumerable<(Vector3 Base, float Radius, float Height, int Type)> Trunks(TerrainComponent terrain)
    {
        var data = terrain?.Terrain;
        if (data == null || data.Resolution < 2)
            yield break;
        var types = terrain.Vegetation;
        foreach (var instance in data.Vegetation)
        {
            if ((uint)instance.Type >= (uint)types.Count || types[instance.Type] is not { TrunkRadius: > 0 } type)
                continue;
            var scale = instance.Scale > 0 ? instance.Scale : 1.0f;
            var ground = data.SampleHeight(instance.Position.X, instance.Position.Y);
            yield return (new Vector3(instance.Position.X, ground + instance.HeightOffset, instance.Position.Y), type.TrunkRadius * scale, Math.Max(0.5f, type.TrunkHeight * scale), instance.Type);
        }
    }

    bool ICollider.TryAttach(Shapes shapes, BufferPool pool, ShapeCacheSystem shapeCache, bool shouldCalculateInertia, out TypedIndex index, out Vector3 centerOfMass, out BodyInertia inertia)
    {
        index = default;
        centerOfMass = Vector3.Zero;
        inertia = default;
        var terrain = FindTerrain();
        if (terrain?.Terrain == null || shouldCalculateInertia)
            return false; // Static only

        // Trunks of the same type and about the same size share a cylinder
        var cylinders = new Dictionary<(int Type, int Radius, int Height), TypedIndex>();
        var children = new List<CompoundChild>();
        foreach (var (position, radius, height, type) in Trunks(terrain))
        {
            var key = (type, (int)MathF.Round(radius * 50), (int)MathF.Round(height * 4));
            if (!cylinders.TryGetValue(key, out var cylinder))
            {
                cylinder = shapes.Add(new Cylinder(key.Item2 / 50.0f, key.Item3 / 4.0f));
                cylinders.Add(key, cylinder);
                attachedCylinders.Add(cylinder);
            }
            children.Add(new CompoundChild { LocalPosition = (position + new Vector3(0, key.Item3 / 8.0f, 0)).ToNumeric(), LocalOrientation = System.Numerics.Quaternion.Identity, ShapeIndex = cylinder });
        }

        attachedData = terrain.Terrain;
        attachedData.Changed += OnDataChanged;
        if (children.Count == 0)
            return false;

        pool.Take<CompoundChild>(children.Count, out var buffer);
        for (int i = 0; i < children.Count; i++)
            buffer[i] = children[i];
        index = shapes.Add(new BigCompound(buffer, shapes, pool));
        return true;
    }

    void ICollider.Detach(Shapes shapes, BufferPool pool, TypedIndex index)
    {
        if (attachedData != null)
            attachedData.Changed -= OnDataChanged;
        attachedData = null;
        shapes.RemoveAndDispose(index, pool);
        foreach (var cylinder in attachedCylinders)
            shapes.RemoveAndDispose(cylinder, pool);
        attachedCylinders.Clear();
    }

    private void OnDataChanged(object sender, TerrainDataChangedEventArgs e)
    {
        if ((e.Flags & (TerrainChangeFlags.Vegetation | TerrainChangeFlags.Heights)) != 0)
            component?.TryUpdateFeatures();
    }

    void ICollider.AppendModel(List<BasicMeshBuffers> buffer, ShapeCacheSystem shapeCache, out object cache)
    {
        cache = null;
        var vertices = new List<VertexPosition3>();
        var indices = new List<int>();
        foreach (var (position, radius, height, _) in Trunks(FindTerrain()))
            AddPrism(position, radius, height, vertices, indices, x => new VertexPosition3 { Position = x });
        buffer.Add(new BasicMeshBuffers { Vertices = vertices.ToArray(), Indices = indices.ToArray() });
    }

    void ICollider.RayTest<TRayHitHandler>(Shapes shapes, TypedIndex shapeIndex, in NRigidPose pose, in RayData ray, ref float maximumT, ref TRayHitHandler hitHandler, BufferPool pool)
    {
        shapes.GetShape<BigCompound>(shapeIndex.Index).RayTest(pose, in ray, ref maximumT, shapes, pool, ref hitHandler);
    }

    public void GetNavigationTriangles(CollidableComponent collidable, BoundingBox bounds, List<Vector3> vertices, List<int> indices)
    {
        var origin = collidable.Entity.Transform.WorldMatrix.TranslationVector;
        foreach (var (position, radius, height, _) in Trunks(FindTerrain()))
        {
            var world = position + origin;
            if (world.X + radius < bounds.Minimum.X || world.X - radius > bounds.Maximum.X || world.Z + radius < bounds.Minimum.Z || world.Z - radius > bounds.Maximum.Z)
                continue;
            AddPrism(world, radius, height, vertices, indices, x => x);
        }
    }

    public BoundingBox GetNavigationBounds(CollidableComponent collidable)
    {
        var data = FindTerrain()?.Terrain;
        var origin = collidable.Entity.Transform.WorldMatrix.TranslationVector;
        if (data == null)
            return new BoundingBox(origin, origin);
        return new BoundingBox(origin + new Vector3(0, data.MinHeight, 0), origin + new Vector3(data.Size, data.MaxHeight + 50, data.Size));
    }

    /// <summary>
    /// A closed prism around a trunk: sides facing out, the top facing up (counter-clockwise seen from outside).
    /// </summary>
    private static void AddPrism<T>(Vector3 bottom, float radius, float height, List<T> vertices, List<int> indices, Func<Vector3, T> make)
    {
        var start = vertices.Count;
        for (int i = 0; i < PrismSides; i++)
        {
            var angle = i * MathF.PI * 2 / PrismSides;
            var offset = new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            vertices.Add(make(bottom + offset));
            vertices.Add(make(bottom + offset + new Vector3(0, height, 0)));
        }
        for (int i = 0; i < PrismSides; i++)
        {
            var b0 = start + i * 2;
            var t0 = b0 + 1;
            var b1 = start + (i + 1) % PrismSides * 2;
            var t1 = b1 + 1;
            indices.Add(b0); indices.Add(t1); indices.Add(b1);
            indices.Add(b0); indices.Add(t0); indices.Add(t1);
        }
        for (int i = 1; i < PrismSides - 1; i++)
        {
            indices.Add(start + 1);
            indices.Add(start + (i + 1) * 2 + 1);
            indices.Add(start + i * 2 + 1);
        }
    }
}

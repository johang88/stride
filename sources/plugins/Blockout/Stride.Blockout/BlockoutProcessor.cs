// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.BepuPhysics;
using Stride.Blockout.Physics;
using Stride.Blockout.Rendering;
using Stride.Blockout.Shapes;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Blockout;

/// <summary>
/// Builds the meshes of the <see cref="BlockoutComponent"/>s (a model component added to their entity), rebuilt when the
/// shape changes, and has their <see cref="BlockoutCollider"/> follow.
/// </summary>
public sealed class BlockoutProcessor : EntityProcessor<BlockoutComponent, BlockoutProcessor.ShapeData>
{
    public sealed class ShapeData
    {
        internal ModelComponent Model;
        internal Buffer VertexBuffer;
        internal Buffer IndexBuffer;
        internal BlockoutGeometry Geometry;
        internal Material Material;
    }

    private IGraphicsDeviceService graphicsDeviceService;
    private BlockoutMaterials materials;

    public BlockoutProcessor() : base(typeof(TransformComponent))
    {
        Order = 100;
    }

    /// <summary>
    /// The blockout shapes of the scene.
    /// </summary>
    public IEnumerable<BlockoutComponent> Shapes => ComponentDatas.Keys;

    protected override void OnSystemAdd()
    {
        graphicsDeviceService = Services.GetService<IGraphicsDeviceService>();
        materials = Services.GetService<BlockoutMaterials>();
        if (materials == null)
        {
            materials = new BlockoutMaterials(Services);
            Services.AddService(materials);
        }
    }

    protected override ShapeData GenerateComponentData(Entity entity, BlockoutComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, BlockoutComponent component, ShapeData associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, BlockoutComponent component, ShapeData data)
    {
        if (data.Model != null)
        {
            entity.Remove(data.Model);
            data.Model = null;
        }
        ReleaseBuffers(data);
    }

    public override void Update(GameTime time)
    {
        foreach (var (component, data) in ComponentDatas)
        {
            if (!component.Enabled || graphicsDeviceService?.GraphicsDevice == null)
            {
                if (data.Model != null)
                    data.Model.Enabled = false;
                continue;
            }

            var geometry = component.GetGeometry();
            if (geometry != data.Geometry || data.Model == null)
            {
                data.Geometry = geometry;
                BuildModel(component, data, geometry);
                UpdateCollider(component);
            }

            var material = component.Material ?? materials.Get((component.Style ?? new BlockoutStyle()).Look);
            if (material != data.Material)
            {
                data.Material = material;
                data.Model.Model.Materials[0] = new MaterialInstance(material);
            }
            data.Model.Enabled = true;
            data.Model.IsShadowCaster = component.CastShadows;
            data.Model.RenderGroup = component.RenderGroup;
        }
    }

    private void BuildModel(BlockoutComponent component, ShapeData data, BlockoutGeometry geometry)
    {
        ReleaseBuffers(data);
        var model = new Model();
        model.Materials.Add(new MaterialInstance(data.Material));
        if (!geometry.IsEmpty)
        {
            var device = graphicsDeviceService.GraphicsDevice;
            data.VertexBuffer = Buffer.Vertex.New(device, geometry.Vertices);
            data.IndexBuffer = Buffer.Index.New(device, geometry.Indices);
            var bounds = geometry.Bounds;
            model.Meshes.Add(new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = geometry.Indices.Length,
                    IndexBuffer = new IndexBufferBinding(data.IndexBuffer, true, geometry.Indices.Length),
                    VertexBuffers = new[] { new VertexBufferBinding(data.VertexBuffer, BlockoutVertex.Layout, geometry.Vertices.Length) },
                },
                BoundingBox = bounds,
                BoundingSphere = BoundingSphere.FromBox(bounds),
                MaterialIndex = 0,
            });
            model.BoundingBox = bounds;
            model.BoundingSphere = BoundingSphere.FromBox(bounds);
        }

        if (data.Model == null)
        {
            data.Model = new ModelComponent();
            component.Entity.Add(data.Model);
        }
        data.Model.Model = model;
    }

    private static void UpdateCollider(BlockoutComponent component)
    {
        // The collider reads the shape when it attaches: attach it again with the new one
        var collidable = component.Entity.Get<CollidableComponent>();
        if (collidable?.Collider is BlockoutCollider collider && collider.AttachedHash != component.GeometryHash)
            collidable.TryUpdateFeatures();
    }

    private static void ReleaseBuffers(ShapeData data)
    {
        data.VertexBuffer?.Dispose();
        data.IndexBuffer?.Dispose();
        data.VertexBuffer = null;
        data.IndexBuffer = null;
    }
}

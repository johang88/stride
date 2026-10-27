// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Extensions;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Blockout.Editor;

/// <summary>
/// Lines of one color drawn over the scene (in the editor scene, with the transformation gizmos: never hidden).
/// </summary>
internal sealed class BlockoutLineBatch : IDisposable
{
    private const int MaxVertices = 1 << 14;
    private readonly List<VertexPositionNormalTexture> vertices = new();
    private readonly Buffer buffer;
    private readonly MeshDraw draw;
    private readonly ModelComponent model;

    public BlockoutLineBatch(GraphicsDevice device, Scene editorScene, string name, Color color, float intensity)
    {
        buffer = Buffer.Vertex.New(device, new VertexPositionNormalTexture[MaxVertices], GraphicsResourceUsage.Dynamic);
        draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.LineList,
            VertexBuffers = new[] { new VertexBufferBinding(buffer, VertexPositionNormalTexture.Layout, MaxVertices) },
        };
        var bounds = new BoundingBox(new Vector3(-1e6f), new Vector3(1e6f));
        var meshModel = new Model { GizmoEmissiveColorMaterial.Create(device, color, intensity) };
        meshModel.Meshes.Add(new Mesh { Draw = draw, MaterialIndex = 0, BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) });
        meshModel.BoundingBox = bounds;
        model = new ModelComponent { Model = meshModel, RenderGroup = TransformationGizmo.TransformationGizmoGroup, IsShadowCaster = false };
        Entity = new Entity(name) { model };
        editorScene.Entities.Add(Entity);
    }

    public Entity Entity { get; }

    public void Clear() => vertices.Clear();

    public void Line(Vector3 a, Vector3 b)
    {
        if (vertices.Count + 2 > MaxVertices)
            return;
        vertices.Add(new VertexPositionNormalTexture(a, Vector3.UnitY, Vector2.Zero));
        vertices.Add(new VertexPositionNormalTexture(b, Vector3.UnitY, Vector2.Zero));
    }

    /// <summary>
    /// Uploads the lines added since <see cref="Clear"/>.
    /// </summary>
    public void Upload(CommandList commandList)
    {
        draw.DrawCount = vertices.Count;
        model.Enabled = vertices.Count > 0;
        if (vertices.Count > 0)
            buffer.SetData(commandList, (ReadOnlySpan<VertexPositionNormalTexture>)vertices.ToArray());
    }

    public void Dispose()
    {
        Entity.Scene = null;
        buffer.Dispose();
    }
}

/// <summary>
/// A resize handle: a small cube kept the same size on screen.
/// </summary>
internal sealed class BlockoutHandleGizmo
{
    private readonly GraphicsDevice device;
    private readonly Material material;
    private readonly ModelComponent model;
    private readonly Color color;
    private bool highlighted;

    public BlockoutHandleGizmo(GraphicsDevice device, Scene editorScene, MeshDraw cube, Color color)
    {
        this.device = device;
        this.color = color;
        material = GizmoEmissiveColorMaterial.Create(device, color, 1.0f);
        var cubeModel = new Model { material };
        cubeModel.Meshes.Add(new Mesh { Draw = cube, MaterialIndex = 0 });
        model = new ModelComponent { Model = cubeModel, RenderGroup = TransformationGizmo.TransformationGizmoGroup, IsShadowCaster = false, Enabled = false };
        Entity = new Entity("Blockout handle") { model };
        editorScene.Entities.Add(Entity);
    }

    public Entity Entity { get; }

    public static MeshDraw CreateCube(GraphicsDevice device) => GeometricPrimitive.Cube.New(device, 1.0f).ToMeshDraw();

    public void Show(Vector3 position, Quaternion rotation, float size, bool highlight)
    {
        model.Enabled = true;
        Entity.Transform.Position = position;
        Entity.Transform.Rotation = rotation;
        Entity.Transform.Scale = new Vector3(size);
        if (highlight != highlighted)
        {
            highlighted = highlight;
            GizmoEmissiveColorMaterial.UpdateColor(device, material, highlight ? new Color(255, 230, 90) : color, highlight ? 1.5f : 1.0f);
        }
    }

    public void Hide() => model.Enabled = false;
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Decals.Editor.Gizmos;

/// <summary>
/// Draws the box of a decal and an arrow down the direction it projects in.
/// </summary>
[GizmoComponent(typeof(DecalComponent), false)]
public class DecalGizmo : EntityGizmo<DecalComponent>
{
    private Buffer vertexBuffer;
    private ModelComponent modelComponent;

    public DecalGizmo(EntityComponent component) : base(component)
    {
    }

    protected override Entity Create()
    {
        var lines = BuildLines();
        var material = GizmoEmissiveColorMaterial.Create(GraphicsDevice, new Color(255, 170, 70), 1.0f);
        vertexBuffer = Buffer.Vertex.New(GraphicsDevice, lines);
        var draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.LineList,
            VertexBuffers = [new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, lines.Length)],
            DrawCount = lines.Length,
        };
        var model = new Model { material };
        model.Meshes.Add(new Mesh { Draw = draw, MaterialIndex = 0 });
        return new Entity($"Decal gizmo of {Component.Entity.Name}")
        {
            (modelComponent = new ModelComponent { Model = model, RenderGroup = RenderGroup, IsShadowCaster = false, ReceiveDecals = false }),
        };
    }

    public override void Update()
    {
        if (ContentEntity == null || GizmoRootEntity == null)
            return;

        // The unit box, scaled to the decal and placed with its entity
        modelComponent.Enabled = Component.Enabled;
        ContentEntity.Transform.UpdateWorldMatrix();
        GizmoRootEntity.Transform.UseTRS = false;
        GizmoRootEntity.Transform.LocalMatrix = Matrix.Scaling(Vector3.Max(Component.Size, new Vector3(1e-3f))) * ContentEntity.Transform.WorldMatrix;
        GizmoRootEntity.Transform.UpdateWorldMatrix();
    }

    private static VertexPositionNormalTexture[] BuildLines()
    {
        var lines = new List<VertexPositionNormalTexture>();
        void Line(Vector3 a, Vector3 b)
        {
            lines.Add(new VertexPositionNormalTexture(a, Vector3.UnitY, Vector2.Zero));
            lines.Add(new VertexPositionNormalTexture(b, Vector3.UnitY, Vector2.Zero));
        }

        // The 12 edges of the box: along each axis, between the 4 corners of the other two
        for (int axis = 0; axis < 3; axis++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                var u = (corner & 1) != 0 ? 0.5f : -0.5f;
                var v = (corner & 2) != 0 ? 0.5f : -0.5f;
                var (a, b) = axis switch
                {
                    0 => (new Vector3(-0.5f, u, v), new Vector3(0.5f, u, v)),
                    1 => (new Vector3(u, -0.5f, v), new Vector3(u, 0.5f, v)),
                    _ => (new Vector3(u, v, -0.5f), new Vector3(u, v, 0.5f)),
                };
                Line(a, b);
            }
        }

        // The projection: an arrow down the Y axis, and a tick towards -Z (the top of the texture)
        var tip = new Vector3(0, -0.5f, 0);
        Line(new Vector3(0, 0.5f, 0), tip);
        Line(tip, new Vector3(0.1f, -0.35f, 0));
        Line(tip, new Vector3(-0.1f, -0.35f, 0));
        Line(new Vector3(0, -0.5f, 0), new Vector3(0, -0.5f, -0.25f));
        return lines.ToArray();
    }

    protected override void Destroy()
    {
        vertexBuffer?.Dispose();
        base.Destroy();
    }
}

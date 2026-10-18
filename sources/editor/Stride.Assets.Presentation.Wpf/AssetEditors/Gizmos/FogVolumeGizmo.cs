// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.VolumetricFog;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

/// <summary>
/// Draws the outline of a fog volume: the edges of its box, or three circles around its ellipsoid.
/// </summary>
[GizmoComponent(typeof(FogVolumeComponent), false)]
public class FogVolumeGizmo : EntityGizmo<FogVolumeComponent>
{
    private const int CircleSegments = 48;

    private Buffer vertexBuffer;
    private MeshDraw draw;
    private ModelComponent modelComponent;
    private FogVolumeShape? shape;

    public FogVolumeGizmo(EntityComponent component) : base(component)
    {
    }

    protected override Entity Create()
    {
        var material = GizmoEmissiveColorMaterial.Create(GraphicsDevice, new Color(120, 190, 255), 1.0f);
        vertexBuffer = Buffer.Vertex.New(GraphicsDevice, new VertexPositionNormalTexture[CircleSegments * 6], GraphicsResourceUsage.Dynamic);
        draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.LineList,
            VertexBuffers = [new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, CircleSegments * 6)],
        };
        var model = new Model { material };
        model.Meshes.Add(new Mesh { Draw = draw, MaterialIndex = 0 });
        return new Entity($"Fog volume gizmo of {Component.Entity.Name}")
        {
            (modelComponent = new ModelComponent { Model = model, RenderGroup = RenderGroup, IsShadowCaster = false }),
        };
    }

    public override void Update()
    {
        if (ContentEntity == null || GizmoRootEntity == null)
            return;

        if (shape != Component.Shape)
        {
            shape = Component.Shape;
            Rebuild(Component.Shape);
        }

        // The outline of the unit volume, scaled to the volume and placed with its entity
        modelComponent.Enabled = Component.Enabled;
        ContentEntity.Transform.UpdateWorldMatrix();
        GizmoRootEntity.Transform.UseTRS = false;
        GizmoRootEntity.Transform.LocalMatrix = Matrix.Scaling(Vector3.Max(Component.Size * 0.5f, new Vector3(1e-3f))) * ContentEntity.Transform.WorldMatrix;
        GizmoRootEntity.Transform.UpdateWorldMatrix();
    }

    private void Rebuild(FogVolumeShape volumeShape)
    {
        var lines = new List<VertexPositionNormalTexture>();
        void Line(Vector3 a, Vector3 b)
        {
            lines.Add(new VertexPositionNormalTexture(a, Vector3.UnitY, Vector2.Zero));
            lines.Add(new VertexPositionNormalTexture(b, Vector3.UnitY, Vector2.Zero));
        }

        if (volumeShape == FogVolumeShape.Sphere)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 Point(int i)
                {
                    var angle = i * MathF.Tau / CircleSegments;
                    var (sin, cos) = MathF.SinCos(angle);
                    return axis switch
                    {
                        0 => new Vector3(cos, sin, 0),
                        1 => new Vector3(cos, 0, sin),
                        _ => new Vector3(0, cos, sin),
                    };
                }
                for (int i = 0; i < CircleSegments; i++)
                    Line(Point(i), Point(i + 1));
            }
        }
        else
        {
            // The 12 edges of the cube: along each axis, between the 4 corners of the other two
            for (int axis = 0; axis < 3; axis++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    var u = (corner & 1) != 0 ? 1.0f : -1.0f;
                    var v = (corner & 2) != 0 ? 1.0f : -1.0f;
                    var (a, b) = axis switch
                    {
                        0 => (new Vector3(-1, u, v), new Vector3(1, u, v)),
                        1 => (new Vector3(u, -1, v), new Vector3(u, 1, v)),
                        _ => (new Vector3(u, v, -1), new Vector3(u, v, 1)),
                    };
                    Line(a, b);
                }
            }
        }

        draw.DrawCount = lines.Count;
        vertexBuffer.SetData(GraphicsCommandList, (ReadOnlySpan<VertexPositionNormalTexture>)lines.ToArray());
    }

    protected override void Destroy()
    {
        vertexBuffer?.Dispose();
        base.Destroy();
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain;
using Stride.Terrain.Decals;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

/// <summary>
/// Draws the outline of a terrain decal laid on the terrain, and an arrow along its local Z axis (direction of the texture V).
/// </summary>
[GizmoComponent(typeof(TerrainDecalComponent), false)]
public class TerrainDecalGizmo : EntityGizmo<TerrainDecalComponent>
{
    private const int MaxVertices = 4096;
    private const float Lift = 0.25f;

    private Material material;
    private Buffer vertexBuffer;
    private MeshDraw draw;
    private int hash;

    public TerrainDecalGizmo(EntityComponent component) : base(component)
    {
    }

    protected override Entity Create()
    {
        material = GizmoEmissiveColorMaterial.Create(GraphicsDevice, new Color(200, 120, 255), 1.5f);
        vertexBuffer = Buffer.Vertex.New(GraphicsDevice, new VertexPositionNormalTexture[MaxVertices], GraphicsResourceUsage.Dynamic);
        draw = new MeshDraw
        {
            PrimitiveType = PrimitiveType.LineList,
            VertexBuffers = new[] { new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, MaxVertices) },
        };
        var model = new Model { material };
        model.Meshes.Add(new Mesh { Draw = draw, MaterialIndex = 0 });
        return new Entity($"Decal of {Component.Entity.Name}")
        {
            new ModelComponent { Model = model, RenderGroup = RenderGroup, IsShadowCaster = false },
        };
    }

    public override void Update()
    {
        if (ContentEntity == null || GizmoRootEntity == null)
            return;

        // The gizmo is drawn in world space
        GizmoRootEntity.Transform.Position = Vector3.Zero;
        GizmoRootEntity.Transform.Rotation = Quaternion.Identity;
        GizmoRootEntity.Transform.Scale = Vector3.One;
        GizmoRootEntity.Transform.UpdateWorldMatrix();

        ContentEntity.Transform.UpdateWorldMatrix();
        var hashCode = new HashCode();
        hashCode.Add(ContentEntity.Transform.WorldMatrix);
        hashCode.Add(Component.Size);
        hashCode.Add(Component.Shape);
        var newHash = hashCode.ToHashCode();
        if (newHash == hash)
            return;
        hash = newHash;
        Rebuild();
    }

    private void Rebuild()
    {
        var terrains = ContentEntity.EntityManager?.GetProcessor<TerrainProcessor>()?.Terrains.Where(x => x.Enabled).ToList() ?? new List<TerrainComponent>();
        var world = ContentEntity.Transform.WorldMatrix;
        var center = world.TranslationVector;
        var axisX = new Vector3(world.M11, 0, world.M13) * (Component.Size.X * 0.5f);
        var axisZ = new Vector3(world.M31, 0, world.M33) * (Component.Size.Y * 0.5f);
        var lines = new List<VertexPositionNormalTexture>();

        Vector3 OnTerrain(Vector3 position)
        {
            foreach (var terrain in terrains)
            {
                if (terrain.GetHeight(position) is { } height)
                    return new Vector3(position.X, height + Lift, position.Z);
            }
            return new Vector3(position.X, center.Y, position.Z);
        }

        void Line(Vector3 a, Vector3 b)
        {
            if (lines.Count + 2 > MaxVertices)
                return;
            lines.Add(new VertexPositionNormalTexture(a, Vector3.UnitY, Vector2.Zero));
            lines.Add(new VertexPositionNormalTexture(b, Vector3.UnitY, Vector2.Zero));
        }

        // Outline, subdivided to follow the terrain
        const int Segments = 64;
        Vector3 Outline(float t)
        {
            if (Component.Shape == TerrainDecalShape.Ellipse)
            {
                var angle = t * MathF.Tau;
                return center + axisX * MathF.Cos(angle) + axisZ * MathF.Sin(angle);
            }
            // Around the rectangle, one side per quarter
            var side = Math.Min((int)(t * 4), 3);
            var s = t * 4 - side;
            return side switch
            {
                0 => center + axisX * (s * 2 - 1) - axisZ,
                1 => center + axisX + axisZ * (s * 2 - 1),
                2 => center + axisX * (1 - s * 2) + axisZ,
                _ => center - axisX + axisZ * (1 - s * 2),
            };
        }
        var previous = OnTerrain(Outline(0));
        for (int i = 1; i <= Segments; i++)
        {
            var next = OnTerrain(Outline(i / (float)Segments));
            Line(previous, next);
            previous = next;
        }

        // Arrow along +Z (texture V)
        var from = OnTerrain(center);
        var to = OnTerrain(center + axisZ);
        Line(from, to);
        var back = OnTerrain(center + axisZ * 0.75f + axisX * 0.15f);
        Line(to, back);
        back = OnTerrain(center + axisZ * 0.75f - axisX * 0.15f);
        Line(to, back);

        draw.DrawCount = lines.Count;
        if (lines.Count > 0)
            vertexBuffer.SetData(GraphicsCommandList, (ReadOnlySpan<VertexPositionNormalTexture>)lines.ToArray());
    }

    protected override void Destroy()
    {
        vertexBuffer?.Dispose();
        base.Destroy();
    }
}

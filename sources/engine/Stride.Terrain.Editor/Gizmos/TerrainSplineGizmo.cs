// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Gizmos;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain.Splines;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Assets.Presentation.AssetEditors.Gizmos
{
    /// <summary>
    /// Draws a terrain spline: the curve, its edges and the falloff band, and a marker at each control point.
    /// </summary>
    [GizmoComponent(typeof(TerrainSplineComponent), false)]
    public class TerrainSplineGizmo : EntityGizmo<TerrainSplineComponent>
    {
        private const int MaxVertices = 1 << 16;

        private Entity gizmoEntity;
        private Material curveMaterial;
        private Material edgeMaterial;
        private Buffer curveBuffer;
        private Buffer edgeBuffer;
        private MeshDraw curveDraw;
        private MeshDraw edgeDraw;
        private int hash;
        private TerrainSplineKind kind;

        public TerrainSplineGizmo(EntityComponent component) : base(component)
        {
        }

        protected override Entity Create()
        {
            kind = Component.Kind;
            curveMaterial = GizmoEmissiveColorMaterial.Create(GraphicsDevice, CurveColor(kind), 1.5f);
            edgeMaterial = GizmoEmissiveColorMaterial.Create(GraphicsDevice, CurveColor(kind) * 0.6f, 1.0f);
            curveBuffer = Buffer.Vertex.New(GraphicsDevice, new VertexPositionNormalTexture[MaxVertices], GraphicsResourceUsage.Dynamic);
            edgeBuffer = Buffer.Vertex.New(GraphicsDevice, new VertexPositionNormalTexture[MaxVertices], GraphicsResourceUsage.Dynamic);
            curveDraw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.LineList,
                VertexBuffers = new[] { new VertexBufferBinding(curveBuffer, VertexPositionNormalTexture.Layout, MaxVertices) },
            };
            edgeDraw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.LineList,
                VertexBuffers = new[] { new VertexBufferBinding(edgeBuffer, VertexPositionNormalTexture.Layout, MaxVertices) },
            };

            var model = new Model { curveMaterial, edgeMaterial };
            model.Meshes.Add(new Mesh { Draw = curveDraw, MaterialIndex = 0 });
            model.Meshes.Add(new Mesh { Draw = edgeDraw, MaterialIndex = 1 });
            gizmoEntity = new Entity($"Spline of {Component.Entity.Name}")
            {
                new ModelComponent { Model = model, RenderGroup = RenderGroup, IsShadowCaster = false },
            };
            return gizmoEntity;
        }

        private static Color CurveColor(TerrainSplineKind kind) => kind == TerrainSplineKind.River ? new Color(60, 160, 255) : new Color(255, 170, 40);

        public override void Update()
        {
            if (ContentEntity == null || GizmoRootEntity == null)
                return;

            // The gizmo is drawn in world space
            GizmoRootEntity.Transform.Position = Vector3.Zero;
            GizmoRootEntity.Transform.Rotation = Quaternion.Identity;
            GizmoRootEntity.Transform.Scale = Vector3.One;
            GizmoRootEntity.Transform.UpdateWorldMatrix();

            if (Component.Kind != kind)
            {
                kind = Component.Kind;
                GizmoEmissiveColorMaterial.UpdateColor(GraphicsDevice, curveMaterial, CurveColor(kind), 1.5f);
                GizmoEmissiveColorMaterial.UpdateColor(GraphicsDevice, edgeMaterial, CurveColor(kind) * 0.6f, 1.0f);
            }

            ContentEntity.Transform.UpdateWorldMatrix();
            var newHash = ComputeHash();
            if (newHash == hash)
                return;
            hash = newHash;
            Rebuild();
        }

        private int ComputeHash()
        {
            var hashCode = new HashCode();
            hashCode.Add(ContentEntity.Transform.WorldMatrix);
            foreach (var child in ContentEntity.Transform.Children)
            {
                hashCode.Add(child.Position);
                hashCode.Add(child.Scale.X);
            }
            hashCode.Add(Component.Width);
            hashCode.Add(Component.Falloff);
            hashCode.Add(Component.Closed);
            hashCode.Add(Component.SampleSpacing);
            return hashCode.ToHashCode();
        }

        private void Rebuild()
        {
            var curve = Component.Evaluate();
            var samples = curve.Samples;
            var curveLines = new List<VertexPositionNormalTexture>();
            var edgeLines = new List<VertexPositionNormalTexture>();
            const float lift = 0.15f;

            void Line(List<VertexPositionNormalTexture> lines, Vector3 a, Vector3 b)
            {
                if (lines.Count + 2 > MaxVertices)
                    return;
                lines.Add(new VertexPositionNormalTexture(a, Vector3.UnitY, Vector2.Zero));
                lines.Add(new VertexPositionNormalTexture(b, Vector3.UnitY, Vector2.Zero));
            }

            var up = new Vector3(0, lift, 0);
            for (int i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                Line(curveLines, a.Position + up, b.Position + up);

                var rightA = Right(a.Tangent);
                var rightB = Right(b.Tangent);
                // Edges
                Line(edgeLines, a.Position + rightA * (a.Width * 0.5f) + up, b.Position + rightB * (b.Width * 0.5f) + up);
                Line(edgeLines, a.Position - rightA * (a.Width * 0.5f) + up, b.Position - rightB * (b.Width * 0.5f) + up);
                // Falloff band, dashed
                if (Component.Falloff > 0 && (i & 1) == 0)
                {
                    var fa = a.Width * 0.5f + Component.Falloff;
                    var fb = b.Width * 0.5f + Component.Falloff;
                    Line(edgeLines, a.Position + rightA * fa + up, b.Position + rightB * fb + up);
                    Line(edgeLines, a.Position - rightA * fa + up, b.Position - rightB * fb + up);
                }
            }

            // Control point markers: a vertical tick and a cross across the width
            foreach (var point in curve.Points)
            {
                Line(curveLines, point.Position, point.Position + new Vector3(0, 2.0f, 0));
                Line(curveLines, point.Position + new Vector3(-0.5f, lift, 0), point.Position + new Vector3(0.5f, lift, 0));
                Line(curveLines, point.Position + new Vector3(0, lift, -0.5f), point.Position + new Vector3(0, lift, 0.5f));
            }

            Upload(curveBuffer, curveDraw, curveLines);
            Upload(edgeBuffer, edgeDraw, edgeLines);
        }

        private void Upload(Buffer buffer, MeshDraw draw, List<VertexPositionNormalTexture> vertices)
        {
            draw.DrawCount = vertices.Count;
            if (vertices.Count > 0)
                buffer.SetData(GraphicsCommandList, (ReadOnlySpan<VertexPositionNormalTexture>)vertices.ToArray());
        }

        private static Vector3 Right(Vector3 tangent)
        {
            var right = Vector3.Cross(tangent, Vector3.UnitY);
            right.Y = 0;
            return right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitX;
        }

        protected override void Destroy()
        {
            curveBuffer?.Dispose();
            edgeBuffer?.Dispose();
            base.Destroy();
        }
    }
}

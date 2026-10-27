// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Blockout.Shapes;

/// <summary>
/// How the pattern of a blockout material maps onto a vertex's surface (<see cref="BlockoutVertex.Mapping"/>).
/// </summary>
public enum BlockoutMapping
{
    /// <summary>
    /// A flat face: the pattern is projected along its normal, anchored to the world grid so neighboring shapes line up.
    /// </summary>
    Planar = 0,

    /// <summary>
    /// A surface around a vertical axis (cylinder sides): horizontally along the texture coordinate (arc length), vertically
    /// anchored to the world grid.
    /// </summary>
    Around = 1,

    /// <summary>
    /// A curved surface: the texture coordinates (in meters) only.
    /// </summary>
    Surface = 2,
}

/// <summary>
/// A vertex of the blockout meshes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BlockoutVertex
{
    public Vector3 Position;
    public Vector3 Normal;

    /// <summary>
    /// Texture coordinates in meters along the surface (what a material override samples).
    /// </summary>
    public Vector2 TexCoord;

    /// <summary>
    /// X: the <see cref="BlockoutMapping"/> of the pattern.
    /// </summary>
    public Vector2 Mapping;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector3>(),
        VertexElement.Normal<Vector3>(),
        VertexElement.TextureCoordinate<Vector2>(0),
        VertexElement.TextureCoordinate<Vector2>(1));
}

/// <summary>
/// The triangles of a blockout shape in the local space of its entity (Stride winding: clockwise seen from the front).
/// </summary>
public sealed class BlockoutGeometry
{
    public static readonly BlockoutGeometry Empty = new(Array.Empty<BlockoutVertex>(), Array.Empty<int>());

    public BlockoutGeometry(BlockoutVertex[] vertices, int[] indices)
    {
        Vertices = vertices;
        Indices = indices;
        if (vertices.Length == 0)
        {
            Bounds = new BoundingBox(Vector3.Zero, Vector3.Zero);
            return;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }
        Bounds = new BoundingBox(min, max);
    }

    public BlockoutVertex[] Vertices { get; }

    public int[] Indices { get; }

    public int TriangleCount => Indices.Length / 3;

    public BoundingBox Bounds { get; }

    public bool IsEmpty => Indices.Length == 0;

    /// <summary>
    /// Intersects a ray (local space) with the triangles, front faces only.
    /// </summary>
    /// <param name="ray">The ray in the local space of the geometry.</param>
    /// <param name="distance">The distance along the ray to the closest hit (in units of the ray direction).</param>
    /// <param name="normal">The geometric normal of the triangle hit.</param>
    public bool Raycast(in Ray ray, out float distance, out Vector3 normal)
    {
        distance = float.MaxValue;
        normal = Vector3.Zero;
        // Flat shapes have flat bounds
        var bounds = new BoundingBox(Bounds.Minimum - new Vector3(1e-3f), Bounds.Maximum + new Vector3(1e-3f));
        if (IsEmpty || !ray.Intersects(ref bounds, out float _))
            return false;

        var hit = false;
        for (int i = 0; i < Indices.Length; i += 3)
        {
            var a = Vertices[Indices[i]].Position;
            var b = Vertices[Indices[i + 1]].Position;
            var c = Vertices[Indices[i + 2]].Position;
            // Front faces are clockwise: their outward normal is (c - a) x (b - a)
            var n = Vector3.Cross(c - a, b - a);
            if (Vector3.Dot(n, ray.Direction) >= 0)
                continue;
            if (!IntersectTriangle(ray, a, b, c, out var t) || t >= distance)
                continue;
            distance = t;
            normal = Vector3.Normalize(n);
            hit = true;
        }
        return hit;
    }

    // Möller–Trumbore, both sides
    private static bool IntersectTriangle(in Ray ray, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(ray.Direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
            return false;
        var inv = 1.0f / det;
        var s = ray.Position - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
            return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(ray.Direction, q) * inv;
        if (v < 0 || u + v > 1)
            return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0;
    }
}

/// <summary>
/// Builds the triangles of a blockout shape; faces are given with their outward normal and wound for Stride by the builder.
/// </summary>
public sealed class BlockoutMeshBuilder
{
    private readonly List<BlockoutVertex> vertices = new();
    private readonly List<int> indices = new();

    public int VertexCount => vertices.Count;

    /// <summary>
    /// Adds a vertex.
    /// </summary>
    public int AddVertex(Vector3 position, Vector3 normal, Vector2 texCoord, BlockoutMapping mapping)
    {
        vertices.Add(new BlockoutVertex { Position = position, Normal = normal, TexCoord = texCoord, Mapping = new Vector2((int)mapping, 0) });
        return vertices.Count - 1;
    }

    /// <summary>
    /// Adds a triangle of vertices added before, wound to face the side of their normals. Degenerate triangles are skipped.
    /// </summary>
    public void AddTriangle(int a, int b, int c)
    {
        var pa = vertices[a].Position;
        var pb = vertices[b].Position;
        var pc = vertices[c].Position;
        var cross = Vector3.Cross(pb - pa, pc - pa);
        if (cross.LengthSquared() < 1e-14f)
            return;
        var normal = vertices[a].Normal + vertices[b].Normal + vertices[c].Normal;
        // Stride front faces are clockwise: (b - a) x (c - a) points inward
        if (Vector3.Dot(cross, normal) > 0)
            (b, c) = (c, b);
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    /// <summary>
    /// Adds a quad of vertices added before (a, b, c, d around it).
    /// </summary>
    public void AddQuad(int a, int b, int c, int d)
    {
        AddTriangle(a, b, c);
        AddTriangle(a, c, d);
    }

    /// <summary>
    /// Adds a flat convex polygon (points in order around it, either direction) facing <paramref name="normal"/>, with planar
    /// texture coordinates in meters.
    /// </summary>
    public void AddPolygon(ReadOnlySpan<Vector3> points, Vector3 normal)
    {
        if (points.Length < 3)
            return;
        normal = Vector3.Normalize(normal);
        GetFaceAxes(normal, out var u, out var v);
        var first = vertices.Count;
        foreach (var point in points)
            AddVertex(point, normal, new Vector2(Vector3.Dot(point, u), Vector3.Dot(point, v)), BlockoutMapping.Planar);
        for (int i = 1; i < points.Length - 1; i++)
            AddTriangle(first, first + i, first + i + 1);
    }

    /// <summary>
    /// Adds a flat quad facing <paramref name="normal"/>.
    /// </summary>
    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        AddPolygon(stackalloc[] { a, b, c, d }, normal);
    }

    /// <summary>
    /// Adds the six faces of an axis aligned box.
    /// </summary>
    public void AddBox(Vector3 min, Vector3 max)
    {
        if (max.X - min.X <= 0 || max.Y - min.Y <= 0 || max.Z - min.Z <= 0)
            return;
        AddFaceX(min.X, min.Y, max.Y, min.Z, max.Z, -1);
        AddFaceX(max.X, min.Y, max.Y, min.Z, max.Z, 1);
        AddFaceY(min.Y, min.X, max.X, min.Z, max.Z, -1);
        AddFaceY(max.Y, min.X, max.X, min.Z, max.Z, 1);
        AddFaceZ(min.Z, min.X, max.X, min.Y, max.Y, -1);
        AddFaceZ(max.Z, min.X, max.X, min.Y, max.Y, 1);
    }

    /// <summary>
    /// Adds a prism: a convex profile in the ZY plane (points as (z, y), in order around it) extruded from x0 to x1.
    /// </summary>
    public void AddExtrudedX(ReadOnlySpan<Vector2> profile, float x0, float x1)
    {
        if (profile.Length < 3 || x1 - x0 <= 0)
            return;
        var center = Vector2.Zero;
        foreach (var point in profile)
            center += point;
        center /= profile.Length;

        Span<Vector3> cap = stackalloc Vector3[profile.Length];
        for (int i = 0; i < profile.Length; i++)
            cap[i] = new Vector3(x0, profile[i].Y, profile[i].X);
        AddPolygon(cap, -Vector3.UnitX);
        for (int i = 0; i < profile.Length; i++)
            cap[i].X = x1;
        AddPolygon(cap, Vector3.UnitX);

        for (int i = 0; i < profile.Length; i++)
        {
            var p = profile[i];
            var q = profile[(i + 1) % profile.Length];
            var edge = q - p;
            if (edge.LengthSquared() < 1e-12f)
                continue;
            // Perpendicular to the edge, away from the center
            var n = new Vector2(-edge.Y, edge.X);
            if (Vector2.Dot(n, (p + q) * 0.5f - center) < 0)
                n = -n;
            AddQuad(new Vector3(x0, p.Y, p.X), new Vector3(x1, p.Y, p.X), new Vector3(x1, q.Y, q.X), new Vector3(x0, q.Y, q.X), new Vector3(0, n.Y, n.X));
        }
    }

    /// <summary>
    /// Adds a rectangle in the plane x = <paramref name="x"/>, facing +X (sign 1) or -X (sign -1).
    /// </summary>
    public void AddFaceX(float x, float y0, float y1, float z0, float z1, int sign)
    {
        if (y1 - y0 <= 0 || z1 - z0 <= 0)
            return;
        AddQuad(new Vector3(x, y0, z0), new Vector3(x, y1, z0), new Vector3(x, y1, z1), new Vector3(x, y0, z1), new Vector3(sign, 0, 0));
    }

    /// <summary>
    /// Adds a rectangle in the plane y = <paramref name="y"/>, facing +Y (sign 1) or -Y (sign -1).
    /// </summary>
    public void AddFaceY(float y, float x0, float x1, float z0, float z1, int sign)
    {
        if (x1 - x0 <= 0 || z1 - z0 <= 0)
            return;
        AddQuad(new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), new Vector3(0, sign, 0));
    }

    /// <summary>
    /// Adds a rectangle in the plane z = <paramref name="z"/>, facing +Z (sign 1) or -Z (sign -1).
    /// </summary>
    public void AddFaceZ(float z, float x0, float x1, float y0, float y1, int sign)
    {
        if (x1 - x0 <= 0 || y1 - y0 <= 0)
            return;
        AddQuad(new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z), new Vector3(0, 0, sign));
    }

    /// <summary>
    /// The texture axes of a flat face (the pattern shader uses the same ones): floors along X and Z, walls and slopes with V
    /// up the face.
    /// </summary>
    public static void GetFaceAxes(Vector3 normal, out Vector3 u, out Vector3 v)
    {
        var ax = MathF.Abs(normal.X);
        var ay = MathF.Abs(normal.Y);
        var az = MathF.Abs(normal.Z);
        if (ay >= ax && ay >= az)
        {
            u = Vector3.Normalize(Vector3.UnitX - normal * normal.X);
            v = Vector3.Cross(u, normal);
        }
        else
        {
            v = Vector3.Normalize(Vector3.UnitY - normal * normal.Y);
            u = Vector3.Cross(v, normal);
        }
    }

    public BlockoutGeometry ToGeometry() => new(vertices.ToArray(), indices.ToArray());
}

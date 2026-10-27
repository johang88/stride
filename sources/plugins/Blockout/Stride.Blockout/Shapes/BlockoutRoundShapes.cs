// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Shapes;

/// <summary>
/// A cylinder (elliptic when the width and depth differ), possibly hollow (a pipe, a tower) and partial (a curved wall).
/// </summary>
/// <remarks>
/// A partial cylinder sweeps from +X toward the front (-Z): with its center in, it fills the size (90 degrees: a quarter
/// with its center at the back left corner, 180: a half with its center at the middle of the back).
/// </remarks>
[DataContract("BlockoutCylinder")]
[Display("Cylinder")]
public sealed class BlockoutCylinder : BlockoutShape
{
    [DataMember(10)]
    [DataMemberRange(3, 128, 1, 4, 0)]
    public int Sides { get; set; } = 16;

    /// <summary>
    /// Whether the sides are shaded round; otherwise they are flat and touch the edges of the size.
    /// </summary>
    [DataMember(20)]
    [Display("Smooth sides")]
    public bool Smooth { get; set; } = true;

    /// <summary>
    /// The angle covered, in degrees (360: a full cylinder).
    /// </summary>
    [DataMember(30)]
    [Display("Sweep (degrees)")]
    [DataMemberRange(1, 360, 1, 15, 0)]
    public float Sweep { get; set; } = 360;

    /// <summary>
    /// The thickness of a hollow cylinder (0: solid).
    /// </summary>
    [DataMember(40)]
    [Display("Wall thickness (m)")]
    [DataMemberRange(0, 100, 0.05, 0.25, 2)]
    public float WallThickness { get; set; }

    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var ring = RoundProfile.Create(size, Sides, Smooth, Sweep);
        var height = size.Y;
        var hollow = WallThickness > 0;
        var thickness = Math.Min(WallThickness, Math.Min(ring.RadiusX, ring.RadiusZ) * 0.98f);
        var inner = hollow ? ring.Inset(thickness) : null;

        // Sides (and the inside of a hollow one)
        AddSides(builder, ring.Points, ring.Normals, ring.Smooth, ring.Closed, height, 1);
        if (hollow)
            AddSides(builder, inner, ring.Normals, ring.Smooth, ring.Closed, height, -1);

        // Top and bottom
        if (!hollow)
        {
            var cap = new List<Vector3>();
            if (!ring.Closed)
                cap.Add(new Vector3(ring.Center.X, 0, ring.Center.Y));
            for (int i = 0; i < ring.Count; i++)
                cap.Add(new Vector3(ring.Points[i].X, 0, ring.Points[i].Y));
            var bottom = cap.ToArray();
            builder.AddPolygon(bottom, -Vector3.UnitY);
            for (int i = 0; i < bottom.Length; i++)
                bottom[i].Y = height;
            builder.AddPolygon(bottom, Vector3.UnitY);
        }
        else
        {
            var segments = ring.Closed ? ring.Count : ring.Count - 1;
            for (int i = 0; i < segments; i++)
            {
                var j = (i + 1) % ring.Count;
                foreach (var (y, sign) in new[] { (0.0f, -1), (height, 1) })
                {
                    builder.AddQuad(new Vector3(ring.Points[i].X, y, ring.Points[i].Y), new Vector3(ring.Points[j].X, y, ring.Points[j].Y),
                        new Vector3(inner[j].X, y, inner[j].Y), new Vector3(inner[i].X, y, inner[i].Y), new Vector3(0, sign, 0));
                }
            }
        }

        // The ends of a partial cylinder
        if (!ring.Closed)
        {
            var start = hollow ? inner[0] : ring.Center;
            var end = hollow ? inner[^1] : ring.Center;
            AddEnd(builder, start, ring.Points[0], -ring.Tangent(0), height);
            AddEnd(builder, end, ring.Points[^1], ring.Tangent(ring.Count - 1), height);
        }
    }

    private static void AddEnd(BlockoutMeshBuilder builder, Vector2 a, Vector2 b, Vector2 normal, float height)
    {
        builder.AddQuad(new Vector3(a.X, 0, a.Y), new Vector3(b.X, 0, b.Y), new Vector3(b.X, height, b.Y), new Vector3(a.X, height, a.Y), new Vector3(normal.X, 0, normal.Y));
    }

    private static void AddSides(BlockoutMeshBuilder builder, Vector2[] points, Vector2[] normals, bool smooth, bool closed, float height, int sign)
    {
        var count = points.Length;
        var segments = closed ? count : count - 1;
        var length = 0.0f;
        for (int i = 0; i < segments; i++)
        {
            var j = (i + 1) % count;
            var p = points[i];
            var q = points[j];
            var next = length + Vector2.Distance(p, q);
            Vector3 np, nq;
            if (smooth)
            {
                np = new Vector3(normals[i].X, 0, normals[i].Y) * sign;
                nq = new Vector3(normals[j].X, 0, normals[j].Y) * sign;
            }
            else
            {
                var edge = q - p;
                var flat = Vector2.Normalize(new Vector2(edge.Y, -edge.X));
                if (Vector2.Dot(flat, normals[i] + normals[j]) < 0)
                    flat = -flat;
                np = nq = new Vector3(flat.X, 0, flat.Y) * sign;
            }
            var a = builder.AddVertex(new Vector3(p.X, 0, p.Y), np, new Vector2(length, 0), BlockoutMapping.Around);
            var b = builder.AddVertex(new Vector3(q.X, 0, q.Y), nq, new Vector2(next, 0), BlockoutMapping.Around);
            var c = builder.AddVertex(new Vector3(q.X, height, q.Y), nq, new Vector2(next, height), BlockoutMapping.Around);
            var d = builder.AddVertex(new Vector3(p.X, height, p.Y), np, new Vector2(length, height), BlockoutMapping.Around);
            builder.AddQuad(a, b, c, d);
            length = next;
        }
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(5);
        hash.Add(Sides);
        hash.Add(Smooth);
        hash.Add(Sweep);
        hash.Add(WallThickness);
    }
}

/// <summary>
/// A cone, or a frustum with a smaller top; with 4 flat sides, a pyramid.
/// </summary>
[DataContract("BlockoutCone")]
[Display("Cone")]
public sealed class BlockoutCone : BlockoutShape
{
    [DataMember(10)]
    [DataMemberRange(3, 128, 1, 4, 0)]
    public int Sides { get; set; } = 16;

    /// <summary>
    /// Whether the sides are shaded round; otherwise they are flat and touch the edges of the size.
    /// </summary>
    [DataMember(20)]
    [Display("Smooth sides")]
    public bool Smooth { get; set; } = true;

    /// <summary>
    /// The size of the top relative to the bottom (0: a point).
    /// </summary>
    [DataMember(30)]
    [Display("Top size")]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float TopScale { get; set; }

    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var ring = RoundProfile.Create(size, Sides, Smooth, 360);
        var top = Math.Clamp(TopScale, 0, 1);
        var height = size.Y;
        var count = ring.Count;
        Vector2 Top(int i) => ring.Center + (ring.Points[i] - ring.Center) * top;

        var length = 0.0f;
        for (int i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var p0 = new Vector3(ring.Points[i].X, 0, ring.Points[i].Y);
            var q0 = new Vector3(ring.Points[j].X, 0, ring.Points[j].Y);
            var p1 = new Vector3(Top(i).X, height, Top(i).Y);
            var q1 = new Vector3(Top(j).X, height, Top(j).Y);
            var next = length + Vector3.Distance(p0, q0);
            var slant = Vector3.Distance(p0, p1);
            Vector3 np, nq;
            if (Smooth)
            {
                np = SlopeNormal(ring.Normals[i], ring.Points[i] - ring.Center, height, top);
                nq = SlopeNormal(ring.Normals[j], ring.Points[j] - ring.Center, height, top);
            }
            else
            {
                var face = Vector3.Cross(q0 - p0, (p1 + q1) * 0.5f - p0);
                var outward = new Vector3(ring.Normals[i].X + ring.Normals[j].X, 0, ring.Normals[i].Y + ring.Normals[j].Y);
                np = nq = Vector3.Normalize(Vector3.Dot(face, outward) < 0 ? -face : face);
            }
            var a = builder.AddVertex(p0, np, new Vector2(length, 0), BlockoutMapping.Surface);
            var b = builder.AddVertex(q0, nq, new Vector2(next, 0), BlockoutMapping.Surface);
            var c = builder.AddVertex(q1, nq, new Vector2(next, slant), BlockoutMapping.Surface);
            var d = builder.AddVertex(p1, np, new Vector2(length, slant), BlockoutMapping.Surface);
            builder.AddQuad(a, b, c, d);
            length = next;
        }

        var cap = new Vector3[count];
        for (int i = 0; i < count; i++)
            cap[i] = new Vector3(ring.Points[i].X, 0, ring.Points[i].Y);
        builder.AddPolygon(cap, -Vector3.UnitY);
        if (top > 0.001f)
        {
            for (int i = 0; i < count; i++)
                cap[i] = new Vector3(Top(i).X, height, Top(i).Y);
            builder.AddPolygon(cap, Vector3.UnitY);
        }
    }

    // Normal of the side of a cone: tilted up by how much the radius shrinks over the height
    private static Vector3 SlopeNormal(Vector2 normal, Vector2 radial, float height, float top)
    {
        var shrink = radial.Length() * (1 - top);
        var n = new Vector3(normal.X * height, shrink, normal.Y * height);
        return n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(6);
        hash.Add(Sides);
        hash.Add(Smooth);
        hash.Add(TopScale);
    }
}

/// <summary>
/// A sphere (an ellipsoid when the sizes differ), or a dome.
/// </summary>
[DataContract("BlockoutSphere")]
[Display("Sphere")]
public sealed class BlockoutSphere : BlockoutShape
{
    /// <summary>
    /// The number of segments around.
    /// </summary>
    [DataMember(10)]
    [DataMemberRange(6, 128, 1, 4, 0)]
    public int Segments { get; set; } = 24;

    /// <summary>
    /// Whether only the top half is built, its flat side on the ground (the height is the radius).
    /// </summary>
    [DataMember(20)]
    [Display("Dome")]
    public bool Hemisphere { get; set; }

    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var around = Math.Clamp(Segments, 6, 128);
        var rings = Math.Max(2, Hemisphere ? around / 4 : around / 2);
        var radius = new Vector3(size.X * 0.5f, Hemisphere ? size.Y : size.Y * 0.5f, size.Z * 0.5f);
        var centerY = Hemisphere ? 0 : size.Y * 0.5f;
        var minLatitude = Hemisphere ? 0 : -MathF.PI * 0.5f;
        var meanRadius = (radius.X + radius.Z) * 0.5f;

        var first = builder.VertexCount;
        for (int r = 0; r <= rings; r++)
        {
            var latitude = MathUtil.Lerp(minLatitude, MathF.PI * 0.5f, r / (float)rings);
            for (int s = 0; s <= around; s++)
            {
                var longitude = MathF.PI * 2 * s / around;
                var unit = new Vector3(MathF.Cos(latitude) * MathF.Cos(longitude), MathF.Sin(latitude), -MathF.Cos(latitude) * MathF.Sin(longitude));
                var position = new Vector3(unit.X * radius.X, centerY + unit.Y * radius.Y, unit.Z * radius.Z);
                var normal = Vector3.Normalize(new Vector3(unit.X / radius.X, unit.Y / radius.Y, unit.Z / radius.Z));
                builder.AddVertex(position, normal, new Vector2(longitude * meanRadius, latitude * radius.Y), BlockoutMapping.Surface);
            }
        }
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < around; s++)
            {
                var a = first + r * (around + 1) + s;
                var c = a + around + 1;
                builder.AddQuad(a, a + 1, c + 1, c);
            }
        }

        if (Hemisphere)
        {
            var cap = new Vector3[around];
            for (int s = 0; s < around; s++)
            {
                var longitude = MathF.PI * 2 * s / around;
                cap[s] = new Vector3(MathF.Cos(longitude) * radius.X, 0, -MathF.Sin(longitude) * radius.Z);
            }
            builder.AddPolygon(cap, -Vector3.UnitY);
        }
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(7);
        hash.Add(Segments);
        hash.Add(Hemisphere);
    }
}

/// <summary>
/// The outline of a round shape seen from above, fitted to the size (points as (x, z)).
/// </summary>
internal sealed class RoundProfile
{
    public Vector2[] Points;
    // Outward normals of the outline at the points
    public Vector2[] Normals;
    public Vector2 Center;
    public float RadiusX, RadiusZ;
    public bool Closed;
    public bool Smooth;

    public int Count => Points.Length;

    public static RoundProfile Create(Vector3 size, int sides, bool smooth, float sweepDegrees)
    {
        sides = Math.Clamp(sides, 3, 128);
        var sweep = MathUtil.DegreesToRadians(Math.Clamp(sweepDegrees, 1, 360));
        var closed = sweepDegrees >= 359.999f;
        var segments = closed ? sides : Math.Max(1, (int)MathF.Round(sides * sweep / (MathF.PI * 2)));
        var count = closed ? segments : segments + 1;

        // Flat sides of a full outline face the axes and touch the edges of the size
        var offset = closed && !smooth ? MathF.PI / sides : 0;
        var scale = closed && !smooth ? 1 / MathF.Cos(MathF.PI / sides) : 1;

        var unit = new Vector2[count];
        var min = closed ? new Vector2(float.MaxValue) : Vector2.Zero;
        var max = closed ? new Vector2(float.MinValue) : Vector2.Zero;
        for (int i = 0; i < count; i++)
        {
            var angle = offset + sweep * i / segments;
            unit[i] = new Vector2(MathF.Cos(angle), -MathF.Sin(angle)) * scale;
            min = Vector2.Min(min, unit[i]);
            max = Vector2.Max(max, unit[i]);
        }
        // A full outline is centered; a partial one fits with its center
        if (closed)
        {
            min = new Vector2(-1);
            max = new Vector2(1);
        }
        var extent = Vector2.Max(max - min, new Vector2(1e-4f));
        var radiusX = size.X / extent.X;
        var radiusZ = size.Z / extent.Y;
        var center = new Vector2(-size.X * 0.5f - min.X * radiusX, -size.Z * 0.5f - min.Y * radiusZ);

        var profile = new RoundProfile
        {
            Points = new Vector2[count],
            Normals = new Vector2[count],
            Center = center,
            RadiusX = radiusX,
            RadiusZ = radiusZ,
            Closed = closed,
            Smooth = smooth,
        };
        for (int i = 0; i < count; i++)
        {
            profile.Points[i] = center + new Vector2(unit[i].X * radiusX, unit[i].Y * radiusZ);
            profile.Normals[i] = Vector2.Normalize(new Vector2(unit[i].X / radiusX, unit[i].Y / radiusZ));
        }
        return profile;
    }

    /// <summary>
    /// The outline moved inward by a distance.
    /// </summary>
    public Vector2[] Inset(float distance)
    {
        var result = new Vector2[Count];
        for (int i = 0; i < Count; i++)
            result[i] = Points[i] - Normals[i] * distance;
        return result;
    }

    /// <summary>
    /// The direction along the outline (increasing angle) at a point.
    /// </summary>
    public Vector2 Tangent(int i)
    {
        var n = Normals[i];
        // Turning from +X toward -Z (clockwise seen from above): the tangent is the normal turned by -90 degrees
        return new Vector2(n.Y, -n.X);
    }
}

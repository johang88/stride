// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Shapes;

/// <summary>
/// A hole through a <see cref="BlockoutWall"/>: a door (from the ground), a window, an arch.
/// </summary>
[DataContract("BlockoutOpening")]
public sealed class BlockoutOpening
{
    /// <summary>
    /// The horizontal position of the opening's center from the center of the wall (+X to the right).
    /// </summary>
    [DataMember(10)]
    [Display("Offset (m)")]
    public float Offset { get; set; }

    /// <summary>
    /// The height of the bottom of the opening (0: a door).
    /// </summary>
    [DataMember(20)]
    [Display("Bottom (m)")]
    [DataMemberRange(0, 3)]
    public float Bottom { get; set; }

    [DataMember(30)]
    [Display("Width (m)")]
    [DataMemberRange(0.01, 3)]
    public float Width { get; set; } = 1.2f;

    /// <summary>
    /// The height of the opening, its arch included.
    /// </summary>
    [DataMember(40)]
    [Display("Height (m)")]
    [DataMemberRange(0.01, 3)]
    public float Height { get; set; } = 2.2f;

    /// <summary>
    /// Whether the top of the opening is a round arch (half the width high, less when the opening is lower).
    /// </summary>
    [DataMember(50)]
    public bool Arched { get; set; }

    public BlockoutOpening Clone() => (BlockoutOpening)MemberwiseClone();
}

/// <summary>
/// A wall across the X axis (its thickness is the depth), with openings through it.
/// </summary>
[DataContract("BlockoutWall")]
[Display("Wall")]
public sealed class BlockoutWall : BlockoutShape
{
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// Doors, windows and arches through the wall. Overlapping openings are ignored.
    /// </summary>
    [DataMember(10)]
    public List<BlockoutOpening> Openings { get; } = new();

    /// <summary>
    /// The number of segments of the arches.
    /// </summary>
    [DataMember(20)]
    [Display("Arch segments")]
    [DataMemberRange(2, 64, 1, 4, 0)]
    public int ArchSegments { get; set; } = 12;

    private sealed class Hole
    {
        public float X0, X1, Y0, Y1;
        public bool Arched;
        public float Spring, CenterX, RadiusX, Rise;
        public Vector2[] Arc;

        // The height of the arch polyline at x (the arc points go left to right)
        public float ArcY(float x)
        {
            if (x <= Arc[0].X)
                return Arc[0].Y;
            for (int i = 1; i < Arc.Length; i++)
            {
                if (x <= Arc[i].X)
                {
                    var t = (x - Arc[i - 1].X) / Math.Max(Arc[i].X - Arc[i - 1].X, 1e-6f);
                    return MathUtil.Lerp(Arc[i - 1].Y, Arc[i].Y, t);
                }
            }
            return Arc[^1].Y;
        }
    }

    // Cell states
    private const int Solid = 0, Open = 1, ArchCell = 2, Outside = 3;

    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var left = -size.X * 0.5f;
        var right = size.X * 0.5f;
        var zFront = -size.Z * 0.5f;
        var zBack = size.Z * 0.5f;
        var holes = GetHoles(size);

        // The wall is a grid of cells cut by the edges of the openings, extruded through the thickness
        var xs = Coordinates(new[] { left, right }.Concat(holes.SelectMany(x => new[] { x.X0, x.X1 })));
        var ys = Coordinates(new[] { 0, size.Y }.Concat(holes.SelectMany(x => x.Arched ? new[] { x.Y0, x.Y1, x.Spring } : new[] { x.Y0, x.Y1 })));
        var nx = xs.Count - 1;
        var ny = ys.Count - 1;
        var state = new int[nx, ny];
        var cellHole = new Hole[nx, ny];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                var cx = (xs[i] + xs[i + 1]) * 0.5f;
                var cy = (ys[j] + ys[j + 1]) * 0.5f;
                foreach (var hole in holes)
                {
                    if (cx > hole.X0 && cx < hole.X1 && cy > hole.Y0 && cy < hole.Y1)
                    {
                        state[i, j] = hole.Arched && cy > hole.Spring ? ArchCell : Open;
                        cellHole[i, j] = hole;
                        break;
                    }
                }
            }
        }
        int State(int i, int j) => i < 0 || j < 0 || i >= nx || j >= ny ? Outside : state[i, j];

        // Front and back
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                if (state[i, j] == Solid)
                {
                    builder.AddFaceZ(zFront, xs[i], xs[i + 1], ys[j], ys[j + 1], -1);
                    builder.AddFaceZ(zBack, xs[i], xs[i + 1], ys[j], ys[j + 1], 1);
                }
                else if (state[i, j] == ArchCell)
                {
                    AddArchCell(builder, cellHole[i, j], xs[i], xs[i + 1], ys[j], ys[j + 1], zFront, zBack);
                }
            }
        }

        // Faces between solid and open cells (or the outside): the jambs, sills and lintels, and the outline of the wall
        for (int i = 0; i <= nx; i++)
        {
            for (int j = 0; j < ny; j++)
            {
                var a = IsSolid(State(i - 1, j));
                var b = IsSolid(State(i, j));
                if (a && !b)
                    builder.AddFaceX(xs[i], ys[j], ys[j + 1], zFront, zBack, 1);
                else if (!a && b)
                    builder.AddFaceX(xs[i], ys[j], ys[j + 1], zFront, zBack, -1);
            }
        }
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                var below = State(i, j - 1);
                var above = State(i, j);
                // Under an arch, the arch itself closes the opening
                if (IsSolid(below) && !IsSolid(above))
                    builder.AddFaceY(ys[j], xs[i], xs[i + 1], zFront, zBack, 1);
                else if (!IsSolid(below) && IsSolid(above) && above != ArchCell)
                    builder.AddFaceY(ys[j], xs[i], xs[i + 1], zFront, zBack, -1);
            }
        }

        // The underside of the arches
        foreach (var hole in holes.Where(x => x.Arched))
        {
            var length = 0.0f;
            for (int k = 0; k < hole.Arc.Length - 1; k++)
            {
                var p = hole.Arc[k];
                var q = hole.Arc[k + 1];
                var np = ArchNormal(hole, p);
                var nq = ArchNormal(hole, q);
                var next = length + Vector2.Distance(p, q);
                var a = builder.AddVertex(new Vector3(p.X, p.Y, zFront), np, new Vector2(length, zFront), BlockoutMapping.Surface);
                var b = builder.AddVertex(new Vector3(q.X, q.Y, zFront), nq, new Vector2(next, zFront), BlockoutMapping.Surface);
                var c = builder.AddVertex(new Vector3(q.X, q.Y, zBack), nq, new Vector2(next, zBack), BlockoutMapping.Surface);
                var d = builder.AddVertex(new Vector3(p.X, p.Y, zBack), np, new Vector2(length, zBack), BlockoutMapping.Surface);
                builder.AddQuad(a, b, c, d);
                length = next;
            }
        }
    }

    private static bool IsSolid(int state) => state is Solid or ArchCell;

    // Inward normal of the arch (toward its center)
    private static Vector3 ArchNormal(Hole hole, Vector2 point)
    {
        var gradient = new Vector2((point.X - hole.CenterX) / (hole.RadiusX * hole.RadiusX), (point.Y - hole.Spring) / (hole.Rise * hole.Rise));
        return gradient.LengthSquared() > 1e-12f ? -new Vector3(Vector2.Normalize(gradient), 0) : -Vector3.UnitY;
    }

    // The part of an arch cell above the arch, on both sides of the wall
    private static void AddArchCell(BlockoutMeshBuilder builder, Hole hole, float xa, float xb, float ya, float yb, float zFront, float zBack)
    {
        var cuts = new List<float> { xa, xb };
        cuts.AddRange(hole.Arc.Select(x => x.X).Where(x => x > xa && x < xb));
        cuts.Sort();
        for (int k = 0; k < cuts.Count - 1; k++)
        {
            var xl = cuts[k];
            var xr = cuts[k + 1];
            if (xr - xl <= Epsilon)
                continue;
            var yl = Math.Clamp(hole.ArcY(xl), ya, yb);
            var yr = Math.Clamp(hole.ArcY(xr), ya, yb);
            builder.AddQuad(new Vector3(xl, yl, zFront), new Vector3(xr, yr, zFront), new Vector3(xr, yb, zFront), new Vector3(xl, yb, zFront), -Vector3.UnitZ);
            builder.AddQuad(new Vector3(xl, yl, zBack), new Vector3(xr, yr, zBack), new Vector3(xr, yb, zBack), new Vector3(xl, yb, zBack), Vector3.UnitZ);
        }
    }

    private List<Hole> GetHoles(Vector3 size)
    {
        var holes = new List<Hole>();
        var segments = Math.Clamp(ArchSegments, 2, 64);
        foreach (var opening in Openings)
        {
            if (opening == null)
                continue;
            var x0 = Math.Max(opening.Offset - opening.Width * 0.5f, -size.X * 0.5f);
            var x1 = Math.Min(opening.Offset + opening.Width * 0.5f, size.X * 0.5f);
            var y0 = Math.Max(opening.Bottom, 0);
            var y1 = Math.Min(opening.Bottom + opening.Height, size.Y);
            if (x1 - x0 < MinSize || y1 - y0 < MinSize)
                continue;
            if (holes.Any(x => x.X0 < x1 && x0 < x.X1 && x.Y0 < y1 && y0 < x.Y1))
                continue;
            var hole = new Hole { X0 = x0, X1 = x1, Y0 = y0, Y1 = y1, Arched = opening.Arched };
            if (hole.Arched)
            {
                hole.CenterX = (x0 + x1) * 0.5f;
                hole.RadiusX = (x1 - x0) * 0.5f;
                hole.Rise = Math.Min(hole.RadiusX, y1 - y0);
                hole.Spring = y1 - hole.Rise;
                hole.Arc = new Vector2[segments + 1];
                for (int k = 0; k <= segments; k++)
                {
                    var angle = MathF.PI * (1 - k / (float)segments);
                    hole.Arc[k] = new Vector2(hole.CenterX + hole.RadiusX * MathF.Cos(angle), hole.Spring + hole.Rise * MathF.Sin(angle));
                }
                hole.Arc[0].X = x0;
                hole.Arc[^1].X = x1;
            }
            holes.Add(hole);
        }
        return holes;
    }

    private static List<float> Coordinates(IEnumerable<float> values)
    {
        var result = new List<float>();
        foreach (var value in values.OrderBy(x => x))
        {
            if (result.Count == 0 || value - result[^1] > Epsilon)
                result.Add(value);
        }
        return result;
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(4);
        hash.Add(ArchSegments);
        foreach (var opening in Openings)
        {
            if (opening == null)
                continue;
            hash.Add(opening.Offset);
            hash.Add(opening.Bottom);
            hash.Add(opening.Width);
            hash.Add(opening.Height);
            hash.Add(opening.Arched);
        }
    }

    protected override object MemberwiseCloneDeep()
    {
        var clone = new BlockoutWall { ArchSegments = ArchSegments };
        foreach (var opening in Openings)
            clone.Openings.Add(opening?.Clone());
        return clone;
    }
}

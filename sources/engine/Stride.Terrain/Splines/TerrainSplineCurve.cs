// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Splines
{
    /// <summary>
    /// A control point of a terrain spline.
    /// </summary>
    public readonly record struct TerrainSplinePoint(Vector3 Position, float Width);

    /// <summary>
    /// A sample of a terrain spline curve (world space).
    /// </summary>
    public readonly record struct TerrainSplineSample(Vector3 Position, Vector3 Tangent, float Width, float Distance);

    /// <summary>
    /// A terrain spline sampled at regular intervals: a centripetal Catmull-Rom curve through the control points.
    /// </summary>
    public sealed class TerrainSplineCurve
    {
        public TerrainSplineCurve(IReadOnlyList<TerrainSplinePoint> points, bool closed, float spacing)
        {
            Points = points;
            Closed = closed && points.Count > 2;
            Build(Math.Max(0.05f, spacing));
        }

        public IReadOnlyList<TerrainSplinePoint> Points { get; }

        public bool Closed { get; }

        public List<TerrainSplineSample> Samples { get; } = new();

        public float Length => Samples.Count > 0 ? Samples[^1].Distance : 0;

        /// <summary>
        /// World bounds of the curve samples, expanded by the half width.
        /// </summary>
        public BoundingBox Bounds { get; private set; }

        /// <summary>
        /// Gets the control points of a spline component: its child entities, in order (world positions; X scale scales the width).
        /// </summary>
        public static List<TerrainSplinePoint> GetControlPoints(TerrainSplineComponent spline)
        {
            var points = new List<TerrainSplinePoint>();
            var transform = spline.Entity?.Transform;
            if (transform == null)
                return points;
            foreach (var child in transform.Children)
            {
                child.UpdateWorldMatrix();
                var world = child.WorldMatrix;
                var widthScale = Math.Abs(child.Scale.X);
                points.Add(new TerrainSplinePoint(world.TranslationVector, spline.Width * (widthScale > 0 ? widthScale : 1)));
            }
            return points;
        }

        public static TerrainSplineCurve FromComponent(TerrainSplineComponent spline)
            => new(GetControlPoints(spline), spline.Closed, spline.SampleSpacing);

        private void Build(float spacing)
        {
            var count = Points.Count;
            if (count == 0)
                return;
            if (count == 1)
            {
                Samples.Add(new TerrainSplineSample(Points[0].Position, Vector3.UnitZ, Points[0].Width, 0));
                UpdateBounds();
                return;
            }

            var segments = Closed ? count : count - 1;
            var distance = 0.0f;
            Vector3? previous = null;
            for (int s = 0; s < segments; s++)
            {
                var p0 = GetPoint(s - 1);
                var p1 = GetPoint(s);
                var p2 = GetPoint(s + 1);
                var p3 = GetPoint(s + 2);
                var chord = Vector3.Distance(p1.Position, p2.Position);
                var steps = Math.Max(1, (int)MathF.Ceiling(chord / spacing));
                // The last segment includes its end point
                var last = s == segments - 1 ? steps : steps - 1;
                for (int i = 0; i <= last; i++)
                {
                    var t = i / (float)steps;
                    var position = CatmullRom(p0.Position, p1.Position, p2.Position, p3.Position, t);
                    var width = MathUtil.Lerp(p1.Width, p2.Width, SmoothStep(t));
                    if (previous.HasValue)
                        distance += Vector3.Distance(previous.Value, position);
                    previous = position;
                    Samples.Add(new TerrainSplineSample(position, Vector3.Zero, width, distance));
                }
            }

            // Tangents from the neighbor samples
            for (int i = 0; i < Samples.Count; i++)
            {
                var before = Samples[Math.Max(0, i - 1)].Position;
                var after = Samples[Math.Min(Samples.Count - 1, i + 1)].Position;
                if (Closed && (i == 0 || i == Samples.Count - 1))
                {
                    before = Samples[i == 0 ? Samples.Count - 2 : i - 1].Position;
                    after = Samples[i == 0 ? 1 : 1].Position;
                }
                var tangent = after - before;
                if (tangent.LengthSquared() < 1e-8f)
                    tangent = Vector3.UnitZ;
                tangent.Normalize();
                var sample = Samples[i];
                Samples[i] = sample with { Tangent = tangent };
            }
            UpdateBounds();
        }

        private void UpdateBounds()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var sample in Samples)
            {
                var half = new Vector3(sample.Width * 0.5f, 0, sample.Width * 0.5f);
                min = Vector3.Min(min, sample.Position - half);
                max = Vector3.Max(max, sample.Position + half);
            }
            Bounds = new BoundingBox(min, max);
        }

        private TerrainSplinePoint GetPoint(int index)
        {
            var count = Points.Count;
            if (Closed)
                return Points[((index % count) + count) % count];
            if (index < 0)
            {
                // Mirror the second point to get a natural end tangent
                var mirrored = Points[0].Position * 2 - Points[1].Position;
                return Points[0] with { Position = mirrored };
            }
            if (index >= count)
            {
                var mirrored = Points[count - 1].Position * 2 - Points[count - 2].Position;
                return Points[count - 1] with { Position = mirrored };
            }
            return Points[index];
        }

        private static float SmoothStep(float t) => t * t * (3 - 2 * t);

        /// <summary>
        /// Centripetal Catmull-Rom (no cusps or self-intersections within a segment).
        /// </summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            static float Knot(float ti, Vector3 a, Vector3 b) => ti + MathF.Max(MathF.Sqrt(Vector3.Distance(a, b)), 1e-4f);

            var t0 = 0.0f;
            var t1 = Knot(t0, p0, p1);
            var t2 = Knot(t1, p1, p2);
            var t3 = Knot(t2, p2, p3);
            var tt = MathUtil.Lerp(t1, t2, t);

            var a1 = (t1 - tt) / (t1 - t0) * p0 + (tt - t0) / (t1 - t0) * p1;
            var a2 = (t2 - tt) / (t2 - t1) * p1 + (tt - t1) / (t2 - t1) * p2;
            var a3 = (t3 - tt) / (t3 - t2) * p2 + (tt - t2) / (t3 - t2) * p3;
            var b1 = (t2 - tt) / (t2 - t0) * a1 + (tt - t0) / (t2 - t0) * a2;
            var b2 = (t3 - tt) / (t3 - t1) * a2 + (tt - t1) / (t3 - t1) * a3;
            return (t2 - tt) / (t2 - t1) * b1 + (tt - t1) / (t2 - t1) * b2;
        }

        /// <summary>
        /// The closest point of the curve to a world position, in the horizontal plane.
        /// </summary>
        public bool FindClosest(Vector3 position, out TerrainSplineSample closest, out float horizontalDistance)
        {
            closest = default;
            horizontalDistance = float.MaxValue;
            if (Samples.Count == 0)
                return false;
            if (Samples.Count == 1)
            {
                closest = Samples[0];
                horizontalDistance = HorizontalDistance(position, closest.Position);
                return true;
            }
            for (int i = 0; i < Samples.Count - 1; i++)
            {
                var a = Samples[i];
                var b = Samples[i + 1];
                var t = ProjectOnSegment(position, a.Position, b.Position);
                var point = Vector3.Lerp(a.Position, b.Position, t);
                var distance = HorizontalDistance(position, point);
                if (distance < horizontalDistance)
                {
                    horizontalDistance = distance;
                    closest = new TerrainSplineSample(point, a.Tangent, MathUtil.Lerp(a.Width, b.Width, t), MathUtil.Lerp(a.Distance, b.Distance, t));
                }
            }
            return true;
        }

        internal static float ProjectOnSegment(Vector3 position, Vector3 a, Vector3 b)
        {
            var ab = new Vector2(b.X - a.X, b.Z - a.Z);
            var lengthSquared = ab.LengthSquared();
            if (lengthSquared < 1e-10f)
                return 0;
            var ap = new Vector2(position.X - a.X, position.Z - a.Z);
            return Math.Clamp(Vector2.Dot(ap, ab) / lengthSquared, 0, 1);
        }

        internal static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
    }
}

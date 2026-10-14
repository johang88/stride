// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Splines
{
    /// <summary>
    /// Applies terrain splines to a terrain: flattens (roads) or carves (rivers) the height field along the curve
    /// with a smooth falloff, and paints layers over the width and the falloff band.
    /// </summary>
    public static class TerrainSplineShaper
    {
        private struct CellInfluence
        {
            public float Distance;
            public float Height;
            public float Width;
        }

        /// <summary>
        /// Applies a spline to a terrain whose first sample is at <paramref name="terrainOrigin"/>.
        /// </summary>
        /// <param name="beforeModify">Called once with the modified sample region before anything changes (undo snapshots).</param>
        /// <returns>True if samples changed.</returns>
        public static bool Apply(TerrainData data, Vector3 terrainOrigin, TerrainSplineComponent spline, TerrainSplineCurve curve, Action<Int2, Int2> beforeModify = null)
        {
            if (data == null || spline == null || curve == null || curve.Samples.Count < 2)
                return false;
            var shape = spline.ShapeTerrain;
            var paint = spline.PaintLayer >= 0 || spline.EdgeLayer >= 0;
            if (!shape && !paint)
                return false;

            var influences = CollectInfluences(data, terrainOrigin, spline, curve, out var min, out var max);
            if (influences.Count == 0)
                return false;

            beforeModify?.Invoke(min, max);
            var resolution = data.Resolution;
            var falloff = Math.Max(spline.Falloff, 1e-3f);
            var river = spline.Kind == TerrainSplineKind.River;
            var paintFalloff = Math.Clamp(spline.Falloff * 0.5f, 0.25f, 2.0f);
            foreach (var (index, cell) in influences)
            {
                var half = cell.Width * 0.5f;
                var d = cell.Distance;

                if (shape)
                {
                    var weight = d <= half ? 1.0f : SmoothStep(1 - (d - half) / falloff);
                    if (weight > 0)
                    {
                        var target = cell.Height + spline.HeightOffset;
                        if (river && d < half)
                        {
                            // Rounded bed, deepest at the center
                            var across = d / Math.Max(half, 1e-3f);
                            target -= spline.Depth * (1 - across * across);
                        }
                        var x = index % resolution;
                        var z = index / resolution;
                        var current = data.GetHeight(x, z);
                        data.Heights[index] = data.Quantize(MathUtil.Lerp(current, target, weight));
                    }
                }

                if (paint)
                {
                    var splat = data.Splat[index];
                    var paintHalf = half * spline.PaintWidthScale;
                    if (spline.PaintLayer >= 0)
                    {
                        var amount = d <= paintHalf ? 1.0f : SmoothStep(1 - (d - paintHalf) / paintFalloff);
                        if (amount > 0)
                            splat = TerrainSplat.Paint(splat, spline.PaintLayer, amount * spline.PaintStrength);
                    }
                    if (spline.EdgeLayer >= 0 && d > paintHalf * 0.8f)
                    {
                        // Over the falloff band, fading out at its outer edge
                        var outer = 1 - Math.Clamp((d - half) / falloff, 0, 1);
                        var inner = SmoothStep(Math.Clamp((d - paintHalf * 0.8f) / Math.Max(paintFalloff, 1e-3f), 0, 1));
                        var amount = Math.Min(inner, SmoothStep(outer));
                        if (amount > 0)
                            splat = TerrainSplat.Paint(splat, spline.EdgeLayer, amount * spline.PaintStrength);
                    }
                    data.Splat[index] = splat;
                }
            }

            var flags = (shape ? TerrainChangeFlags.Heights : 0) | (paint ? TerrainChangeFlags.Splat : 0);
            data.NotifyChanged(min, max, flags);
            return true;
        }

        /// <summary>
        /// For each sample within the influence of the spline: the distance to the closest point of the curve and the curve height and width there.
        /// </summary>
        private static Dictionary<int, CellInfluence> CollectInfluences(TerrainData data, Vector3 origin, TerrainSplineComponent spline, TerrainSplineCurve curve, out Int2 min, out Int2 max)
        {
            var influences = new Dictionary<int, CellInfluence>();
            var cellSize = data.CellSize;
            var resolution = data.Resolution;
            var samples = curve.Samples;
            min = new Int2(int.MaxValue);
            max = new Int2(int.MinValue);

            for (int i = 0; i < samples.Count - 1; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];
                var reach = Math.Max(a.Width, b.Width) * 0.5f * Math.Max(1, spline.PaintWidthScale) + spline.Falloff + cellSize;
                var x0 = Math.Max(0, (int)MathF.Floor((Math.Min(a.Position.X, b.Position.X) - reach - origin.X) / cellSize));
                var z0 = Math.Max(0, (int)MathF.Floor((Math.Min(a.Position.Z, b.Position.Z) - reach - origin.Z) / cellSize));
                var x1 = Math.Min(resolution - 1, (int)MathF.Ceiling((Math.Max(a.Position.X, b.Position.X) + reach - origin.X) / cellSize));
                var z1 = Math.Min(resolution - 1, (int)MathF.Ceiling((Math.Max(a.Position.Z, b.Position.Z) + reach - origin.Z) / cellSize));
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var position = new Vector3(origin.X + x * cellSize, 0, origin.Z + z * cellSize);
                        var t = TerrainSplineCurve.ProjectOnSegment(position, a.Position, b.Position);
                        var point = Vector3.Lerp(a.Position, b.Position, t);
                        var distance = TerrainSplineCurve.HorizontalDistance(position, point);
                        var width = MathUtil.Lerp(a.Width, b.Width, t);
                        if (distance > width * 0.5f * Math.Max(1, spline.PaintWidthScale) + spline.Falloff)
                            continue;
                        var index = z * resolution + x;
                        if (influences.TryGetValue(index, out var existing) && existing.Distance <= distance)
                            continue;
                        influences[index] = new CellInfluence { Distance = distance, Height = point.Y, Width = width };
                        min = new Int2(Math.Min(min.X, x), Math.Min(min.Y, z));
                        max = new Int2(Math.Max(max.X, x), Math.Max(max.Y, z));
                    }
                }
            }
            return influences;
        }

        private static float SmoothStep(float t)
        {
            t = Math.Clamp(t, 0, 1);
            return t * t * (3 - 2 * t);
        }
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing
{
    public enum TerrainBrushTool
    {
        [Display("Raise / Lower")] Raise,
        Smooth,
        Flatten,
        Noise,
        [Display("Paint layer")] Paint,
        [Display("Holes")] Hole,
    }

    /// <summary>
    /// Settings of a terrain brush stroke.
    /// </summary>
    public struct TerrainBrushSettings
    {
        public TerrainBrushTool Tool;

        /// <summary>
        /// Radius in meters.
        /// </summary>
        public float Radius;

        /// <summary>
        /// Strength (0-1).
        /// </summary>
        public float Strength;

        /// <summary>
        /// Fraction of the radius over which the brush fades out (0 = hard edge, 1 = fades from the center).
        /// </summary>
        public float Falloff;

        /// <summary>
        /// Inverts the tool (lower instead of raise, erase layer instead of paint, fill holes instead of cutting).
        /// </summary>
        public bool Invert;

        /// <summary>
        /// Target height (local) of the flatten tool.
        /// </summary>
        public float TargetHeight;

        /// <summary>
        /// Layer painted by the paint tool.
        /// </summary>
        public int Layer;

        /// <summary>
        /// Feature size of the noise tool in meters.
        /// </summary>
        public float NoiseScale;

        public static TerrainBrushSettings Default => new()
        {
            Tool = TerrainBrushTool.Raise,
            Radius = 10.0f,
            Strength = 0.5f,
            Falloff = 0.6f,
            NoiseScale = 8.0f,
        };
    }

    /// <summary>
    /// Applies brush strokes to a <see cref="TerrainData"/>.
    /// </summary>
    public static class TerrainBrush
    {
        // Height change in meters per second at full strength, per meter of brush radius
        private const float RaiseRate = 0.5f;
        // Blend rate (per second) of the smooth/flatten tools at full strength
        private const float BlendRate = 6.0f;
        private const float PaintRate = 4.0f;

        /// <summary>
        /// Gets the samples a brush centered at a local position covers.
        /// </summary>
        public static void GetRegion(TerrainData data, Vector2 center, float radius, out Int2 min, out Int2 max)
        {
            var r = radius / data.CellSize;
            var cx = center.X / data.CellSize;
            var cz = center.Y / data.CellSize;
            min = Int2.Max(new Int2((int)MathF.Floor(cx - r), (int)MathF.Floor(cz - r)), Int2.Zero);
            max = Int2.Min(new Int2((int)MathF.Ceiling(cx + r), (int)MathF.Ceiling(cz + r)), new Int2(data.Resolution - 1));
        }

        /// <summary>
        /// Brush weight (0-1) at a distance from its center.
        /// </summary>
        public static float Weight(float distance, float radius, float falloff)
        {
            if (distance >= radius)
                return 0;
            var inner = radius * (1.0f - Math.Clamp(falloff, 0, 1));
            if (distance <= inner)
                return 1;
            var t = (distance - inner) / Math.Max(radius - inner, 1e-5f);
            // Smoothstep fade
            return 1 - t * t * (3 - 2 * t);
        }

        /// <summary>
        /// Applies a brush for a time step.
        /// </summary>
        /// <param name="data">The terrain.</param>
        /// <param name="center">Brush center in terrain local XZ.</param>
        /// <param name="settings">The brush.</param>
        /// <param name="deltaTime">Time the brush is applied for (seconds).</param>
        /// <param name="beforeModify">Called with the region about to be modified (e.g. to record undo data).</param>
        /// <returns>False if the brush doesn't touch the terrain.</returns>
        public static bool Apply(TerrainData data, Vector2 center, in TerrainBrushSettings settings, float deltaTime, Action<Int2, Int2> beforeModify = null)
        {
            if (settings.Radius <= 0)
                return false;

            GetRegion(data, center, settings.Radius, out var min, out var max);
            if (min.X > max.X || min.Y > max.Y)
                return false;

            beforeModify?.Invoke(min, max);

            var flags = settings.Tool switch
            {
                TerrainBrushTool.Paint or TerrainBrushTool.Hole => TerrainChangeFlags.Splat,
                _ => TerrainChangeFlags.Heights,
            };

            switch (settings.Tool)
            {
                case TerrainBrushTool.Raise:
                    ApplyRaise(data, center, settings, deltaTime, min, max);
                    break;
                case TerrainBrushTool.Smooth:
                    ApplySmooth(data, center, settings, deltaTime, min, max);
                    break;
                case TerrainBrushTool.Flatten:
                    ApplyFlatten(data, center, settings, deltaTime, min, max);
                    break;
                case TerrainBrushTool.Noise:
                    ApplyNoise(data, center, settings, deltaTime, min, max);
                    break;
                case TerrainBrushTool.Paint:
                    ApplyPaint(data, center, settings, deltaTime, min, max, settings.Invert ? -1 : 1, settings.Layer);
                    break;
                case TerrainBrushTool.Hole:
                    ApplyHole(data, center, settings, min, max);
                    break;
            }

            data.NotifyChanged(min, max, flags);
            return true;
        }

        private static float Distance(TerrainData data, Vector2 center, int x, int z)
        {
            var dx = x * data.CellSize - center.X;
            var dz = z * data.CellSize - center.Y;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        private static void ApplyRaise(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
        {
            var amount = RaiseRate * s.Radius * s.Strength * dt * (s.Invert ? -1 : 1);
            for (int z = min.Y; z <= max.Y; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var w = Weight(Distance(data, center, x, z), s.Radius, s.Falloff);
                    if (w > 0)
                        data.SetHeight(x, z, data.GetHeight(x, z) + amount * w);
                }
            }
        }

        private static void ApplyFlatten(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
        {
            var blend = 1 - MathF.Exp(-BlendRate * s.Strength * dt);
            for (int z = min.Y; z <= max.Y; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var w = Weight(Distance(data, center, x, z), s.Radius, s.Falloff) * blend;
                    if (w <= 0)
                        continue;
                    var h = data.GetHeight(x, z);
                    // Inverted flatten only lowers (carve down to the target), otherwise both ways
                    if (s.Invert && h <= s.TargetHeight)
                        continue;
                    data.SetHeight(x, z, MathUtil.Lerp(h, s.TargetHeight, w));
                }
            }
        }

        private static void ApplySmooth(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
        {
            var blend = 1 - MathF.Exp(-BlendRate * s.Strength * dt);
            var width = max.X - min.X + 1;
            var height = max.Y - min.Y + 1;
            var source = new float[width * height];
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                    source[z * width + x] = data.GetHeight(min.X + x, min.Y + z);
            }

            // Kernel radius grows with the brush so large brushes smooth large features
            var kernel = Math.Clamp((int)(s.Radius / data.CellSize * 0.1f), 1, 4);
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    var w = Weight(Distance(data, center, min.X + x, min.Y + z), s.Radius, s.Falloff) * blend;
                    if (w <= 0)
                        continue;
                    float sum = 0;
                    int count = 0;
                    for (int kz = -kernel; kz <= kernel; kz++)
                    {
                        for (int kx = -kernel; kx <= kernel; kx++)
                        {
                            var sx = x + kx;
                            var sz = z + kz;
                            sum += sx >= 0 && sz >= 0 && sx < width && sz < height ? source[sz * width + sx] : data.GetHeight(min.X + sx, min.Y + sz);
                            count++;
                        }
                    }
                    data.SetHeight(min.X + x, min.Y + z, MathUtil.Lerp(source[z * width + x], sum / count, w));
                }
            }
        }

        private static void ApplyNoise(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
        {
            var amount = RaiseRate * s.Radius * s.Strength * dt;
            var frequency = data.CellSize / Math.Max(s.NoiseScale, data.CellSize);
            for (int z = min.Y; z <= max.Y; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var w = Weight(Distance(data, center, x, z), s.Radius, s.Falloff);
                    if (w <= 0)
                        continue;
                    var n = Fbm(x * frequency, z * frequency);
                    data.SetHeight(x, z, data.GetHeight(x, z) + n * amount * w);
                }
            }
        }

        private static void ApplyPaint(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max, float sign, int layer)
        {
            var rate = 1 - MathF.Exp(-PaintRate * s.Strength * dt);
            var resolution = data.Resolution;
            for (int z = min.Y; z <= max.Y; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    var w = Weight(Distance(data, center, x, z), s.Radius, s.Falloff) * rate;
                    if (w <= 1e-4f)
                        continue;
                    ref var splat = ref data.Splat[z * resolution + x];
                    splat = TerrainSplat.Paint(splat, layer, sign * w);
                }
            }
        }

        private static void ApplyHole(TerrainData data, Vector2 center, in TerrainBrushSettings s, Int2 min, Int2 max)
        {
            var resolution = data.Resolution;
            for (int z = min.Y; z <= max.Y; z++)
            {
                for (int x = min.X; x <= max.X; x++)
                {
                    if (Distance(data, center, x, z) > s.Radius)
                        continue;
                    ref var splat = ref data.Splat[z * resolution + x];
                    splat = TerrainSplat.Paint(splat, TerrainSplat.HoleLayer, s.Invert ? -1 : 1);
                }
            }
        }

        // Signed fractal value noise in [-1, 1]
        private static float Fbm(float x, float z)
        {
            float sum = 0, amplitude = 0.5f, frequency = 1;
            for (int i = 0; i < 4; i++)
            {
                sum += amplitude * (ValueNoise(x * frequency, z * frequency) * 2 - 1);
                frequency *= 2.03f;
                amplitude *= 0.5f;
            }
            return sum;
        }

        private static float ValueNoise(float x, float z)
        {
            var xi = (int)MathF.Floor(x);
            var zi = (int)MathF.Floor(z);
            var tx = x - xi;
            var tz = z - zi;
            tx = tx * tx * (3 - 2 * tx);
            tz = tz * tz * (3 - 2 * tz);
            var a = Hash(xi, zi);
            var b = Hash(xi + 1, zi);
            var c = Hash(xi, zi + 1);
            var d = Hash(xi + 1, zi + 1);
            return MathUtil.Lerp(MathUtil.Lerp(a, b, tx), MathUtil.Lerp(c, d, tx), tz);
        }

        private static float Hash(int x, int z)
        {
            var h = (uint)(x * 374761393 + z * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h ^ (h >> 16)) / (float)uint.MaxValue;
        }
    }
}

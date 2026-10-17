// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing;

public enum TerrainBrushTool
{
    [Display("Raise / Lower")] Raise,
    Smooth,
    Flatten,
    Noise,
    [Display("Paint layer")] Paint,
    [Display("Holes")] Hole,
    [Display("Paint grass")] Grass,
    [Display("Place trees")] Vegetation,
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

    /// <summary>
    /// Shape of a custom brush, stretched over the brush diameter (null: round brush fading out by <see cref="Falloff"/>).
    /// </summary>
    public TerrainBrushMask Mask;

    /// <summary>
    /// Rotation of the custom brush mask in radians (counter-clockwise seen from above).
    /// </summary>
    public float Rotation;

    /// <summary>
    /// Grass layer painted by the grass tool.
    /// </summary>
    public int GrassLayer;

    /// <summary>
    /// Density (0-1) the grass tool paints towards.
    /// </summary>
    public float GrassDensity;

    /// <summary>
    /// Vegetation type placed by the vegetation tool (when <see cref="VegetationTypes"/> is empty).
    /// </summary>
    public int VegetationType;

    /// <summary>
    /// Vegetation types the vegetation tool places as a mix, one picked at random per instance (null or empty: only
    /// <see cref="VegetationType"/>). The density counts them together, and erasing removes all of them.
    /// </summary>
    public int[] VegetationTypes;

    /// <summary>
    /// Number of instances per 100 m² the vegetation tool fills the brush up to (where the brush is fully on).
    /// </summary>
    public float VegetationDensity;

    /// <summary>
    /// Smallest distance between two placed instances (meters, any type).
    /// </summary>
    public float VegetationSpacing;

    /// <summary>
    /// Scale range of placed instances.
    /// </summary>
    public float VegetationMinScale;

    public float VegetationMaxScale;

    /// <summary>
    /// Steepest slope (degrees) instances are placed on.
    /// </summary>
    public float VegetationMaxSlope;

    /// <summary>
    /// Distance from the center to the farthest point the brush can touch: the corners of a rotated mask reach beyond the radius.
    /// </summary>
    public readonly float Reach => Mask != null ? Radius * MathF.Sqrt(2) : Radius;

    /// <summary>
    /// Whether the vegetation tool places (and erases) a vegetation type.
    /// </summary>
    public readonly bool PlacesVegetationType(int type) => VegetationTypes is { Length: > 0 } types ? Array.IndexOf(types, type) >= 0 : type == VegetationType;

    public static TerrainBrushSettings Default => new()
    {
        Tool = TerrainBrushTool.Raise,
        Radius = 10.0f,
        Strength = 0.5f,
        Falloff = 0.6f,
        NoiseScale = 8.0f,
        GrassDensity = 1.0f,
        VegetationDensity = 2.0f,
        VegetationSpacing = 3.0f,
        VegetationMinScale = 0.8f,
        VegetationMaxScale = 1.2f,
        VegetationMaxSlope = 35.0f,
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
    /// Brush weight (0-1) of the sample (x, z) of a terrain for a brush centered at a local position.
    /// </summary>
    public static float Weight(TerrainData data, Vector2 center, int x, int z, in TerrainBrushSettings settings)
    {
        var offset = new Vector2(x * data.CellSize - center.X, z * data.CellSize - center.Y);
        if (settings.Mask != null)
            return settings.Mask.Weight(offset, settings.Radius, settings.Rotation);
        return Weight(offset.Length(), settings.Radius, settings.Falloff);
    }

    /// <summary>
    /// Brush weight (0-1) of a round brush at a distance from its center.
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
    /// <param name="beforeVegetationModify">Called before the vegetation instances are modified (e.g. to record undo data).</param>
    /// <returns>False if the brush doesn't touch the terrain.</returns>
    public static bool Apply(TerrainData data, Vector2 center, in TerrainBrushSettings settings, float deltaTime, Action<Int2, Int2> beforeModify = null, Action beforeVegetationModify = null)
    {
        if (settings.Radius <= 0)
            return false;

        GetRegion(data, center, settings.Reach, out var min, out var max);
        if (min.X > max.X || min.Y > max.Y)
            return false;

        if (settings.Tool == TerrainBrushTool.Vegetation)
        {
            if (ApplyVegetation(data, center, settings, deltaTime, beforeVegetationModify))
                data.NotifyChanged(Int2.Zero, new Int2(data.Resolution - 1), TerrainChangeFlags.Vegetation);
            return true;
        }

        if (settings.Tool == TerrainBrushTool.Grass)
        {
            if ((uint)settings.GrassLayer >= TerrainData.MaxGrassLayers)
                return false;
            // Whole grass samples (one every two terrain samples), in terrain samples for the undo tiles
            var grassMin = new Int2(min.X / 2, min.Y / 2);
            var grassMax = Int2.Min(new Int2((max.X + 1) / 2, (max.Y + 1) / 2), new Int2(data.GrassResolution - 1));
            beforeModify?.Invoke(new Int2(grassMin.X * 2, grassMin.Y * 2), new Int2(grassMax.X * 2, grassMax.Y * 2));
            ApplyGrass(data, center, settings, deltaTime, grassMin, grassMax);
            data.NotifyChanged(new Int2(grassMin.X * 2, grassMin.Y * 2), new Int2(grassMax.X * 2, grassMax.Y * 2), TerrainChangeFlags.Grass);
            return true;
        }

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

    private static void ApplyRaise(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
    {
        var amount = RaiseRate * s.Radius * s.Strength * dt * (s.Invert ? -1 : 1);
        for (int z = min.Y; z <= max.Y; z++)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                var w = Weight(data, center, x, z, s);
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
                var w = Weight(data, center, x, z, s) * blend;
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
                var w = Weight(data, center, min.X + x, min.Y + z, s) * blend;
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
                var w = Weight(data, center, x, z, s);
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
                var w = Weight(data, center, x, z, s) * rate;
                if (w <= 1e-4f)
                    continue;
                ref var splat = ref data.Splat[z * resolution + x];
                splat = TerrainSplat.Paint(splat, layer, sign * w);
            }
        }
    }

    private static void ApplyGrass(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Int2 min, Int2 max)
    {
        var rate = 1 - MathF.Exp(-PaintRate * s.Strength * dt);
        var target = s.Invert ? 0 : Math.Clamp(s.GrassDensity, 0, 1) * 255;
        var density = data.GetOrCreateGrassDensity(s.GrassLayer);
        var size = data.GrassResolution;
        for (int z = min.Y; z <= max.Y; z++)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                // Grass sample (x, z) is terrain sample (2x, 2z)
                var w = Weight(data, center, x * 2, z * 2, s) * rate;
                if (w <= 1e-4f)
                    continue;
                ref var value = ref density[z * size + x];
                var blended = value + (target - value) * w;
                // Always move by at least one step, or slow strokes would stall short of the target
                if (MathF.Abs(blended - value) < 1 && value != target)
                    blended = value + MathF.Sign(target - value);
                value = (byte)Math.Clamp(MathF.Round(blended), 0, 255);
            }
        }
    }

    /// <summary>
    /// Adds instances of the vegetation types until the brush holds their density (or removes them, inverted).
    /// </summary>
    private static bool ApplyVegetation(TerrainData data, Vector2 center, in TerrainBrushSettings s, float dt, Action beforeModify)
    {
        var reach = s.Reach;
        var instances = data.Vegetation;

        if (s.Invert)
        {
            // Erase the type where the brush is at least half on
            var removed = false;
            for (int i = instances.Count - 1; i >= 0; i--)
            {
                var instance = instances[i];
                if (!s.PlacesVegetationType(instance.Type) || Vector2.DistanceSquared(instance.Position, center) > reach * reach)
                    continue;
                if (BrushWeight(instance.Position - center, s) < 0.5f)
                    continue;
                if (!removed)
                    beforeModify?.Invoke();
                removed = true;
                instances.RemoveAt(i);
            }
            return removed;
        }

        // The instances near the brush, to keep the spacing and count the type's density
        var spacing = Math.Max(s.VegetationSpacing, 0.1f);
        var nearby = new List<Vector2>();
        var existing = 0f;
        foreach (var instance in instances)
        {
            var offset = instance.Position - center;
            if (offset.LengthSquared() > (reach + spacing) * (reach + spacing))
                continue;
            nearby.Add(instance.Position);
            if (s.PlacesVegetationType(instance.Type))
                existing += BrushWeight(offset, s);
        }

        // How many the brush should hold: its area weighted by the brush shape (sampled), at the density
        const int areaSamples = 16;
        var coverage = 0f;
        for (int z = 0; z < areaSamples; z++)
        {
            for (int x = 0; x < areaSamples; x++)
            {
                var offset = new Vector2((x + 0.5f) / areaSamples * 2 - 1, (z + 0.5f) / areaSamples * 2 - 1) * reach;
                coverage += BrushWeight(offset, s);
            }
        }
        var area = coverage / (areaSamples * areaSamples) * (2 * reach) * (2 * reach);
        var wanted = area * Math.Max(s.VegetationDensity, 0) / 100.0f;
        var missing = wanted - existing;
        if (missing <= 0)
            return false;

        // Fill part of what's missing per application (strength sets how fast), at least one try
        var tries = Math.Max(1, (int)MathF.Ceiling(missing * Math.Clamp(s.Strength * dt * 8, 0.05f, 1)));
        var maxSlope = MathF.Cos(MathUtil.DegreesToRadians(Math.Clamp(s.VegetationMaxSlope, 0, 90)));
        var random = Random.Shared;
        var added = false;
        for (int attempt = 0; attempt < tries * 4 && tries > 0; attempt++)
        {
            var candidate = center + new Vector2(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1) * reach;
            if (!data.Contains(candidate.X, candidate.Y))
                continue;
            // The brush shape sets the chance to place
            if (random.NextSingle() >= BrushWeight(candidate - center, s))
                continue;
            if (data.SampleNormal(candidate.X, candidate.Y).Y < maxSlope)
                continue;
            if (data.Splat.Length > 0 && IsHole(data, candidate))
                continue;
            var tooClose = false;
            foreach (var position in nearby)
            {
                if (Vector2.DistanceSquared(position, candidate) < spacing * spacing)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
                continue;

            if (!added)
                beforeModify?.Invoke();
            added = true;
            instances.Add(new TerrainVegetationInstance
            {
                Type = s.VegetationTypes is { Length: > 0 } types ? types[random.Next(types.Length)] : s.VegetationType,
                Position = candidate,
                Rotation = random.NextSingle() * MathF.Tau,
                Scale = MathUtil.Lerp(s.VegetationMinScale, Math.Max(s.VegetationMinScale, s.VegetationMaxScale), random.NextSingle()),
            });
            nearby.Add(candidate);
            tries--;
        }
        return added;
    }

    private static bool IsHole(TerrainData data, Vector2 position)
    {
        var x = Math.Clamp((int)MathF.Round(position.X / data.CellSize), 0, data.Resolution - 1);
        var z = Math.Clamp((int)MathF.Round(position.Y / data.CellSize), 0, data.Resolution - 1);
        return TerrainSplat.GetDominantLayer(data.Splat[z * data.Resolution + x]) == TerrainSplat.HoleLayer;
    }

    // Brush weight at an offset from its center (meters)
    private static float BrushWeight(Vector2 offset, in TerrainBrushSettings s)
    {
        return s.Mask != null ? s.Mask.Weight(offset, s.Radius, s.Rotation) : Weight(offset.Length(), s.Radius, s.Falloff);
    }

    private static void ApplyHole(TerrainData data, Vector2 center, in TerrainBrushSettings s, Int2 min, Int2 max)
    {
        var resolution = data.Resolution;
        for (int z = min.Y; z <= max.Y; z++)
        {
            for (int x = min.X; x <= max.X; x++)
            {
                // Holes are on or off: the brush cuts where it's at least half on
                if (Weight(data, center, x, z, s with { Falloff = 0 }) < 0.5f)
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

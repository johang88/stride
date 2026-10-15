// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.CompilerServices;

namespace Stride.Terrain;

/// <summary>
/// Packs the material layer weights of a terrain sample into 32 bits: the three strongest layers and their weights.
/// </summary>
/// <remarks>
/// Layout: bits 0-5 layer A, 6-11 layer B, 12-17 layer C, 18-24 weight A (0-127), 25-31 weight B (0-127).
/// Weight C is the remainder (127 - A - B). Layer <see cref="HoleLayer"/> is reserved to cut holes in the terrain.
/// The shader (TerrainSplatSampling.sdsl) decodes the same layout.
/// </remarks>
public static class TerrainSplat
{
    public const int MaxLayers = 64;

    /// <summary>
    /// Number of layers usable for materials (the last index is reserved for holes).
    /// </summary>
    public const int MaxMaterialLayers = MaxLayers - 1;

    public const int HoleLayer = MaxLayers - 1;

    public const int WeightMax = 127;

    public const int SlotCount = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Encode(int layer) => Encode(layer, WeightMax, layer, 0, layer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Encode(int layerA, int weightA, int layerB, int weightB, int layerC)
    {
        return (uint)(layerA & 63)
            | ((uint)(layerB & 63) << 6)
            | ((uint)(layerC & 63) << 12)
            | ((uint)(weightA & 127) << 18)
            | ((uint)(weightB & 127) << 25);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Decode(uint value, out int layerA, out int weightA, out int layerB, out int weightB, out int layerC, out int weightC)
    {
        layerA = (int)(value & 63);
        layerB = (int)((value >> 6) & 63);
        layerC = (int)((value >> 12) & 63);
        weightA = (int)((value >> 18) & 127);
        weightB = (int)((value >> 25) & 127);
        weightC = Math.Max(0, WeightMax - weightA - weightB);
    }

    /// <summary>
    /// Gets the weight (0-1) of a layer in a packed sample.
    /// </summary>
    public static float GetWeight(uint value, int layer)
    {
        Decode(value, out var la, out var wa, out var lb, out var wb, out var lc, out var wc);
        var w = 0;
        if (la == layer) w += wa;
        if (lb == layer) w += wb;
        if (lc == layer) w += wc;
        return w / (float)WeightMax;
    }

    /// <summary>
    /// Gets the layer with the highest weight in a packed sample.
    /// </summary>
    public static int GetDominantLayer(uint value)
    {
        Span<float> weights = stackalloc float[MaxLayers];
        Accumulate(value, weights, 1.0f);
        var best = 0;
        for (int i = 1; i < MaxLayers; i++)
        {
            if (weights[i] > weights[best])
                best = i;
        }
        return best;
    }

    /// <summary>
    /// Adds the weights of a packed sample (scaled by <paramref name="scale"/>) into a per-layer weight array.
    /// </summary>
    public static void Accumulate(uint value, Span<float> weights, float scale)
    {
        Decode(value, out var la, out var wa, out var lb, out var wb, out var lc, out var wc);
        var s = scale / WeightMax;
        weights[la] += wa * s;
        weights[lb] += wb * s;
        weights[lc] += wc * s;
    }

    /// <summary>
    /// Packs per-layer weights (any scale) keeping the three strongest layers.
    /// </summary>
    public static uint Encode(ReadOnlySpan<float> weights)
    {
        int l0 = -1, l1 = -1, l2 = -1;
        float w0 = 0, w1 = 0, w2 = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            var w = weights[i];
            if (w <= 0)
                continue;
            if (w > w0)
            {
                l2 = l1; w2 = w1;
                l1 = l0; w1 = w0;
                l0 = i; w0 = w;
            }
            else if (w > w1)
            {
                l2 = l1; w2 = w1;
                l1 = i; w1 = w;
            }
            else if (w > w2)
            {
                l2 = i; w2 = w;
            }
        }

        if (l0 < 0)
            return Encode(0);
        if (l1 < 0) { l1 = l0; }
        if (l2 < 0) { l2 = l1; }

        var total = w0 + w1 + w2;
        var qa = (int)MathF.Round(w0 / total * WeightMax);
        var qb = (int)MathF.Round(w1 / total * WeightMax);
        if (qa + qb > WeightMax)
            qb = WeightMax - qa;
        return Encode(l0, qa, l1, qb, l2);
    }

    /// <summary>
    /// Blends a layer into a packed sample: the layer weight moves towards 1 by <paramref name="amount"/> (0-1), other layers are scaled down.
    /// A negative amount removes the layer (its weight is redistributed to the remaining layers).
    /// </summary>
    public static uint Paint(uint value, int layer, float amount)
    {
        Span<float> weights = stackalloc float[MaxLayers];
        Accumulate(value, weights, 1.0f);

        if (amount >= 0)
        {
            amount = Math.Min(amount, 1.0f);
            for (int i = 0; i < MaxLayers; i++)
                weights[i] *= 1.0f - amount;
            weights[layer] += amount;
        }
        else
        {
            var current = weights[layer];
            var removed = Math.Min(current, -amount);
            weights[layer] = current - removed;
            var remaining = 1.0f - current;
            if (remaining <= 1e-4f)
            {
                // Nothing to redistribute to: fall back to the first layer
                if (layer != 0)
                    weights[0] += removed;
                else
                    weights[layer] = current;
            }
            else
            {
                var scale = (remaining + removed) / remaining;
                for (int i = 0; i < MaxLayers; i++)
                {
                    if (i != layer)
                        weights[i] *= scale;
                }
            }
        }

        return Encode(weights);
    }
}

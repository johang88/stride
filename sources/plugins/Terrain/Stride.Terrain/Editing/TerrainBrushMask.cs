// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Editing;

/// <summary>
/// The shape of a custom terrain brush: a square grayscale mask (0-1) stretched over the brush diameter.
/// Row 0 is the far edge (-Z) of the brush, column 0 its left edge (-X), before rotation.
/// </summary>
public sealed class TerrainBrushMask
{
    /// <summary>
    /// Resolution masks are resampled to.
    /// </summary>
    public const int DefaultSize = 128;

    public TerrainBrushMask(int size, float[] values)
    {
        if (size < 2)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (values == null || values.Length != size * size)
            throw new ArgumentException("A mask needs size * size values.", nameof(values));
        Size = size;
        Values = values;
    }

    public int Size { get; }

    /// <summary>
    /// Mask values (0-1), row-major.
    /// </summary>
    public float[] Values { get; }

    /// <summary>
    /// Bilinear sample at normalized coordinates (0-1 across the mask), 0 outside.
    /// </summary>
    public float Sample(float u, float v)
    {
        if (u < 0 || v < 0 || u > 1 || v > 1)
            return 0;
        var x = u * (Size - 1);
        var y = v * (Size - 1);
        var x0 = Math.Min((int)x, Size - 2);
        var y0 = Math.Min((int)y, Size - 2);
        var tx = x - x0;
        var ty = y - y0;
        var row0 = y0 * Size + x0;
        var row1 = row0 + Size;
        var a = Values[row0] + (Values[row0 + 1] - Values[row0]) * tx;
        var b = Values[row1] + (Values[row1 + 1] - Values[row1]) * tx;
        return a + (b - a) * ty;
    }

    /// <summary>
    /// Brush weight at an offset from the brush center (terrain local XZ, meters), for a brush radius and rotation (radians).
    /// </summary>
    public float Weight(Vector2 offset, float radius, float rotation)
    {
        // Into the mask frame: undo the brush rotation (counter-clockwise seen from above)
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);
        var x = offset.X * cos + offset.Y * sin;
        var z = -offset.X * sin + offset.Y * cos;
        return Sample(x / radius * 0.5f + 0.5f, z / radius * 0.5f + 0.5f);
    }

    /// <summary>
    /// Creates a mask by evaluating a function of the position (-1 to 1 across the mask, X right, Y towards +Z).
    /// </summary>
    public static TerrainBrushMask Create(Func<float, float, float> shape, int size = DefaultSize)
    {
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var px = x / (float)(size - 1) * 2 - 1;
                var py = y / (float)(size - 1) * 2 - 1;
                values[y * size + x] = Math.Clamp(shape(px, py), 0, 1);
            }
        }
        return new TerrainBrushMask(size, values);
    }

    /// <summary>
    /// Resamples grayscale pixels (any square or rectangular size) to a mask of <see cref="DefaultSize"/>, stretching to a square.
    /// </summary>
    public static TerrainBrushMask FromPixels(float[] pixels, int width, int height, int size = DefaultSize)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("The pixel count doesn't match the size.", nameof(pixels));
        var values = new float[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                // Box filter over the source pixels covered by the target pixel (downscaling), bilinear when upscaling
                var sx0 = x * width / (float)size;
                var sx1 = (x + 1) * width / (float)size;
                var sy0 = y * height / (float)size;
                var sy1 = (y + 1) * height / (float)size;
                float sum = 0;
                int count = 0;
                for (int sy = (int)sy0; sy < Math.Max((int)MathF.Ceiling(sy1), (int)sy0 + 1) && sy < height; sy++)
                {
                    for (int sx = (int)sx0; sx < Math.Max((int)MathF.Ceiling(sx1), (int)sx0 + 1) && sx < width; sx++)
                    {
                        sum += pixels[sy * width + sx];
                        count++;
                    }
                }
                values[y * size + x] = count > 0 ? Math.Clamp(sum / count, 0, 1) : 0;
            }
        }
        return new TerrainBrushMask(size, values);
    }
}

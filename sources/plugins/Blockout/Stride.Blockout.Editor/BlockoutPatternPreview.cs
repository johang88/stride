// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Blockout.Rendering;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Editor;

/// <summary>
/// Renders the pattern of a look on the CPU, as the ComputeColorBlockoutPattern shader does, for the swatches of the panel.
/// </summary>
public static class BlockoutPatternPreview
{
    /// <summary>
    /// The pattern on a square of <paramref name="metersAcross"/> meters, as BGRA bytes (sRGB, opaque).
    /// </summary>
    public static byte[] Render(BlockoutLook look, int size, float metersAcross = 2.0f)
    {
        var pixels = new byte[size * size * 4];
        var color = look.LinearColor;
        var accentColor = look.LinearAccent;
        var cellSize = Math.Max(look.Scale, 0.05f);
        var pixelsPerCell = size / metersAcross * cellSize;
        // Lines stay at least a pixel wide
        var minimumWidth = 1.2f / Math.Max(pixelsPerCell, 1e-3f);
        const int samples = 3;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var sum = Vector3.Zero;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        var uv = new Vector2((x + (sx + 0.5f) / samples) / size, 1 - (y + (sy + 0.5f) / samples) / size) * metersAcross / cellSize;
                        var (accent, tone) = Evaluate(look.Pattern, uv, minimumWidth);
                        var linear = Vector3.Lerp((Vector3)color, (Vector3)accentColor, accent) * (1 + tone * look.ToneVariation);
                        sum += linear;
                    }
                }
                var srgb = new Color3(sum / (samples * samples)).ToSRgb();
                var index = (y * size + x) * 4;
                pixels[index] = ToByte(srgb.B);
                pixels[index + 1] = ToByte(srgb.G);
                pixels[index + 2] = ToByte(srgb.R);
                pixels[index + 3] = 255;
            }
        }
        return pixels;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);

    private static (float Accent, float Tone) Evaluate(BlockoutPattern pattern, Vector2 uv, float minimumWidth)
    {
        switch (pattern)
        {
            case BlockoutPattern.Grid:
                return (Math.Max(Lines(uv, new Vector2(0.035f), minimumWidth), 0.4f * Lines(uv * 4, new Vector2(0.06f), minimumWidth * 4)), 0);
            case BlockoutPattern.Checker:
                return (((int)MathF.Floor(uv.X) + (int)MathF.Floor(uv.Y) & 1) != 0 ? 1 : 0, 0);
            case BlockoutPattern.Stripes:
                return (Fract(uv.X + uv.Y) >= 0.5f ? 1 : 0, 0);
            case BlockoutPattern.Bricks:
            {
                var row = MathF.Floor(uv.Y * 2);
                var brick = new Vector2(uv.X + 0.5f * (Math.Abs(row) % 2), uv.Y * 2);
                return (Lines(brick, new Vector2(0.05f, 0.1f), minimumWidth), Hash(new Vector2(MathF.Floor(brick.X), row)) - 0.5f);
            }
            case BlockoutPattern.Tiles:
                return (Lines(uv, new Vector2(0.04f), minimumWidth), Hash(new Vector2(MathF.Floor(uv.X), MathF.Floor(uv.Y))) - 0.5f);
            case BlockoutPattern.Planks:
            {
                var board = MathF.Floor(uv.Y * 4);
                var along = uv.X * 0.5f + Hash(new Vector2(board, 7.0f));
                return (Lines(new Vector2(along, uv.Y * 4), new Vector2(0.02f, 0.08f), minimumWidth * 4), Hash(new Vector2(MathF.Floor(along), board)) - 0.5f);
            }
            default:
                return (0, 0);
        }
    }

    private static float Lines(Vector2 uv, Vector2 width, float minimumWidth)
    {
        var dx = MathF.Abs(Fract(uv.X + 0.5f) - 0.5f);
        var dy = MathF.Abs(Fract(uv.Y + 0.5f) - 0.5f);
        return dx < Math.Max(width.X, minimumWidth) * 0.5f || dy < Math.Max(width.Y, minimumWidth) * 0.5f ? 1 : 0;
    }

    private static float Fract(float value) => value - MathF.Floor(value);

    // The hash of the shader
    private static float Hash(Vector2 p)
    {
        p = new Vector2(Fract(p.X * 0.1031f), Fract(p.Y * 0.1030f));
        var d = Vector2.Dot(p, new Vector2(p.Y, p.X) + new Vector2(33.33f));
        p += new Vector2(d);
        return Fract((p.X + p.Y) * p.X);
    }
}

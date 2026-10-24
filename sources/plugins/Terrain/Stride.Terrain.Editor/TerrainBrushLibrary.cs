// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Graphics;
using Stride.Terrain.Editing;

namespace Stride.Terrain.Editor;

/// <summary>
/// A brush of the terrain tools: the round brush (no mask, faded by the falloff) or a custom shape.
/// </summary>
public sealed class TerrainBrushItemViewModel
{
    public TerrainBrushItemViewModel(string name, TerrainBrushMask mask, string filePath = null)
    {
        Name = name;
        Mask = mask;
        FilePath = filePath;
    }

    public string Name { get; }

    /// <summary>
    /// The brush shape, null for the round brush.
    /// </summary>
    public TerrainBrushMask Mask { get; }

    /// <summary>
    /// The image the brush was loaded from (project brushes), null for the built-in brushes.
    /// </summary>
    public string FilePath { get; }

    public bool IsCustom => Mask != null;

    public override string ToString() => Name;
}

/// <summary>
/// The brushes of the terrain tools: built-in shapes, and grayscale images in the <c>TerrainBrushes</c> folder of a package's
/// resources (images with transparency use their alpha). Brush images are stretched over the brush diameter.
/// </summary>
public static class TerrainBrushLibrary
{
    public const string FolderName = "TerrainBrushes";

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".tif", ".tiff", ".dds"];

    private static List<TerrainBrushItemViewModel> builtIn;

    /// <summary>
    /// The built-in brushes, the round brush first.
    /// </summary>
    public static IReadOnlyList<TerrainBrushItemViewModel> BuiltIn => builtIn ??= CreateBuiltIn();

    /// <summary>
    /// The folder holding the brush images of a package: <c>TerrainBrushes</c> in its first resource folder.
    /// </summary>
    public static string GetFolder(Package package)
    {
        var root = package?.RootDirectory;
        if (root == null)
            return null;
        var resources = package.ResourceFolders.FirstOrDefault() ?? new UDirectory("Resources");
        var directory = resources.IsAbsolute ? resources : UPath.Combine(root, resources);
        return Path.Combine(directory.ToOSPath(), FolderName);
    }

    /// <summary>
    /// Loads the brush images of a folder (sorted by name); images that fail to load are reported and skipped.
    /// </summary>
    public static List<TerrainBrushItemViewModel> LoadFolder(string folder, Action<string, Exception> onError = null)
    {
        var result = new List<TerrainBrushItemViewModel>();
        if (folder == null || !Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder).Where(IsImage).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(new TerrainBrushItemViewModel(Path.GetFileNameWithoutExtension(file), LoadMask(file), file));
            }
            catch (Exception e)
            {
                onError?.Invoke(file, e);
            }
        }
        return result;
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a brush image as a mask: its alpha when it has transparency, its brightness otherwise.
    /// </summary>
    public static TerrainBrushMask LoadMask(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = Image.Load(stream) ?? throw new InvalidDataException("Unsupported image.");
        var description = image.Description;
        var pixelBuffer = image.PixelBuffer[0];
        var width = description.Width;
        var height = description.Height;
        var count = width * height;
        var luminance = new float[count];
        var alpha = new float[count];
        Array.Fill(alpha, 1.0f);
        switch (description.Format)
        {
            case PixelFormat.R8_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<byte>();
                for (int i = 0; i < count; i++)
                    luminance[i] = pixels[i] / 255.0f;
                break;
            }
            case PixelFormat.R16_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<ushort>();
                for (int i = 0; i < count; i++)
                    luminance[i] = pixels[i] / 65535.0f;
                break;
            }
            case PixelFormat.R32_Float:
                Array.Copy(pixelBuffer.GetPixels<float>(), luminance, count);
                break;
            case PixelFormat.R16G16B16A16_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<ushort>();
                for (int i = 0; i < count; i++)
                {
                    luminance[i] = Luminance(pixels[i * 4] / 65535.0f, pixels[i * 4 + 1] / 65535.0f, pixels[i * 4 + 2] / 65535.0f);
                    alpha[i] = pixels[i * 4 + 3] / 65535.0f;
                }
                break;
            }
            case PixelFormat.R8G8B8A8_UNorm:
            case PixelFormat.R8G8B8A8_UNorm_SRgb:
            case PixelFormat.B8G8R8A8_UNorm:
            case PixelFormat.B8G8R8A8_UNorm_SRgb:
            {
                // Luminance doesn't depend on the channel order enough to matter for a mask
                var pixels = pixelBuffer.GetPixels<byte>();
                for (int i = 0; i < count; i++)
                {
                    luminance[i] = Luminance(pixels[i * 4] / 255.0f, pixels[i * 4 + 1] / 255.0f, pixels[i * 4 + 2] / 255.0f);
                    alpha[i] = pixels[i * 4 + 3] / 255.0f;
                }
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported image format {description.Format} (use a grayscale or RGBA image).");
        }

        var hasTransparency = alpha.Any(x => x < 0.98f);
        return TerrainBrushMask.FromPixels(hasTransparency ? alpha : luminance, width, height);
    }

    private static float Luminance(float r, float g, float b) => r * 0.2126f + g * 0.7152f + b * 0.0722f;

    private static List<TerrainBrushItemViewModel> CreateBuiltIn()
    {
        return
        [
            new TerrainBrushItemViewModel("Round", null),
            new TerrainBrushItemViewModel("Square", TerrainBrushMask.Create((x, y) => 1 - SmoothStep(0.7f, 1.0f, MathF.Max(MathF.Abs(x), MathF.Abs(y))))),
            new TerrainBrushItemViewModel("Rock", TerrainBrushMask.Create((x, y) =>
            {
                // A blob with an irregular, rocky edge
                var r = MathF.Sqrt(x * x + y * y);
                var n = Fbm(x * 2.5f + 3.1f, y * 2.5f + 7.3f);
                return SmoothStep(1.0f, 0.45f, r + (n - 0.5f) * 0.7f);
            })),
            new TerrainBrushItemViewModel("Ridge", TerrainBrushMask.Create((x, y) =>
                (1 - SmoothStep(0.08f, 0.4f, MathF.Abs(y))) * (1 - SmoothStep(0.6f, 1.0f, MathF.Abs(x))))),
            new TerrainBrushItemViewModel("Ring", TerrainBrushMask.Create((x, y) =>
            {
                var r = MathF.Sqrt(x * x + y * y);
                var d = (r - 0.62f) / 0.2f;
                return MathF.Exp(-d * d * 2.5f);
            })),
            new TerrainBrushItemViewModel("Scatter", TerrainBrushMask.Create((x, y) =>
            {
                // Patches of different sizes, for painting variation
                var n = Fbm(x * 4.0f + 11.7f, y * 4.0f + 2.9f);
                var r = MathF.Sqrt(x * x + y * y);
                return SmoothStep(0.55f, 0.7f, n) * (1 - SmoothStep(0.75f, 1.0f, r));
            })),
        ];
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    // Fractal value noise (0-1)
    private static float Fbm(float x, float y)
    {
        float sum = 0, amplitude = 0.5f, total = 0;
        for (int i = 0; i < 4; i++)
        {
            sum += amplitude * ValueNoise(x, y);
            total += amplitude;
            x = x * 2.03f + 1.7f;
            y = y * 2.03f + 9.2f;
            amplitude *= 0.5f;
        }
        return sum / total;
    }

    private static float ValueNoise(float x, float y)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var tx = x - xi;
        var ty = y - yi;
        tx = tx * tx * (3 - 2 * tx);
        ty = ty * ty * (3 - 2 * ty);
        var a = Hash(xi, yi);
        var b = Hash(xi + 1, yi);
        var c = Hash(xi, yi + 1);
        var d = Hash(xi + 1, yi + 1);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    private static float Hash(int x, int y)
    {
        var h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}

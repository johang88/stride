// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stride.Core.Presentation.Themes;
using Stride.Core.Presentation.ValueConverters;
using Stride.Terrain.Editing;

namespace Stride.Terrain.Editor.Views;

/// <summary>
/// The icon of a terrain tool (a <see cref="TerrainBrushTool"/> value): the image resource <c>TerrainIcon.{value}</c>, themed
/// like the editor's own icons.
/// </summary>
public class TerrainIcon : OneWayValueConverter<TerrainIcon>
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Get($"TerrainIcon.{value}") : null;
    }

    /// <summary>
    /// A themed copy of an icon of the application resources, or null.
    /// </summary>
    public static ImageSource Get(string key)
    {
        var source = Application.Current?.TryFindResource(key) as ImageSource;
        if (source is DrawingImage drawingImage)
            return new DrawingImage(ImageThemingUtilities.TransformDrawing(drawingImage.Drawing, ThemeController.CurrentTheme.GetThemeBase().GetIconTheme()));
        return source;
    }
}

/// <summary>
/// The preview of a terrain brush (a <see cref="TerrainBrushItemViewModel"/>): its mask, or a soft disc for the round brush.
/// </summary>
public class TerrainBrushPreview : OneWayValueConverter<TerrainBrushPreview>
{
    private static readonly ConditionalWeakTable<TerrainBrushMask, BitmapSource> Previews = new();
    private static readonly TerrainBrushMask RoundPreview = TerrainBrushMask.Create((x, y) =>
    {
        var t = Math.Clamp((MathF.Sqrt(x * x + y * y) - 0.4f) / 0.6f, 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }, 64);

    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TerrainBrushItemViewModel brush)
            return null;
        return Previews.GetValue(brush.Mask ?? RoundPreview, CreatePreview);
    }

    private static BitmapSource CreatePreview(TerrainBrushMask mask)
    {
        var pixels = new byte[mask.Values.Length];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp(mask.Values[i] * 255 + 0.5f, 0, 255);
        var bitmap = BitmapSource.Create(mask.Size, mask.Size, 96, 96, PixelFormats.Gray8, null, pixels, mask.Size);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// A brush of a linear <see cref="Core.Mathematics.Color3"/> (e.g. the tint of a terrain layer without texture).
/// </summary>
public class TerrainTintBrush : OneWayValueConverter<TerrainTintBrush>
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Core.Mathematics.Color3 color)
            return null;
        var srgb = color.ToSRgb();
        var brush = new SolidColorBrush(Color.FromRgb(ToByte(srgb.R), ToByte(srgb.G), ToByte(srgb.B)));
        brush.Freeze();
        return brush;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
}

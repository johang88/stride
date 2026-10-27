// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Stride.Blockout.Rendering;
using Stride.Core.Presentation.ValueConverters;
using SColor3 = Stride.Core.Mathematics.Color3;
using SColor4 = Stride.Core.Mathematics.Color4;

namespace Stride.Blockout.Editor.Views;

/// <summary>
/// The swatch of a <see cref="BlockoutLook"/>: its pattern, two meters across.
/// </summary>
public class BlockoutLookPreview : OneWayValueConverter<BlockoutLookPreview>
{
    private const int Size = 48;
    private static readonly Dictionary<BlockoutLook, BitmapSource> Cache = new();

    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var look = value switch
        {
            BlockoutLook x => x,
            BlockoutPattern pattern => new BlockoutLook(pattern, new SColor3(0.62f), 1, 0.6f, 0.85f),
            _ => (BlockoutLook?)null,
        };
        if (look == null)
            return null;
        if (Cache.TryGetValue(look.Value, out var bitmap))
            return bitmap;
        var pixels = BlockoutPatternPreview.Render(look.Value, Size);
        bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        bitmap.Freeze();
        if (Cache.Count > 512)
            Cache.Clear();
        Cache[look.Value] = bitmap;
        return bitmap;
    }
}

/// <summary>
/// A brush of a color (<see cref="SColor3"/> or <see cref="SColor4"/>, sRGB).
/// </summary>
public class BlockoutColorBrush : OneWayValueConverter<BlockoutColorBrush>
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = value switch
        {
            SColor3 c => new SColor4(c, 1),
            SColor4 c => c,
            _ => (SColor4?)null,
        };
        if (color == null)
            return null;
        var c4 = color.Value;
        var brush = new SolidColorBrush(Color.FromRgb((byte)Math.Clamp(c4.R * 255, 0, 255), (byte)Math.Clamp(c4.G * 255, 0, 255), (byte)Math.Clamp(c4.B * 255, 0, 255)));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The thumbnail of a palette shape (a <see cref="BlockoutShapeItemViewModel"/>): its mesh, lit, seen from the front right.
/// </summary>
public class BlockoutShapeThumbnail : OneWayValueConverter<BlockoutShapeThumbnail>
{
    private const int Size = 96;
    private static readonly ConditionalWeakTable<BlockoutShapeItemViewModel, BitmapSource> Cache = new();

    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not BlockoutShapeItemViewModel item)
            return null;
        if (Cache.TryGetValue(item, out var bitmap))
            return bitmap;
        bitmap = Render(item);
        Cache.AddOrUpdate(item, bitmap);
        return bitmap;
    }

    private static BitmapSource Render(BlockoutShapeItemViewModel item)
    {
        var geometry = item.PreviewGeometry;
        var mesh = new MeshGeometry3D();
        foreach (var vertex in geometry.Vertices)
        {
            mesh.Positions.Add(new Point3D(vertex.Position.X, vertex.Position.Y, vertex.Position.Z));
            mesh.Normals.Add(new Vector3D(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z));
        }
        // Stride front faces are clockwise, WPF ones counter-clockwise
        for (int i = 0; i < geometry.Indices.Length; i += 3)
        {
            mesh.TriangleIndices.Add(geometry.Indices[i]);
            mesh.TriangleIndices.Add(geometry.Indices[i + 2]);
            mesh.TriangleIndices.Add(geometry.Indices[i + 1]);
        }
        mesh.Freeze();

        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(205, 200, 190)));
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(90, 90, 95)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(225, 220, 210), new Vector3D(-0.5, -1.0, 0.7)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(70, 80, 100), new Vector3D(0.8, 0.3, 0.4)));
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });

        var bounds = geometry.Bounds;
        var center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var radius = Math.Max((bounds.Maximum - bounds.Minimum).Length() * 0.5f, 0.1f);
        const double fieldOfView = 30;
        var distance = radius / Math.Sin(fieldOfView * 0.5 * Math.PI / 180) * 1.02;
        // From the front (-Z) right, above
        var direction = new Vector3D(0.75, 0.62, -1.15);
        direction.Normalize();
        var position = new Point3D(center.X + direction.X * distance, center.Y + direction.Y * distance, center.Z + direction.Z * distance);
        var camera = new PerspectiveCamera(position, -direction, new Vector3D(0, 1, 0), fieldOfView);

        var viewport = new Viewport3D { Camera = camera, Width = Size, Height = Size };
        viewport.Children.Add(new ModelVisual3D { Content = group });
        viewport.Measure(new Size(Size, Size));
        viewport.Arrange(new Rect(0, 0, Size, Size));
        viewport.UpdateLayout();
        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(viewport);
        target.Freeze();
        return target;
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Core.Presentation.Themes;
using Stride.Core.Presentation.ValueConverters;

namespace Stride.Assets.Presentation.ValueConverters;

/// <summary>
/// The content of an editor tool's toolbar toggle: its icon (<see cref="EntityHierarchyEditorToolViewModel.IconResourceKey"/>,
/// themed like the editor's own icons) or its name.
/// </summary>
public class EditorToolToContent : OneWayValueConverter<EditorToolToContent>
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not EntityHierarchyEditorToolViewModel tool)
            return null;
        if (tool.IconResourceKey != null && Application.Current?.TryFindResource(tool.IconResourceKey) is ImageSource source)
        {
            if (source is DrawingImage drawingImage)
                source = new DrawingImage(ImageThemingUtilities.TransformDrawing(drawingImage.Drawing, ThemeController.CurrentTheme.GetThemeBase().GetIconTheme()));
            return new Image { Source = source, Width = 16, Height = 16 };
        }
        return tool.DisplayName;
    }
}

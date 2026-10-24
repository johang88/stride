// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Windows;
using Stride.Core.Diagnostics;
using Stride.Editor;

namespace Stride.Terrain.Editor.Views;

/// <summary>
/// The Game Studio's entry point into this package: adds the terrain tools panel (the data template of
/// <see cref="TerrainToolsViewModel"/>) to the application resources, where the scene editor's tool panel finds it.
/// </summary>
public sealed class TerrainEditorWpfPlugin : StrideAssetsPlugin
{
    /// <inheritdoc />
    protected override void Initialize(ILogger logger)
    {
        var views = (ResourceDictionary)Application.LoadComponent(new Uri("/Stride.Terrain.Editor.Wpf;component/Views/TerrainToolsView.xaml", UriKind.RelativeOrAbsolute));
        Application.Current.Resources.MergedDictionaries.Add(views);
    }
}

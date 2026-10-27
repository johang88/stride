// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Windows;
using Stride.Core.Diagnostics;
using Stride.Editor;

namespace Stride.Blockout.Editor.Views;

/// <summary>
/// The Game Studio's entry point into this package: adds the blockout tools panel (the data template of
/// <see cref="BlockoutToolsViewModel"/>) to the application resources, where the scene editor's tool panel finds it.
/// </summary>
public sealed class BlockoutEditorWpfPlugin : StrideAssetsPlugin
{
    /// <inheritdoc />
    protected override void Initialize(ILogger logger)
    {
        var views = (ResourceDictionary)Application.LoadComponent(new Uri("/Stride.Blockout.Editor.Wpf;component/Views/BlockoutToolsView.xaml", UriKind.RelativeOrAbsolute));
        Application.Current.Resources.MergedDictionaries.Add(views);
    }
}

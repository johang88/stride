// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Terrain.Assets;
using Stride.Core.Assets.Editor.Annotations;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Terrain.Editor;

[AssetViewModel<TerrainAsset>]
public class TerrainViewModel : AssetViewModel<TerrainAsset>
{
    public TerrainViewModel(AssetViewModelConstructionParameters parameters)
        : base(parameters)
    {
    }

    /// <summary>
    /// The samples being edited in a scene editor, if any.
    /// </summary>
    internal TerrainEditingSession EditingSession { get; set; }

    /// <summary>
    /// Gets the absolute path of the data file (or of the default one when not set yet).
    /// </summary>
    public UFile GetDataFilePath()
    {
        var relative = UPath.IsNullOrEmpty(Asset.DataFile) ? GetDefaultDataFile() : Asset.DataFile;
        return UPath.Combine(AssetItem.FullPath.GetFullDirectory(), relative);
    }

    internal UFile GetDefaultDataFile() => new UFile(AssetItem.FullPath.GetFileNameWithoutExtension() + Stride.Terrain.TerrainDataFile.Extension);

    protected override void OnPrepareSave(ILogger logger)
    {
        EditingSession?.Save(GetDataFilePath(), logger);
    }
}

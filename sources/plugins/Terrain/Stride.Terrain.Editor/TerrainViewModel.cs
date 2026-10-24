// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Terrain.Assets;
using Stride.Core.Assets.Editor.Annotations;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Presentation.Dirtiables;

namespace Stride.Terrain.Editor;

[AssetViewModel<TerrainAsset>]
public class TerrainViewModel : AssetViewModel<TerrainAsset>
{
    private readonly SamplesDirtyFlag samples;
    private bool propertiesDirty;

    public TerrainViewModel(AssetViewModelConstructionParameters parameters)
        : base(parameters)
    {
        samples = new SamplesDirtyFlag(this);
    }

    /// <summary>
    /// The samples being edited in a scene editor, if any.
    /// </summary>
    internal TerrainEditingSession EditingSession { get; set; }

    /// <summary>
    /// What the undoable edits of the samples (strokes, imports) dirty: the asset is dirty with them, so they're saved, but its
    /// properties didn't change, so the editor doesn't build and reload the terrain after each of them.
    /// </summary>
    /// <remarks>
    /// Operations dirtying the asset itself raise <see cref="SessionViewModel.AssetPropertiesChanged"/> when their transaction
    /// completes, which recompiles the asset and replaces the terrain of the editor games: a flash and a delay after each stroke.
    /// </remarks>
    internal IDirtiable SamplesDirtiable => samples;

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

    protected override void UpdateDirtiness(bool value)
    {
        propertiesDirty = value;
        base.UpdateDirtiness(propertiesDirty || samples.IsDirty);
    }

    private void OnSamplesDirtinessChanged() => base.UpdateDirtiness(propertiesDirty || samples.IsDirty);

    private sealed class SamplesDirtyFlag(TerrainViewModel owner) : IDirtiable
    {
        public bool IsDirty { get; private set; }

        public void UpdateDirtiness(bool value)
        {
            IsDirty = value;
            owner.OnSamplesDirtinessChanged();
        }
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Presentation.Dirtiables;
using Stride.Terrain;

namespace Stride.Terrain.Editor;

/// <summary>
/// Undoable removal of the data of a grass layer (its density) or a vegetation type (its instances) from the edited samples,
/// the later layers or types moving down one: goes with the removal of the layer or type from the terrain component.
/// </summary>
internal sealed class TerrainDefinitionDataOperation : DirtyingOperation
{
    private readonly IEditorGameController controller;
    private readonly TerrainEditingSession session;
    private readonly bool grass;
    private readonly int index;
    private byte[] removedGrass;
    private List<TerrainVegetationInstance> vegetationBefore;

    public TerrainDefinitionDataOperation(IEditorGameController controller, TerrainEditingSession session, bool grass, int index, IEnumerable<IDirtiable> dirtiables)
        : base(dirtiables)
    {
        this.controller = controller;
        this.session = session;
        this.grass = grass;
        this.index = index;
    }

    /// <summary>
    /// Removes the data (on the game thread).
    /// </summary>
    public void Remove() => Run(data =>
    {
        if (grass)
        {
            removedGrass = data.RemoveGrassLayer(index);
        }
        else
        {
            vegetationBefore = new List<TerrainVegetationInstance>(data.Vegetation);
            data.RemoveVegetationType(index);
        }
    });

    protected override void Undo() => Run(data =>
    {
        if (grass)
        {
            data.InsertGrassLayer(index, removedGrass);
        }
        else if (vegetationBefore != null)
        {
            data.Vegetation.Clear();
            data.Vegetation.AddRange(vegetationBefore);
            data.NotifyChanged(Stride.Core.Mathematics.Int2.Zero, new Stride.Core.Mathematics.Int2(data.Resolution - 1), TerrainChangeFlags.Vegetation);
        }
    });

    protected override void Redo() => Remove();

    protected override void FreezeContent()
    {
        removedGrass = null;
        vegetationBefore = null;
        base.FreezeContent();
    }

    private void Run(System.Action<TerrainData> action)
    {
        controller.InvokeAsync(() =>
        {
            lock (session.SyncRoot)
            {
                if (session.Data == null)
                    return;
                action(session.Data);
                session.MarkModified();
            }
        });
    }
}

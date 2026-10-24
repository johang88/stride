// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Presentation.Dirtiables;
using Stride.Terrain;

namespace Stride.Terrain.Editor;

/// <summary>
/// What a terrain component defines with data painted or placed in the terrain data.
/// </summary>
public enum TerrainDefinitionKind
{
    Grass,
    Vegetation,
    Fog,
}

/// <summary>
/// Undoable removal of the data of a grass or fog layer (its density) or a vegetation type (its instances) from the edited
/// samples, the later layers or types moving down one: goes with the removal of the layer or type from the terrain component.
/// </summary>
internal sealed class TerrainDefinitionDataOperation : DirtyingOperation
{
    private readonly IEditorGameController controller;
    private readonly TerrainEditingSession session;
    private readonly TerrainDefinitionKind kind;
    private readonly int index;
    private byte[] removedDensity;
    private List<TerrainVegetationInstance> vegetationBefore;

    public TerrainDefinitionDataOperation(IEditorGameController controller, TerrainEditingSession session, TerrainDefinitionKind kind, int index, IEnumerable<IDirtiable> dirtiables)
        : base(dirtiables)
    {
        this.controller = controller;
        this.session = session;
        this.kind = kind;
        this.index = index;
    }

    /// <summary>
    /// Removes the data (on the game thread).
    /// </summary>
    public void Remove() => Run(data =>
    {
        switch (kind)
        {
            case TerrainDefinitionKind.Grass:
                removedDensity = data.RemoveGrassLayer(index);
                break;
            case TerrainDefinitionKind.Fog:
                removedDensity = data.RemoveFogLayer(index);
                break;
            default:
                vegetationBefore = new List<TerrainVegetationInstance>(data.Vegetation);
                data.RemoveVegetationType(index);
                break;
        }
    });

    protected override void Undo() => Run(data =>
    {
        if (kind == TerrainDefinitionKind.Grass)
        {
            data.InsertGrassLayer(index, removedDensity);
        }
        else if (kind == TerrainDefinitionKind.Fog)
        {
            data.InsertFogLayer(index, removedDensity);
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
        removedDensity = null;
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

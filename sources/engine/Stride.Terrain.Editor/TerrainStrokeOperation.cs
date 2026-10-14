// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Presentation.Dirtiables;
using Stride.Terrain.Editing;

namespace Stride.Assets.Presentation.AssetEditors.TerrainEditor
{
    /// <summary>
    /// Undoable terrain edit (a brush stroke or an import): the affected tiles before and after.
    /// </summary>
    internal sealed class TerrainStrokeOperation : DirtyingOperation
    {
        private readonly IEditorGameController controller;
        private readonly TerrainEditingSession session;
        private TerrainSnapshot before;
        private TerrainSnapshot after;

        public TerrainStrokeOperation(IEditorGameController controller, TerrainEditingSession session, TerrainSnapshot before, TerrainSnapshot after, IEnumerable<IDirtiable> dirtiables)
            : base(dirtiables)
        {
            this.controller = controller;
            this.session = session;
            this.before = before;
            this.after = after;
        }

        protected override void Undo() => Apply(before);

        protected override void Redo() => Apply(after);

        protected override void FreezeContent()
        {
            before = null;
            after = null;
            base.FreezeContent();
        }

        private void Apply(TerrainSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            controller.InvokeAsync(() =>
            {
                lock (session.SyncRoot)
                {
                    if (session.Data == null)
                        return;
                    snapshot.Restore(session.Data);
                    session.MarkModified();
                }
            });
        }
    }
}

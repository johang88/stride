// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Terrain;

namespace Stride.Terrain.Editor;

/// <summary>
/// Tracks the samples of a terrain asset edited in the editor game, and writes them to the asset data file on save.
/// </summary>
internal sealed class TerrainEditingSession
{
    /// <summary>
    /// Held while the samples are modified (game thread) or copied for saving (save thread).
    /// </summary>
    public object SyncRoot { get; } = new();

    /// <summary>
    /// The game-side samples currently edited.
    /// </summary>
    public TerrainData Data { get; set; }

    /// <summary>
    /// Whether the samples changed since they were loaded or last saved.
    /// </summary>
    public bool HasUnsavedChanges { get; private set; }

    public void MarkModified()
    {
        HasUnsavedChanges = true;
    }

    public void Save(UFile path, ILogger logger)
    {
        TerrainData copy;
        lock (SyncRoot)
        {
            if (!HasUnsavedChanges || Data == null)
                return;
            copy = Data.Clone();
            HasUnsavedChanges = false;
        }

        try
        {
            TerrainDataFile.Write(path, copy);
        }
        catch (Exception e)
        {
            HasUnsavedChanges = true;
            logger.Error($"Failed to save the terrain data [{path}]: {e.Message}", e);
        }
    }
}

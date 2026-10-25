// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Stride.Core.Assets.Editor.Services;

/// <summary>
/// Builds and starts the session's current project, as the editor's Start command does, for editor extensions that start
/// the game in a given state (command line arguments, environment variables).
/// </summary>
public interface IProjectRunService
{
    /// <summary>
    /// A build is running: <see cref="BuildAndRunAsync"/> would do nothing.
    /// </summary>
    bool IsBuilding { get; }

    /// <summary>
    /// Saves the session, builds the current project and starts it (Windows executables).
    /// </summary>
    /// <param name="arguments">The command line of the game.</param>
    /// <param name="environment">Environment variables added for the game.</param>
    /// <returns>The started process, or null when the build failed or was canceled.</returns>
    Task<Process?> BuildAndRunAsync(string? arguments = null, IReadOnlyDictionary<string, string>? environment = null);
}

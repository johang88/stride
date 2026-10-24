// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Rendering;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// What the vegetation processor and the grass render feature of a visibility group share.
/// </summary>
internal sealed class TerrainVegetationState
{
    public static readonly PropertyKey<TerrainVegetationState> Key = new("TerrainVegetationState", typeof(TerrainVegetationState));

    /// <summary>
    /// The models drawn by the grass layers.
    /// </summary>
    public readonly Dictionary<RenderModel, TerrainGrassVariant> GrassModels = new();

    /// <summary>
    /// Position of the main camera in the last frame drawn (the levels of detail of the vegetation follow it).
    /// </summary>
    public Vector3 CameraPosition;

    public bool HasCamera;
}

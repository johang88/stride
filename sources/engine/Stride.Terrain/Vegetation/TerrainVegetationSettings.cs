// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Quality of the grass and vegetation of every terrain, for graphics options: the game registers an instance as a service and
/// changes it at any time (without one, the terrains draw as their definitions say).
/// </summary>
public sealed class TerrainVegetationSettings
{
    internal static readonly TerrainVegetationSettings Defaults = new();

    /// <summary>
    /// Whether the grass layers are drawn.
    /// </summary>
    public bool GrassEnabled { get; set; } = true;

    /// <summary>
    /// Multiplies the draw distance of the grass layers (and where they start thinning out).
    /// </summary>
    public float GrassDistanceScale { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the density of the grass layers (plants per square meter).
    /// </summary>
    public float GrassDensityScale { get; set; } = 1.0f;

    /// <summary>
    /// Whether the grass layers set to cast shadows do.
    /// </summary>
    public bool GrassShadows { get; set; } = true;

    /// <summary>
    /// Multiplies the distances where the vegetation types change level of detail and become impostors: more is more detail.
    /// </summary>
    public float VegetationDetailScale { get; set; } = 1.0f;

    /// <summary>
    /// Whether the vegetation types set to cast shadows do (their meshes).
    /// </summary>
    public bool VegetationShadows { get; set; } = true;

    /// <summary>
    /// Whether the impostors of the vegetation types set to cast shadows do.
    /// </summary>
    public bool ImpostorShadows { get; set; } = true;
}

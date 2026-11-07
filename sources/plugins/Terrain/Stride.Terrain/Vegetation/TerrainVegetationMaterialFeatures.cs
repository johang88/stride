// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Sways plants in the wind (grass layers, bushes): vertices move with their height above the model origin.
/// </summary>
[DataContract("MaterialTerrainGrassWindFeature")]
[Display("Grass wind")]
public sealed class MaterialTerrainGrassWindFeature : MaterialFeature, IMaterialDisplacementFeature
{
    /// <summary>
    /// Sideways movement per meter of height.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.1f)]
    [DataMemberRange(0, 2, 0.01, 0.05, 3)]
    public float Strength { get; set; } = 0.1f;

    /// <summary>
    /// Sway cycles per second (roughly).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(1.5f)]
    [DataMemberRange(0, 10, 0.1, 0.5, 2)]
    public float Speed { get; set; } = 1.5f;

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.AddShaderSource(MaterialShaderStage.Vertex, new ShaderClassSource("MaterialTerrainGrassWind", Strength.ToString("R", CultureInfo.InvariantCulture), Speed.ToString("R", CultureInfo.InvariantCulture)));
        // (blades: thin, for the screen-space effects)
        context.AddShaderSource(MaterialShaderStage.Pixel, new ShaderClassSource("MaterialTerrainThinSurface"));
    }
}

/// <summary>
/// Sways trees in the wind: the tree bends with its height, branches swing, leaves (<see cref="Flutter"/>) shiver. Give every
/// material of a tree the same strength and speed so its parts move together.
/// </summary>
[DataContract("MaterialTerrainTreeWindFeature")]
[Display("Tree wind")]
public sealed class MaterialTerrainTreeWindFeature : MaterialFeature, IMaterialDisplacementFeature
{
    /// <summary>
    /// How far the tree moves (1: about 0.4 m at 10 m of height, 1.6 m at 20 m, in the strongest gusts).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.25f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 3)]
    public float Strength { get; set; } = 0.25f;

    /// <summary>
    /// How fast it moves (1: a sway every ten seconds or so, with faster branches and leaves).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.1, 0.5, 2)]
    public float Speed { get; set; } = 1.0f;

    /// <summary>
    /// Leaf shiver (leaf materials 1, bark 0).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 2, 0.05, 0.25, 2)]
    public float Flutter { get; set; }

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.AddShaderSource(MaterialShaderStage.Vertex, new ShaderClassSource("MaterialTerrainTreeWind", Strength.ToString("R", CultureInfo.InvariantCulture),
            Speed.ToString("R", CultureInfo.InvariantCulture), Flutter.ToString("R", CultureInfo.InvariantCulture)));
    }
}

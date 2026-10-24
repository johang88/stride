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
    }
}

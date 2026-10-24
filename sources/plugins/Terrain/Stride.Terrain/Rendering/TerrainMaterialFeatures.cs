// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Terrain.Rendering;

/// <summary>
/// Vertex stage of the terrain material: places patch vertices on the height field.
/// </summary>
[DataContract("MaterialTerrainDisplacementFeature")]
[Display("Terrain Height Field")]
public sealed class MaterialTerrainDisplacementFeature : MaterialFeature, IMaterialDisplacementFeature
{
    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.AddShaderSource(MaterialShaderStage.Vertex, new ShaderClassSource("TerrainMaterialVertex"));
    }
}

/// <summary>
/// Pixel stage of the terrain material: surface attributes and normal, either from the layers or the virtual texture.
/// </summary>
[DataContract("MaterialTerrainSurfaceFeature")]
[Display("Terrain Surface")]
public sealed class MaterialTerrainSurfaceFeature : MaterialFeature, IMaterialDiffuseFeature
{
    /// <summary>
    /// The shader class computing the surface (TerrainMaterialSurface or TerrainMaterialSurfaceVirtualTexture).
    /// </summary>
    public string ShaderClass { get; set; } = "TerrainMaterialSurface";

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.UseStream(MaterialShaderStage.Pixel, "matDiffuse");
        context.UseStream(MaterialShaderStage.Pixel, "matColorBase");
        context.UseStream(MaterialShaderStage.Pixel, "matGlossiness");
        context.UseStream(MaterialShaderStage.Pixel, "matAmbientOcclusion");
        context.AddShaderSource(MaterialShaderStage.Pixel, new ShaderClassSource(ShaderClass));
    }
}

internal static class TerrainMaterialFactory
{
    public static MaterialDescriptor CreateDescriptor(string surfaceShader)
    {
        return new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialTerrainSurfaceFeature { ShaderClass = surfaceShader },
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Displacement = new MaterialTerrainDisplacementFeature(),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
            },
        };
    }
}

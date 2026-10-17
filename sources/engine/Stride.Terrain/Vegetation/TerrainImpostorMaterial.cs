// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Vertex stage of the vegetation impostors: expands each instance into a billboard facing the view and picks the atlas
/// views around the view direction.
/// </summary>
[DataContract("MaterialTerrainImpostorDisplacementFeature")]
[NonInstantiable]
public sealed class MaterialTerrainImpostorDisplacementFeature : MaterialFeature, IMaterialDisplacementFeature
{
    public override void GenerateShader(MaterialGeneratorContext context)
    {
        context.AddShaderSource(MaterialShaderStage.Vertex, new ShaderClassSource("MaterialTerrainImpostorDisplacement"));
    }
}

/// <summary>
/// The baked impostor color, blended from the atlas views around the view direction.
/// </summary>
[DataContract("ComputeTerrainImpostorDiffuse")]
[NonInstantiable]
public sealed class ComputeTerrainImpostorDiffuse : IComputeColor
{
    public bool HasChanged => false;

    public ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys) => new ShaderClassSource("ComputeColorTerrainImpostorDiffuse");

    public IEnumerable<IComputeNode> GetChildren(object context = null) => [];
}

/// <summary>
/// The baked impostor normal (tangent space of the billboard).
/// </summary>
[DataContract("ComputeTerrainImpostorNormal")]
[NonInstantiable]
public sealed class ComputeTerrainImpostorNormal : IComputeColor
{
    public bool HasChanged => false;

    public ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys) => new ShaderClassSource("ComputeColorTerrainImpostorNormal");

    public IEnumerable<IComputeNode> GetChildren(object context = null) => [];
}

/// <summary>
/// The material of the vegetation impostors: the baked atlas lit like foliage, alpha tested.
/// </summary>
internal static class TerrainImpostorMaterial
{
    public static MaterialDescriptor CreateDescriptor()
    {
        return new MaterialDescriptor
        {
            Attributes =
            {
                Displacement = new MaterialTerrainImpostorDisplacementFeature(),
                Surface = new MaterialNormalMapFeature(new ComputeTerrainImpostorNormal()) { ScaleAndBias = true, IsXYNormal = false },
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(0.125f)),
                Diffuse = new MaterialDiffuseMapFeature(new ComputeTerrainImpostorDiffuse()),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f)),
                SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
                SubsurfaceScattering = new MaterialTerrainVegetationSubsurfaceFeature
                {
                    SubsurfaceLightingAmount = new ComputeFloat(0.8f),
                    Extinction = new ComputeColor(new Color4(0.12f, 0.23f, 0.12f, 1.0f)),
                },
                Transparency = new MaterialTransparencyCutoffFeature { Alpha = new ComputeFloat(0.25f) },
                CullMode = CullMode.None,
            },
        };
    }
}

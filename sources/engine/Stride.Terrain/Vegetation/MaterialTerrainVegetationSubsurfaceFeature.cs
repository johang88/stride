// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Terrain.Vegetation;

public static class TerrainVegetationSubsurfaceKeys
{
    public static readonly ObjectParameterKey<Texture> SubsurfaceLightingAmount = ParameterKeys.NewObject<Texture>();
    public static readonly ValueParameterKey<float> SubsurfaceLightingAmountValue = ParameterKeys.NewValue<float>();
    public static readonly ObjectParameterKey<Texture> Extinction = ParameterKeys.NewObject<Texture>();
    // Color4: Extinction is a color node, a constant color binds through this key
    public static readonly ValueParameterKey<Color4> ExtinctionValue = ParameterKeys.NewValue<Color4>();
}

/// <summary>
/// Foliage shading: leaves are lit through both faces, and get back light from the sky, dimmed by what goes through the leaf.
/// </summary>
[DataContract("MaterialTerrainVegetationSubsurfaceFeature")]
[Display("Subsurface Scattering (Vegetation)")]
public sealed class MaterialTerrainVegetationSubsurfaceFeature : MaterialFeature, IMaterialSubsurfaceScatteringFeature
{
    /// <summary>
    /// How much the light wraps around (lower: flatter and brighter) and how much sky back light the leaves get.
    /// </summary>
    [NotNull]
    [DataMember(10)]
    [Display("Lighting amount")]
    public IComputeScalar SubsurfaceLightingAmount { get; set; } = new ComputeFloat(0.5f);

    /// <summary>
    /// The color of light after going through a leaf (e.g. the leaf texture, or a dark green).
    /// </summary>
    [NotNull]
    [DataMember(20)]
    [Display("Transmission")]
    public IComputeColor Extinction { get; set; } = new ComputeColor(new Color4(0.12f, 0.23f, 0.12f, 1.0f));

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        if (SubsurfaceLightingAmount is ComputeFloat amount)
            amount.Value = MathUtil.Clamp(amount.Value, 0.0f, 1.0f);

        var amountSource = SubsurfaceLightingAmount.GenerateShaderSource(context, new MaterialComputeColorKeys(TerrainVegetationSubsurfaceKeys.SubsurfaceLightingAmount, TerrainVegetationSubsurfaceKeys.SubsurfaceLightingAmountValue, Color.White));
        var extinctionSource = Extinction.GenerateShaderSource(context, new MaterialComputeColorKeys(TerrainVegetationSubsurfaceKeys.Extinction, TerrainVegetationSubsurfaceKeys.ExtinctionValue, Color.White));

        var shaderSource = new ShaderMixinSource();
        shaderSource.Mixins.Add(new ShaderClassSource("MaterialTerrainVegetationSubsurfaceShading"));
        shaderSource.AddComposition("SubsurfaceLightingAmount", amountSource);
        shaderSource.AddComposition("Extinction", extinctionSource);

        var shaderBuilder = context.AddShading(this);
        shaderBuilder.LightDependentSurface = shaderSource;
    }

    public bool Equals(IMaterialShadingModelFeature other)
    {
        return other is MaterialTerrainVegetationSubsurfaceFeature feature
            && SubsurfaceLightingAmount.Equals(feature.SubsurfaceLightingAmount)
            && Extinction.Equals(feature.Extinction);
    }

    public override bool Equals(object obj) => obj is IMaterialShadingModelFeature feature && Equals(feature);

    public override int GetHashCode() => HashCode.Combine(SubsurfaceLightingAmount, Extinction);
}

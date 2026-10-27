// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Blockout.Rendering;

/// <summary>
/// The pattern of a blockout material (see the ComputeColorBlockoutPattern shader).
/// </summary>
[DataContract("ComputeBlockoutPattern")]
[NonInstantiable]
public sealed class ComputeBlockoutPattern : IComputeColor
{
    public ComputeBlockoutPattern()
    {
    }

    public ComputeBlockoutPattern(BlockoutLook look)
    {
        Look = look;
    }

    public BlockoutLook Look { get; set; }

    public bool HasChanged => false;

    public ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        var color = Look.LinearColor;
        var accent = Look.LinearAccent;
        context.Parameters.Set(ComputeColorBlockoutPatternKeys.BlockoutColor, new Vector4(color.R, color.G, color.B, 1));
        context.Parameters.Set(ComputeColorBlockoutPatternKeys.BlockoutAccent, new Vector4(accent.R, accent.G, accent.B, 1));
        context.Parameters.Set(ComputeColorBlockoutPatternKeys.BlockoutParameters, new Vector4(Math.Max(Look.Scale, 0.05f), (int)Look.Pattern, Look.ToneVariation, 0));
        return new ShaderClassSource("ComputeColorBlockoutPattern");
    }

    public IEnumerable<IComputeNode> GetChildren(object context = null) => [];
}

/// <summary>
/// The materials of the blockout shapes, one per look, shared by the shapes that have it.
/// </summary>
public sealed class BlockoutMaterials
{
    private readonly Dictionary<BlockoutLook, Material> materials = new();
    private readonly IServiceRegistry services;

    public BlockoutMaterials([NotNull] IServiceRegistry services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// The material of a look (game thread).
    /// </summary>
    public Material Get(BlockoutLook look)
    {
        if (materials.TryGetValue(look, out var material))
            return material;
        var device = services.GetSafeServiceAs<IGraphicsDeviceService>().GraphicsDevice;
        material = Material.New(device, CreateDescriptor(look), services.GetService<ContentManager>());
        materials.Add(look, material);
        return material;
    }

    /// <summary>
    /// The material descriptor of a look: the pattern as diffuse, dielectric, rough.
    /// </summary>
    public static MaterialDescriptor CreateDescriptor(BlockoutLook look)
    {
        return new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(new ComputeBlockoutPattern(look)),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(1 - Math.Clamp(look.Roughness, 0, 1))),
                Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0.0f)),
                // The polynomial environment term: the lookup texture of the default one isn't in every content database
                SpecularModel = new MaterialSpecularMicrofacetModelFeature { Environment = new MaterialSpecularMicrofacetEnvironmentGGXPolynomial() },
            },
        };
    }
}

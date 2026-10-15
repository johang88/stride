// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;
using Stride.Terrain.VirtualTexturing;

namespace Stride.Terrain.Rendering;

// Material nodes blending objects (rocks, walls, props) with the terrain they stand on. They read the terrain virtual
// texture, so the blended surface matches the terrain exactly. Typical use: add a layer to the object material whose
// material uses Terrain Blend Color / Normal / Surface, with a Terrain Blend Mask as the layer blend map.

internal static class TerrainBlendNodes
{
    public static void RequestVirtualTexture(ShaderGeneratorContext context)
    {
        // Objects sampling the virtual texture also request its pages in the feedback pass
        context.Parameters.Set(TerrainVirtualTextureKeys.SamplesVirtualTexture, true);
    }

    public static string Format(float value) => value.ToString("0.0#####", CultureInfo.InvariantCulture);
}

/// <summary>
/// 1 where the object touches the terrain, fading out with the height above it.
/// </summary>
[DataContract("ComputeTerrainBlendMask")]
[Display("Terrain Blend Mask")]
public sealed class ComputeTerrainBlendMask : ComputeNode, IComputeScalar, IComputeColor
{
    /// <summary>
    /// Height above the terrain in meters over which the blend fades out.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0.01, 20, 0.05, 0.5, 2)]
    public float BlendHeight { get; set; } = 0.6f;

    /// <summary>
    /// Exponent of the fade (higher: tighter at the ground).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0.1, 10, 0.1, 1, 2)]
    public float Sharpness { get; set; } = 0.8f;

    /// <summary>
    /// How much the blend favors upward facing surfaces (0: none, 1: only them).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0, 1, 0.05, 0.25, 2)]
    public float UpFacing { get; set; } = 0.3f;

    /// <summary>
    /// Variation of the blend height by noise, relative to the blend height.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0, 1, 0.05, 0.25, 2)]
    public float Noise { get; set; } = 0.4f;

    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        TerrainBlendNodes.RequestVirtualTexture(context);
        return new ShaderClassSource("ComputeTerrainBlendMask", TerrainBlendNodes.Format(BlendHeight), TerrainBlendNodes.Format(Sharpness), TerrainBlendNodes.Format(UpFacing), TerrainBlendNodes.Format(Noise));
    }
}

/// <summary>
/// Albedo of the terrain under the object.
/// </summary>
[DataContract("ComputeTerrainBlendColor")]
[Display("Terrain Blend Color")]
public sealed class ComputeTerrainBlendColor : ComputeNode, IComputeColor
{
    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        TerrainBlendNodes.RequestVirtualTexture(context);
        return new ShaderClassSource("ComputeTerrainBlendColor");
    }
}

/// <summary>
/// Normal of the terrain under the object, for a normal map (in the tangent space of the object, scaled and biased).
/// </summary>
[DataContract("ComputeTerrainBlendNormal")]
[Display("Terrain Blend Normal")]
public sealed class ComputeTerrainBlendNormal : ComputeNode, IComputeColor
{
    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        TerrainBlendNodes.RequestVirtualTexture(context);
        return new ShaderClassSource("ComputeTerrainBlendNormal");
    }
}

/// <summary>
/// Glossiness (red) and ambient occlusion (green) of the terrain under the object.
/// </summary>
[DataContract("ComputeTerrainBlendSurface")]
[Display("Terrain Blend Surface")]
public sealed class ComputeTerrainBlendSurface : ComputeNode, IComputeScalar, IComputeColor
{
    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        TerrainBlendNodes.RequestVirtualTexture(context);
        return new ShaderClassSource("ComputeTerrainBlendSurface");
    }
}

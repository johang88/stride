// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Water.Materials;

/// <summary>
/// Foam coverage of a water surface (0..1), for the Foam of the Water transparency: a tileable foam pattern carried by the
/// flow (or drifting), dense where the floor is close under the surface (shores, rocks, posts, legs wading) and breaking up
/// into lace further out. Transparent materials only (it reads the scene depth).
/// </summary>
[DataContract("ComputeWaterFoam")]
[Display("Water Foam")]
public sealed class ComputeWaterFoam : ComputeNode, IComputeScalar, IComputeColor
{
    /// <userdoc>Tileable foam pattern (gray, red channel read): high values foam first.</userdoc>
    [DataMember(10)]
    public Texture FoamMap { get; set; }

    /// <userdoc>Size of one repetition of the pattern (meters).</userdoc>
    [DataMember(20)]
    [DefaultValue(3.0f)]
    [DataMemberRange(0.05, 1000, 0.1, 1, 2)]
    public float Scale { get; set; } = 3.0f;

    /// <userdoc>Depth of the water (meters) over which the shore foam fades out.</userdoc>
    [DataMember(30)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0.01, 100, 0.05, 0.25, 2)]
    public float ShoreDepth { get; set; } = 0.4f;

    /// <userdoc>How much foam gathers at the shore (1: covered right at the edge).</userdoc>
    [DataMember(40)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 2, 0.05, 0.25, 2)]
    public float ShoreAmount { get; set; } = 0.8f;

    /// <userdoc>Foam everywhere, away from the shore (streaks on flowing water).</userdoc>
    [DataMember(50)]
    [DefaultValue(0.1f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float OpenWaterAmount { get; set; } = 0.1f;

    /// <userdoc>Softness of the foam's edges.</userdoc>
    [DataMember(60)]
    [DefaultValue(0.15f)]
    [DataMemberRange(0.01, 1, 0.01, 0.1, 2)]
    public float Softness { get; set; } = 0.15f;

    /// <userdoc>The foam is carried along the mesh's V texture coordinate (rivers on terrain splines), like the Water Normal's flow.</userdoc>
    [DataMember(70)]
    [DefaultValue(false)]
    [Display("Flow along V", "Flow")]
    public bool Flow { get; set; }

    /// <userdoc>Speed of the flow (m/s) along increasing V (use the Water Normal's); or of the drift along world X when not flowing.</userdoc>
    [DataMember(80)]
    [DefaultValue(0.8f)]
    [DataMemberRange(-20, 20, 0.05, 0.25, 2)]
    [Display("Speed", "Flow")]
    public float FlowSpeed { get; set; } = 0.8f;

    /// <userdoc>How much slower the foam moves at the edges of the mesh (U 0 and 1).</userdoc>
    [DataMember(90)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    [Display("Edge slowdown", "Flow")]
    public float FlowEdgeSlowdown { get; set; } = 0.6f;

    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        var textureKey = context.GetTextureKey(FoamMap, WaterMaterialKeys.FoamMap, new Color(0, 0, 0, 255));
        var shape = new Vector4(1.0f / Math.Max(Scale, 1e-3f), 1.0f / Math.Max(ShoreDepth, 1e-3f), Math.Max(ShoreAmount, 0), Math.Max(Softness, 0.01f));
        var flow = new Vector4(FlowSpeed, Math.Clamp(FlowEdgeSlowdown, 0, 1), Math.Clamp(OpenWaterAmount, 0, 1), Flow ? 0 : FlowSpeed);
        return new ShaderClassSource("ComputeWaterFoam", textureKey,
            WaterMaterialKeys.Set(context, WaterMaterialKeys.FoamShape, shape),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.FoamFlow, flow),
            Flow);
    }
}

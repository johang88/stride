// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Shaders;

namespace Stride.Water.Materials;

/// <summary>
/// 0 where a transparent surface meets the opaque scene, 1 where the scene is <see cref="Distance"/> or more behind it: soft
/// intersections (the alpha of water, fog cards, glows) and shoreline masks. Transparent materials only (it reads the scene
/// depth the forward renderer binds during the transparent stage).
/// </summary>
[DataContract("ComputeSceneDepthFade")]
[Display("Scene Depth Fade")]
public sealed class ComputeSceneDepthFade : ComputeNode, IComputeScalar, IComputeColor
{
    /// <userdoc>Distance behind the surface (meters) at which the fade reaches 1.</userdoc>
    [DataMember(10)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0.001, 1000, 0.01, 0.1, 3)]
    public float Distance { get; set; } = 0.5f;

    /// <userdoc>Measure the distance vertically (the depth of the floor under a horizontal surface, whatever the view angle) instead of along the view ray.</userdoc>
    [DataMember(20)]
    [DefaultValue(true)]
    public bool Vertical { get; set; } = true;

    /// <userdoc>Shape of the fade (1: linear, above 1: stays low longer, below 1: rises faster).</userdoc>
    [DataMember(30)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0.1, 10, 0.05, 0.5, 2)]
    public float Exponent { get; set; } = 1.0f;

    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        var fade = new Vector4(1.0f / Math.Max(Distance, 1e-4f), Vertical ? 1 : 0, Math.Max(Exponent, 0.01f), 0);
        return new ShaderClassSource("ComputeSceneDepthFade", WaterMaterialKeys.Set(context, WaterMaterialKeys.DepthFade, fade));
    }
}

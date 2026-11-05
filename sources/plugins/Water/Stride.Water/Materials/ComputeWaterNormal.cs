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
/// How a <see cref="ComputeWaterNormal"/> lays its ripples on the surface.
/// </summary>
public enum WaterNormalSpace
{
    /// <summary>
    /// Along world X and Z in meters, whatever the mesh's texture coordinates (ponds, lakes, bogs, puddles).
    /// </summary>
    World,

    /// <summary>
    /// Along the mesh's first texture coordinates (rivers whose V runs downstream, with the direction along V).
    /// </summary>
    TextureCoordinates,
}

/// <summary>
/// Animated normal of a water surface, for the normal map of a material (keep its "scale and bias" on): layers of a tileable
/// ripple normal map drifting with the wind at different scales and angles, patchy calm and rippled areas, a fade with
/// distance, and the dynamic ripples of characters wading through (<see cref="WaterRippleEmitterComponent"/>).
/// </summary>
[DataContract("ComputeWaterNormal")]
[Display("Water Normal")]
public sealed class ComputeWaterNormal : ComputeNode, IComputeColor
{
    /// <summary>
    /// Tileable tangent space normal map of ripples (only X and Y are read).
    /// </summary>
    /// <userdoc>Tileable normal map of ripples (only its X and Y are read).</userdoc>
    [DataMember(10)]
    public Texture NormalMap { get; set; }

    /// <userdoc>Lay the ripples along world X/Z in meters, or along the mesh's texture coordinates.</userdoc>
    [DataMember(20)]
    [DefaultValue(WaterNormalSpace.World)]
    public WaterNormalSpace Space { get; set; } = WaterNormalSpace.World;

    /// <userdoc>Size of one repetition of the main ripple layer (meters, or texture coordinate units).</userdoc>
    [DataMember(30)]
    [DefaultValue(4.0f)]
    [DataMemberRange(0.05, 1000, 0.1, 1, 2)]
    public float Scale { get; set; } = 4.0f;

    /// <userdoc>Direction the wind blows the ripples to, in degrees (0: along +X / +U, 90: along +Z / +V).</userdoc>
    [DataMember(40)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-360, 360, 1, 15, 1)]
    public float Direction { get; set; }

    /// <userdoc>Speed at which the ripples drift with the wind (m/s, or texture units per second).</userdoc>
    [DataMember(50)]
    [DefaultValue(0.15f)]
    [DataMemberRange(0, 20, 0.01, 0.1, 3)]
    public float Speed { get; set; } = 0.15f;

    /// <userdoc>Steepness of the ripples.</userdoc>
    [DataMember(60)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 2)]
    public float Strength { get; set; } = 1.0f;

    /// <userdoc>Strength of the fine layer: smaller, faster ripples crossing the main ones.</userdoc>
    [DataMember(70)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float Detail { get; set; } = 0.6f;

    /// <userdoc>Strength of the broad layer: long, slow undulations.</userdoc>
    [DataMember(80)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float Swell { get; set; } = 0.5f;

    /// <userdoc>Patchiness: calm, mirror-like areas between rippled ones, drifting with the wind (0: uniform ripples, 1: dead calm in the calm areas).</userdoc>
    [DataMember(90)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Gusts { get; set; } = 0.6f;

    /// <userdoc>Size of the calm and rippled patches (meters).</userdoc>
    [DataMember(100)]
    [DefaultValue(25.0f)]
    [DataMemberRange(1, 1000, 1, 10, 1)]
    public float GustSize { get; set; } = 25.0f;

    /// <userdoc>Distance from the camera at which the ripples are at half strength (meters): less sparkle and aliasing far away. 0: no fade.</userdoc>
    [DataMember(110)]
    [DefaultValue(60.0f)]
    [DataMemberRange(0, 10000, 1, 10, 1)]
    public float FadeDistance { get; set; } = 60.0f;

    /// <userdoc>Strength of the dynamic ripples of characters wading through (Water Ripple Emitter components). 0: none.</userdoc>
    [DataMember(120)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 4, 0.01, 0.1, 2)]
    public float DynamicRipples { get; set; } = 1.0f;

    /// <userdoc>The water flows along the mesh's V texture coordinate (e.g. downstream on a river ribbon from a terrain spline): the ripples are carried by it instead of drifting with the wind. Laid out in world meters whatever the Space.</userdoc>
    [DataMember(130)]
    [DefaultValue(false)]
    [Display("Flow along V", "Flow")]
    public bool Flow { get; set; }

    /// <userdoc>Speed of the flow (m/s) along increasing V; negative flows the other way.</userdoc>
    [DataMember(140)]
    [DefaultValue(0.8f)]
    [DataMemberRange(-20, 20, 0.05, 0.25, 2)]
    [Display("Speed", "Flow")]
    public float FlowSpeed { get; set; } = 0.8f;

    /// <userdoc>How much slower the water flows at the edges of the mesh (U 0 and 1) than in its middle (0: same speed, 1: still at the edges).</userdoc>
    [DataMember(150)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    [Display("Edge slowdown", "Flow")]
    public float FlowEdgeSlowdown { get; set; } = 0.6f;

    public bool HasChanged => false;

    public override ShaderSource GenerateShaderSource(ShaderGeneratorContext context, MaterialComputeColorKeys baseKeys)
    {
        var textureKey = context.GetTextureKey(NormalMap, WaterMaterialKeys.NormalMap, new Color(128, 128, 255, 255));
        var scale = Math.Max(Scale, 1e-3f);
        var angle = MathUtil.DegreesToRadians(Direction);
        var layer0 = Layer(scale, angle, Speed, 1.0f);
        var layer1 = Layer(scale * 0.37f, angle + MathUtil.DegreesToRadians(57.0f), Speed * 1.4f, Detail);
        var layer2 = Layer(scale * 2.9f, angle - MathUtil.DegreesToRadians(31.0f), Speed * 0.55f, Swell);
        var gustSize = Math.Max(GustSize, 0.1f);
        var drift = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (Space == WaterNormalSpace.World ? Speed : 0.15f) * 0.5f / gustSize;
        var gusts = new Vector4(1.0f / gustSize, Math.Clamp(Gusts, 0, 1), drift.X, drift.Y);
        var settings = new Vector4(Strength, FadeDistance > 0 ? 1.0f / FadeDistance : 0.0f, DynamicRipples, 0);

        return new ShaderClassSource("ComputeWaterNormal", textureKey,
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalLayer0, layer0),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalLayer1, layer1),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalLayer2, layer2),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalGusts, gusts),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalSettings, settings),
            WaterMaterialKeys.Set(context, WaterMaterialKeys.NormalFlow, new Vector4(FlowSpeed, Math.Clamp(FlowEdgeSlowdown, 0, 1), 0, 0)),
            Space == WaterNormalSpace.World || Flow, Flow);
    }

    private static Vector4 Layer(float scale, float angle, float speed, float strength)
        => new(MathF.Cos(angle) / scale, MathF.Sin(angle) / scale, speed / scale, strength);
}

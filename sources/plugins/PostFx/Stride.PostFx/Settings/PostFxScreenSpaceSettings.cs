// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.PostFx;

/// <summary>
/// Ambient occlusion: the light from around (the sky, probes) blocked by the geometry nearby.
/// </summary>
[DataContract("PostFxAmbientOcclusionSettings")]
public sealed class PostFxAmbientOcclusionSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Strength (0 none).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float Intensity { get; set; } = 1.0f;

    /// <summary>
    /// How far the occluding geometry is looked for (meters).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0.05, 10, 0.01, 0.1, 2)]
    public float Radius { get; set; } = 0.8f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxAmbientOcclusionSettings)target;
        Intensity = MathUtil.Lerp(Intensity, t.Intensity, amount);
        Radius = MathUtil.Lerp(Radius, t.Radius, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxAmbientOcclusionSettings)source;
        Intensity = s.Intensity;
        Radius = s.Radius;
    }
}

/// <summary>
/// Contact shadows: the small shadows of the main directional light the shadow maps are too coarse for.
/// </summary>
[DataContract("PostFxContactShadowSettings")]
public sealed class PostFxContactShadowSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Strength (0 none).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Intensity { get; set; } = 0.8f;

    /// <summary>
    /// Length of the shadows looked for (meters, close to the camera).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.35f)]
    [DataMemberRange(0.01, 5, 0.01, 0.1, 2)]
    public float Length { get; set; } = 0.35f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxContactShadowSettings)target;
        Intensity = MathUtil.Lerp(Intensity, t.Intensity, amount);
        Length = MathUtil.Lerp(Length, t.Length, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxContactShadowSettings)source;
        Intensity = s.Intensity;
        Length = s.Length;
    }
}

/// <summary>
/// Reflections and indirect light found on screen, replacing the sky's and the probes' where they're found.
/// </summary>
[DataContract("PostFxScreenSpaceLightSettings")]
public sealed class PostFxScreenSpaceLightSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Strength of the screen-space reflections (0 none).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Reflections { get; set; } = 1.0f;

    /// <summary>
    /// Strength of the screen-space indirect light (0 none).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float IndirectLight { get; set; } = 1.0f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxScreenSpaceLightSettings)target;
        Reflections = MathUtil.Lerp(Reflections, t.Reflections, amount);
        IndirectLight = MathUtil.Lerp(IndirectLight, t.IndirectLight, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxScreenSpaceLightSettings)source;
        Reflections = s.Reflections;
        IndirectLight = s.IndirectLight;
    }
}

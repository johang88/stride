// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.PostFx;

/// <summary>
/// Depth of field: the blur of a thin lens away from its focus.
/// </summary>
[DataContract("PostFxDepthOfFieldSettings")]
public sealed class PostFxDepthOfFieldSettings : PostFxSettingsGroup
{
    /// <summary>
    /// How much of the lens's blur is applied (0: none, everything sharp).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Amount { get; set; }

    /// <summary>
    /// Distance in focus (meters).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(10.0f)]
    [DataMemberRange(0.1, 10000, 0.1, 1, 2)]
    public float FocusDistance { get; set; } = 10.0f;

    /// <summary>
    /// Aperture (f-number): smaller blurs more.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(2.8f)]
    [DataMemberRange(0.7, 32, 0.1, 1, 1)]
    public float FStop { get; set; } = 2.8f;

    /// <summary>
    /// Focal length of the lens the blur is computed for (millimeters, on a full frame sensor): longer blurs more. The
    /// field of view stays the camera's.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(50.0f)]
    [DataMemberRange(8, 600, 1, 10, 0)]
    public float FocalLength { get; set; } = 50.0f;

    /// <summary>
    /// How much the foreground in front of the focus blurs (1: as the lens does).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float NearBlur { get; set; } = 1.0f;

    /// <summary>
    /// Largest blur radius (share of the image's height).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.015f)]
    [DataMemberRange(0.001, 0.05, 0.001, 0.005, 3)]
    public float MaxBlur { get; set; } = 0.015f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxDepthOfFieldSettings)target;
        Amount = MathUtil.Lerp(Amount, t.Amount, amount);
        FocusDistance = MathUtil.Lerp(FocusDistance, t.FocusDistance, amount);
        FStop = MathUtil.Lerp(FStop, t.FStop, amount);
        FocalLength = MathUtil.Lerp(FocalLength, t.FocalLength, amount);
        NearBlur = MathUtil.Lerp(NearBlur, t.NearBlur, amount);
        MaxBlur = MathUtil.Lerp(MaxBlur, t.MaxBlur, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxDepthOfFieldSettings)source;
        Amount = s.Amount;
        FocusDistance = s.FocusDistance;
        FStop = s.FStop;
        FocalLength = s.FocalLength;
        NearBlur = s.NearBlur;
        MaxBlur = s.MaxBlur;
    }
}

/// <summary>
/// Motion blur: the image smeared along the motion over the time the shutter is open.
/// </summary>
[DataContract("PostFxMotionBlurSettings")]
public sealed class PostFxMotionBlurSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Share of the frame the shutter is open (0.5: a 180 degree shutter; 0: no blur).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Shutter { get; set; } = 0.5f;

    /// <summary>
    /// Longest blur (share of the image's height).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.04f)]
    [DataMemberRange(0.001, 0.2, 0.001, 0.01, 3)]
    public float MaxBlur { get; set; } = 0.04f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxMotionBlurSettings)target;
        Shutter = MathUtil.Lerp(Shutter, t.Shutter, amount);
        MaxBlur = MathUtil.Lerp(MaxBlur, t.MaxBlur, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxMotionBlurSettings)source;
        Shutter = s.Shutter;
        MaxBlur = s.MaxBlur;
    }
}

/// <summary>
/// Heat haze over the ground: far, grazing views shimmer (deserts, summer roads). Heat sources are components.
/// </summary>
[DataContract("PostFxHeatHazeSettings")]
public sealed class PostFxHeatHazeSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Strength of the ground's shimmer (0: none).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float GroundShimmer { get; set; }

    /// <summary>
    /// Distance the ground's shimmer starts at (meters), full at twice it.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(60.0f)]
    [DataMemberRange(1, 5000, 1, 10, 0)]
    public float GroundDistance { get; set; } = 60.0f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxHeatHazeSettings)target;
        GroundShimmer = MathUtil.Lerp(GroundShimmer, t.GroundShimmer, amount);
        GroundDistance = MathUtil.Lerp(GroundDistance, t.GroundDistance, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxHeatHazeSettings)source;
        GroundShimmer = s.GroundShimmer;
        GroundDistance = s.GroundDistance;
    }
}

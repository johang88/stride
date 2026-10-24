// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.PostFx;

/// <summary>
/// A group of post-processing settings a volume can override (all of them, blended by the volume's weight).
/// </summary>
[DataContract(Inherited = true)]
public abstract class PostFxSettingsGroup
{
    /// <summary>
    /// Whether the volume's values replace those of the volumes under it (the defaults otherwise).
    /// </summary>
    [DataMember(0)]
    [DefaultValue(false)]
    public bool Override { get; set; }

    /// <summary>
    /// Moves the values towards those of another group of the same kind.
    /// </summary>
    internal abstract void BlendTowards(PostFxSettingsGroup target, float amount);

    /// <summary>
    /// Copies the values of another group of the same kind.
    /// </summary>
    internal abstract void CopyFrom(PostFxSettingsGroup source);
}

/// <summary>
/// How bright the scene is exposed.
/// </summary>
[DataContract("PostFxExposureSettings")]
public sealed class PostFxExposureSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Exposure added to the renderer's (EV): positive brightens.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-10, 10, 0.05, 0.5, 2)]
    public float Compensation { get; set; }

    /// <summary>
    /// Darkest scene the automatic exposure adapts to (EV of the average luminance, histogram exposure only).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(-6.0f)]
    [DataMemberRange(-16, 16, 0.1, 1, 1)]
    public float MinimumEV { get; set; } = -6.0f;

    /// <summary>
    /// Brightest scene the automatic exposure adapts to (EV of the average luminance, histogram exposure only).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(10.0f)]
    [DataMemberRange(-16, 24, 0.1, 1, 1)]
    public float MaximumEV { get; set; } = 10.0f;

    /// <summary>
    /// How fast the automatic exposure darkens towards a brighter scene (EV per second).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(3.0f)]
    [DataMemberRange(0.01, 50, 0.1, 1, 2)]
    public float SpeedUp { get; set; } = 3.0f;

    /// <summary>
    /// How fast the automatic exposure brightens towards a darker scene (EV per second).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0.01, 50, 0.1, 1, 2)]
    public float SpeedDown { get; set; } = 1.0f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxExposureSettings)target;
        Compensation = MathUtil.Lerp(Compensation, t.Compensation, amount);
        MinimumEV = MathUtil.Lerp(MinimumEV, t.MinimumEV, amount);
        MaximumEV = MathUtil.Lerp(MaximumEV, t.MaximumEV, amount);
        SpeedUp = MathUtil.Lerp(SpeedUp, t.SpeedUp, amount);
        SpeedDown = MathUtil.Lerp(SpeedDown, t.SpeedDown, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxExposureSettings)source;
        Compensation = s.Compensation;
        MinimumEV = s.MinimumEV;
        MaximumEV = s.MaximumEV;
        SpeedUp = s.SpeedUp;
        SpeedDown = s.SpeedDown;
    }
}

/// <summary>
/// The glow of bright light scattered in the lens and the eye.
/// </summary>
[DataContract("PostFxBloomSettings")]
public sealed class PostFxBloomSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Share of the light scattered (energy conserving: the image is blended towards its glow, nothing is added).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.04f)]
    [DataMemberRange(0, 1, 0.005, 0.05, 3)]
    public float Intensity { get; set; } = 0.04f;

    /// <summary>
    /// How far the glow spreads: the weight of its wider levels (0 tight, 1 wide).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.7f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Scatter { get; set; } = 0.7f;

    /// <summary>
    /// Tint of the glow.
    /// </summary>
    [DataMember(30)]
    public Color3 Tint { get; set; } = new(1.0f);

    /// <summary>
    /// Smudges and dust on the lens lit by the glow (the dirt texture, stretched over the screen).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(null)]
    public Texture LensDirt { get; set; }

    /// <summary>
    /// Strength of the lens dirt.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 10, 0.01, 0.1, 2)]
    public float LensDirtIntensity { get; set; }

    /// <summary>
    /// Horizontal streaks of anamorphic lenses from the brightest lights.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 2, 0.01, 0.1, 2)]
    public float AnamorphicIntensity { get; set; }

    /// <summary>
    /// Tint of the anamorphic streaks.
    /// </summary>
    [DataMember(70)]
    public Color3 AnamorphicTint { get; set; } = new(0.55f, 0.7f, 1.0f);

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxBloomSettings)target;
        Intensity = MathUtil.Lerp(Intensity, t.Intensity, amount);
        Scatter = MathUtil.Lerp(Scatter, t.Scatter, amount);
        Tint = Color3.Lerp(Tint, t.Tint, amount);
        // (a texture doesn't blend: the dirt of the volume that weighs most)
        if (amount >= 0.5f)
            LensDirt = t.LensDirt;
        LensDirtIntensity = MathUtil.Lerp(LensDirtIntensity, t.LensDirtIntensity, amount);
        AnamorphicIntensity = MathUtil.Lerp(AnamorphicIntensity, t.AnamorphicIntensity, amount);
        AnamorphicTint = Color3.Lerp(AnamorphicTint, t.AnamorphicTint, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxBloomSettings)source;
        Intensity = s.Intensity;
        Scatter = s.Scatter;
        Tint = s.Tint;
        LensDirt = s.LensDirt;
        LensDirtIntensity = s.LensDirtIntensity;
        AnamorphicIntensity = s.AnamorphicIntensity;
        AnamorphicTint = s.AnamorphicTint;
    }
}

/// <summary>
/// The color of the image before the display transform: white balance and a few creative controls.
/// </summary>
[DataContract("PostFxGradingSettings")]
public sealed class PostFxGradingSettings : PostFxSettingsGroup
{
    /// <summary>
    /// White balance: negative cools (bluer), positive warms (yellower).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-1, 1, 0.01, 0.1, 2)]
    public float Temperature { get; set; }

    /// <summary>
    /// White balance: negative towards green, positive towards magenta.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-1, 1, 0.01, 0.1, 2)]
    public float Tint { get; set; }

    /// <summary>
    /// Contrast around middle grey (in logarithmic space).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 3, 0.01, 0.1, 2)]
    public float Contrast { get; set; } = 1.0f;

    /// <summary>
    /// Saturation of the colors (0 grey).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 3, 0.01, 0.1, 2)]
    public float Saturation { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the image (a color filter).
    /// </summary>
    [DataMember(50)]
    public Color3 Filter { get; set; } = new(1.0f);

    /// <summary>
    /// Tint of the shadows.
    /// </summary>
    [DataMember(60)]
    public Color3 Shadows { get; set; } = new(1.0f);

    /// <summary>
    /// Tint of the highlights.
    /// </summary>
    [DataMember(70)]
    public Color3 Highlights { get; set; } = new(1.0f);

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxGradingSettings)target;
        Temperature = MathUtil.Lerp(Temperature, t.Temperature, amount);
        Tint = MathUtil.Lerp(Tint, t.Tint, amount);
        Contrast = MathUtil.Lerp(Contrast, t.Contrast, amount);
        Saturation = MathUtil.Lerp(Saturation, t.Saturation, amount);
        Filter = Color3.Lerp(Filter, t.Filter, amount);
        Shadows = Color3.Lerp(Shadows, t.Shadows, amount);
        Highlights = Color3.Lerp(Highlights, t.Highlights, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxGradingSettings)source;
        Temperature = s.Temperature;
        Tint = s.Tint;
        Contrast = s.Contrast;
        Saturation = s.Saturation;
        Filter = s.Filter;
        Shadows = s.Shadows;
        Highlights = s.Highlights;
    }
}

/// <summary>
/// Imperfections of the camera: vignetting, chromatic aberration, film grain.
/// </summary>
[DataContract("PostFxLensSettings")]
public sealed class PostFxLensSettings : PostFxSettingsGroup
{
    /// <summary>
    /// Darkening towards the corners (natural vignetting: the fall of the light with the angle).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.25f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Vignette { get; set; } = 0.25f;

    /// <summary>
    /// Colors fringing towards the edges of the image (lateral chromatic aberration).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float ChromaticAberration { get; set; }

    /// <summary>
    /// Strength of the film grain.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float FilmGrain { get; set; }

    /// <summary>
    /// How much the grain fades in the highlights (1: only in the shadows).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float FilmGrainResponse { get; set; } = 0.8f;

    internal override void BlendTowards(PostFxSettingsGroup target, float amount)
    {
        var t = (PostFxLensSettings)target;
        Vignette = MathUtil.Lerp(Vignette, t.Vignette, amount);
        ChromaticAberration = MathUtil.Lerp(ChromaticAberration, t.ChromaticAberration, amount);
        FilmGrain = MathUtil.Lerp(FilmGrain, t.FilmGrain, amount);
        FilmGrainResponse = MathUtil.Lerp(FilmGrainResponse, t.FilmGrainResponse, amount);
    }

    internal override void CopyFrom(PostFxSettingsGroup source)
    {
        var s = (PostFxLensSettings)source;
        Vignette = s.Vignette;
        ChromaticAberration = s.ChromaticAberration;
        FilmGrain = s.FilmGrain;
        FilmGrainResponse = s.FilmGrainResponse;
    }
}

/// <summary>
/// The post-processing settings of a volume (or the result of blending the volumes around the camera).
/// </summary>
[DataContract("PostFxProfile")]
public sealed class PostFxProfile
{
    [DataMember(10)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxExposureSettings Exposure { get; set; } = new();

    [DataMember(20)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxBloomSettings Bloom { get; set; } = new();

    [DataMember(30)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxGradingSettings Grading { get; set; } = new();

    [DataMember(40)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxLensSettings Lens { get; set; } = new();

    [DataMember(50)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxAmbientOcclusionSettings AmbientOcclusion { get; set; } = new();

    [DataMember(60)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxContactShadowSettings ContactShadows { get; set; } = new();

    [DataMember(70)]
    [NotNull]
    [Display(Expand = ExpandRule.Once)]
    public PostFxScreenSpaceLightSettings ScreenSpaceLight { get; set; } = new();

    internal PostFxSettingsGroup[] Groups => [Exposure, Bloom, Grading, Lens, AmbientOcclusion, ContactShadows, ScreenSpaceLight];

    /// <summary>
    /// Copies the values of another profile (the override flags stay).
    /// </summary>
    internal void CopyFrom(PostFxProfile source)
    {
        var groups = Groups;
        var sources = source.Groups;
        for (int i = 0; i < groups.Length; i++)
            groups[i].CopyFrom(sources[i]);
    }

    /// <summary>
    /// Moves the groups the other profile overrides towards its values.
    /// </summary>
    internal void BlendTowards(PostFxProfile target, float amount)
    {
        var groups = Groups;
        var targets = target.Groups;
        for (int i = 0; i < groups.Length; i++)
        {
            if (targets[i].Override)
                groups[i].BlendTowards(targets[i], amount);
        }
    }
}

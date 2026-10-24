// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Atmosphere.Weather;

/// <summary>
/// The state of the weather: how much of the sky each cloud layer covers and with which clouds, the wind, the haze and the
/// fog. The look of the clouds (altitudes, shapes, lighting) is set by the clouds component; this is what changes with the
/// weather.
/// </summary>
[DataContract("WeatherState")]
public sealed class WeatherState
{
    /// <summary>
    /// Share of the sky the low clouds cover (0 none at all, 1 overcast).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.35f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float CloudCoverage { get; set; } = 0.35f;

    /// <summary>
    /// Kind of low clouds: 0 stratus (flat sheets), 0.25 stratocumulus, 0.5 cumulus, 0.75 towering cumulus, 1 cumulonimbus
    /// (storm towers with anvils).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float CloudType { get; set; } = 0.5f;

    /// <summary>
    /// Share of the sky the middle clouds cover.
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.15f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float MidCloudCoverage { get; set; } = 0.15f;

    /// <summary>
    /// Kind of middle clouds: 0 altostratus (a grey sheet), 1 altocumulus (fields of small cloudlets).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.7f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float MidCloudType { get; set; } = 0.7f;

    /// <summary>
    /// Share of the sky the high clouds cover.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float HighCloudCoverage { get; set; } = 0.3f;

    /// <summary>
    /// Kind of high clouds: 0 cirrostratus (a milky veil with a halo around the sun), 1 cirrus (fibrous streaks).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.8f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float HighCloudType { get; set; } = 0.8f;

    /// <summary>
    /// Contrails of aircraft across the high sky (0 none, 1 a busy sky): fresh thin lines that spread into ribbons of
    /// cirrus as they age, where the air up there is moist.
    /// </summary>
    [DataMember(65)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float Contrails { get; set; }

    /// <summary>
    /// Rain from the low clouds (0-1): lower, darker and ragged bases.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float Precipitation { get; set; }

    /// <summary>
    /// Direction the wind blows towards, degrees clockwise (seen from above) from +X.
    /// </summary>
    [DataMember(80)]
    [DefaultValue(30.0f)]
    [DataMemberRange(-360, 360, 1, 15, 0)]
    public float WindDirection { get; set; } = 30.0f;

    /// <summary>
    /// Wind speed at the altitude of the clouds, meters per second.
    /// </summary>
    [DataMember(90)]
    [DefaultValue(10.0f)]
    [DataMemberRange(0, 100, 0.5, 5, 1)]
    public float WindSpeed { get; set; } = 10.0f;

    /// <summary>
    /// Multiplies the haze of the atmosphere (its aerosols): 1 clear, more for a milky, humid sky.
    /// </summary>
    [DataMember(100)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 20, 0.05, 0.5, 2)]
    public float Haze { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the density of the scene's fog.
    /// </summary>
    [DataMember(110)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 20, 0.05, 0.5, 2)]
    public float FogDensity { get; set; } = 1.0f;

    public WeatherState Clone() => (WeatherState)MemberwiseClone();

    /// <summary>
    /// Copies another state into this one.
    /// </summary>
    public void CopyFrom(WeatherState other)
    {
        CloudCoverage = other.CloudCoverage;
        CloudType = other.CloudType;
        MidCloudCoverage = other.MidCloudCoverage;
        MidCloudType = other.MidCloudType;
        HighCloudCoverage = other.HighCloudCoverage;
        HighCloudType = other.HighCloudType;
        Contrails = other.Contrails;
        Precipitation = other.Precipitation;
        WindDirection = other.WindDirection;
        WindSpeed = other.WindSpeed;
        Haze = other.Haze;
        FogDensity = other.FogDensity;
    }

    /// <summary>
    /// Blends two states into this one (the wind direction along the shorter way around).
    /// </summary>
    public void Lerp(WeatherState from, WeatherState to, float amount)
    {
        CloudCoverage = MathUtil.Lerp(from.CloudCoverage, to.CloudCoverage, amount);
        CloudType = MathUtil.Lerp(from.CloudType, to.CloudType, amount);
        MidCloudCoverage = MathUtil.Lerp(from.MidCloudCoverage, to.MidCloudCoverage, amount);
        MidCloudType = MathUtil.Lerp(from.MidCloudType, to.MidCloudType, amount);
        HighCloudCoverage = MathUtil.Lerp(from.HighCloudCoverage, to.HighCloudCoverage, amount);
        HighCloudType = MathUtil.Lerp(from.HighCloudType, to.HighCloudType, amount);
        Contrails = MathUtil.Lerp(from.Contrails, to.Contrails, amount);
        Precipitation = MathUtil.Lerp(from.Precipitation, to.Precipitation, amount);
        var turn = to.WindDirection - from.WindDirection;
        turn -= MathF.Round(turn / 360.0f) * 360.0f;
        WindDirection = from.WindDirection + turn * amount;
        WindSpeed = MathUtil.Lerp(from.WindSpeed, to.WindSpeed, amount);
        Haze = MathUtil.Lerp(from.Haze, to.Haze, amount);
        FogDensity = MathUtil.Lerp(from.FogDensity, to.FogDensity, amount);
    }

    /// <summary>
    /// The state of a preset (a new instance; <see cref="WeatherPreset.Custom"/> gives the defaults).
    /// </summary>
    public static WeatherState FromPreset(WeatherPreset preset) => preset switch
    {
        WeatherPreset.Clear => new() { CloudCoverage = 0.0f, MidCloudCoverage = 0.0f, HighCloudCoverage = 0.12f, HighCloudType = 0.9f, Contrails = 0.3f, WindSpeed = 6.0f, Haze = 0.8f },
        WeatherPreset.Fair => new() { CloudCoverage = 0.22f, CloudType = 0.42f, MidCloudCoverage = 0.05f, HighCloudCoverage = 0.3f, HighCloudType = 0.85f, WindSpeed = 8.0f },
        WeatherPreset.Scattered => new() { CloudCoverage = 0.45f, CloudType = 0.55f, MidCloudCoverage = 0.25f, MidCloudType = 0.8f, HighCloudCoverage = 0.35f, WindSpeed = 10.0f },
        WeatherPreset.Towering => new() { CloudCoverage = 0.42f, CloudType = 0.88f, MidCloudCoverage = 0.15f, HighCloudCoverage = 0.45f, HighCloudType = 0.6f, WindSpeed = 12.0f, Haze = 1.3f },
        WeatherPreset.Mackerel => new() { CloudCoverage = 0.1f, CloudType = 0.35f, MidCloudCoverage = 0.7f, MidCloudType = 1.0f, HighCloudCoverage = 0.4f, HighCloudType = 0.9f, WindSpeed = 14.0f },
        WeatherPreset.Overcast => new() { CloudCoverage = 0.95f, CloudType = 0.18f, MidCloudCoverage = 0.8f, MidCloudType = 0.1f, HighCloudCoverage = 0.0f, WindSpeed = 9.0f, Haze = 1.8f, FogDensity = 1.5f },
        WeatherPreset.Storm => new() { CloudCoverage = 0.97f, CloudType = 0.8f, MidCloudCoverage = 0.95f, MidCloudType = 0.0f, HighCloudCoverage = 0.2f, HighCloudType = 0.3f, Precipitation = 1.0f, WindSpeed = 18.0f, Haze = 2.5f, FogDensity = 2.0f },
        WeatherPreset.Broken => new() { CloudCoverage = 0.25f, CloudType = 0.3f, MidCloudCoverage = 0.95f, MidCloudType = 0.55f, HighCloudCoverage = 0.25f, HighCloudType = 0.7f, WindSpeed = 11.0f, Haze = 1.2f },
        WeatherPreset.Veiled => new() { CloudCoverage = 0.05f, CloudType = 0.4f, MidCloudCoverage = 0.0f, HighCloudCoverage = 0.97f, HighCloudType = 0.1f, Contrails = 0.8f, WindSpeed = 9.0f, Haze = 1.9f },
        WeatherPreset.Foggy => new() { CloudCoverage = 0.75f, CloudType = 0.05f, MidCloudCoverage = 0.4f, MidCloudType = 0.0f, HighCloudCoverage = 0.0f, WindSpeed = 3.0f, Haze = 3.0f, FogDensity = 4.0f },
        _ => new(),
    };
}

/// <summary>
/// Weathers ready to use.
/// </summary>
public enum WeatherPreset
{
    /// <summary>
    /// The weather given by the component's own state.
    /// </summary>
    Custom,

    /// <summary>
    /// A clear sky with a few wisps of cirrus.
    /// </summary>
    Clear,

    /// <summary>
    /// Fair weather cumulus and cirrus.
    /// </summary>
    Fair,

    /// <summary>
    /// Scattered cumulus at several levels.
    /// </summary>
    Scattered,

    /// <summary>
    /// Towering cumulus and distant thunderheads with anvils: dramatic skies.
    /// </summary>
    Towering,

    /// <summary>
    /// A mackerel sky: fields of altocumulus under cirrus.
    /// </summary>
    Mackerel,

    /// <summary>
    /// A grey deck of stratus under altostratus.
    /// </summary>
    Overcast,

    /// <summary>
    /// Cumulonimbus with low, dark, ragged bases and rain.
    /// </summary>
    Storm,

    /// <summary>
    /// Low stratus, haze and thick fog.
    /// </summary>
    Foggy,

    /// <summary>
    /// A broken deck of altocumulus and stratocumulus over the whole sky: clumps and fragments with sky between, glowing at
    /// dusk.
    /// </summary>
    Broken,

    /// <summary>
    /// A milky veil of cirrostratus with fibres and contrails, a hazy glow around the sun.
    /// </summary>
    Veiled,
}

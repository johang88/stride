// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Atmosphere.Weather;

/// <summary>
/// The weather of a scene: the cloud cover, the wind, the haze and the fog, from a preset or a state of its own. Games
/// change it at run time with <see cref="SetWeather(WeatherPreset, float)"/>, blending over a transition. One per scene
/// (the first enabled one is used); without one the weather is <see cref="WeatherPreset.Fair"/>.
/// </summary>
[DataContract("WeatherComponent")]
[Display("Weather", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(WeatherProcessor))]
[ComponentCategory("Environment")]
[ComponentOrder(12020)]
public sealed class WeatherComponent : ActivableEntityComponent
{
    private readonly WeatherState current = new();
    private WeatherState transitionFrom;
    private WeatherState transitionTo;
    private float transitionDuration;
    private float transitionTime;

    /// <summary>
    /// The weather when the scene starts (<see cref="WeatherPreset.Custom"/>: <see cref="State"/>).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(WeatherPreset.Fair)]
    public WeatherPreset Preset { get; set; } = WeatherPreset.Fair;

    /// <summary>
    /// The weather of the <see cref="WeatherPreset.Custom"/> preset.
    /// </summary>
    [DataMember(20)]
    public WeatherState State { get; set; } = new();

    /// <summary>
    /// The weather now (blended during a transition).
    /// </summary>
    [DataMemberIgnore]
    public WeatherState Current
    {
        get
        {
            if (transitionTo == null)
                current.CopyFrom(Preset == WeatherPreset.Custom ? State ?? new WeatherState() : PresetState(Preset));
            return current;
        }
    }

    /// <summary>
    /// Whether a transition is in progress.
    /// </summary>
    [DataMemberIgnore]
    public bool IsChanging => transitionTo != null && transitionTime < transitionDuration;

    /// <summary>
    /// Changes the weather to a preset over a number of seconds (0: at once).
    /// </summary>
    public void SetWeather(WeatherPreset preset, float transitionSeconds)
    {
        SetWeather(preset == WeatherPreset.Custom ? State : WeatherState.FromPreset(preset), transitionSeconds);
    }

    /// <summary>
    /// Changes the weather to a state over a number of seconds (0: at once). The state is copied.
    /// </summary>
    public void SetWeather(WeatherState state, float transitionSeconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        transitionFrom = Current.Clone();
        transitionTo = state.Clone();
        transitionDuration = Math.Max(transitionSeconds, 0.0f);
        transitionTime = 0.0f;
        Advance(0.0f);
    }

    /// <summary>
    /// Advances a transition by elapsed seconds.
    /// </summary>
    internal void Advance(float seconds)
    {
        if (transitionTo == null)
            return;
        transitionTime += seconds;
        var amount = transitionDuration > 0.0f ? Math.Clamp(transitionTime / transitionDuration, 0.0f, 1.0f) : 1.0f;
        // Eased: weather fronts arrive and settle gradually
        amount = amount * amount * (3.0f - 2.0f * amount);
        current.Lerp(transitionFrom, transitionTo, amount);
    }

    private static readonly WeatherState[] Presets = CreatePresets();

    private static WeatherState PresetState(WeatherPreset preset) => Presets[(int)preset];

    private static WeatherState[] CreatePresets()
    {
        var values = Enum.GetValues<WeatherPreset>();
        var states = new WeatherState[values.Length];
        foreach (var value in values)
            states[(int)value] = WeatherState.FromPreset(value);
        return states;
    }
}

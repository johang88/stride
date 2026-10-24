// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Engine;
using Stride.Games;
using Stride.Rendering;

namespace Stride.Atmosphere.Weather;

/// <summary>
/// Advances the weather transitions and hands the weather of the first enabled <see cref="WeatherComponent"/> to the
/// atmosphere renderers.
/// </summary>
public sealed class WeatherProcessor : EntityProcessor<WeatherComponent>, IEntityComponentRenderProcessor
{
    private readonly WeatherState weather = new();

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override void OnSystemRemove()
    {
        VisibilityGroup?.Tags.Set(AtmosphereRenderer.CurrentWeather, null);
        base.OnSystemRemove();
    }

    public override void Update(GameTime time)
    {
        var seconds = (float)time.Elapsed.TotalSeconds;
        foreach (var component in ComponentDatas.Keys)
            component.Advance(seconds);
    }

    public override void Draw(RenderContext context)
    {
        WeatherComponent active = null;
        foreach (var component in ComponentDatas.Keys)
        {
            if (component.Enabled)
            {
                active = component;
                break;
            }
        }

        if (active == null)
        {
            VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentWeather, null);
            return;
        }
        weather.CopyFrom(active.Current);
        VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentWeather, weather);
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Atmosphere.Weather;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Skyboxes;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// Hands the sky of the first enabled <see cref="AtmosphereComponent"/> to the atmosphere renderers, advances its time of
/// day, and adjusts the lights of the scene after the <see cref="LightProcessor"/>: the sun light points from the sun (the
/// moon at night) and gets the color of the light through the atmosphere, and the sky light joins them.
/// </summary>
public sealed class AtmosphereProcessor : EntityProcessor<AtmosphereComponent, AtmosphereProcessor.Data>, IEntityComponentRenderProcessor
{
    // The sun is the key light down to this elevation (radians), the moon below it (the sunlight is gone by then)
    private const float KeyLightSwitchElevation = -0.06f;

    private LightProcessor lightProcessor;

    public AtmosphereProcessor()
    {
        // After the light processor has prepared the lights of the frame
        Order = 100;
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override Data GenerateComponentData(Entity entity, AtmosphereComponent component)
    {
        var data = new Data();
        data.SkyLight = new RenderLight { Type = new LightSkybox { Skybox = data.Render.Skybox }, WorldMatrix = Matrix.Identity };
        return data;
    }

    protected override bool IsAssociatedDataValid(Entity entity, AtmosphereComponent component, Data associatedData) => true;

    protected override void OnSystemRemove()
    {
        VisibilityGroup?.Tags.Set(AtmosphereRenderer.CurrentAtmosphere, null);
        base.OnSystemRemove();
    }

    public override void Update(GameTime time)
    {
        var seconds = (float)time.Elapsed.TotalSeconds;
        foreach (var component in ComponentDatas.Keys)
        {
            if (component.Enabled && component.TimeOfDay is { Enabled: true } timeOfDay)
                timeOfDay.Advance(seconds);
        }
    }

    public override void Draw(RenderContext context)
    {
        AtmosphereComponent active = null;
        Data data = null;
        foreach (var (component, componentData) in ComponentDatas)
        {
            if (component.Enabled)
            {
                active = component;
                data = componentData;
                break;
            }
        }

        if (active == null)
        {
            VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentAtmosphere, null);
            return;
        }

        lightProcessor ??= EntityManager.GetProcessor<LightProcessor>();
        var lights = lightProcessor?.Lights;
        var sun = active.Sun != null ? lightProcessor?.GetRenderLight(active.Sun) : FindBrightestDirectional(lights);
        // (a light the light processor skipped this frame: disabled)
        if (sun != null && lights != null && !lights.Contains(sun))
            sun = null;

        var render = data.Render;
        var atmosphere = active.Atmosphere ?? new AtmosphereParameters();
        render.Parameters = atmosphere;
        render.SeaLevel = active.SeaLevel;
        render.AerialPerspectiveScale = active.AerialPerspectiveScale;
        render.SunDiskSize = MathUtil.DegreesToRadians(active.SunDiskSize);
        render.SunDiskIntensity = active.SunDiskIntensity;
        render.MoonDiskSize = MathUtil.DegreesToRadians(active.MoonDiskSize);
        render.MoonDiskIntensity = active.MoonDiskIntensity;
        render.StarBrightness = active.StarBrightness;
        render.NightSkyBrightness = active.NightSkyBrightness;
        render.KeyLight = sun;

        // The sunlight above the atmosphere: the light's own color and intensity (as the light processor computed them) are
        // the sunlight on the ground with the sun overhead, so a white light stays white at noon and reddens towards sunset
        var haze = VisibilityGroup.Tags.Get(AtmosphereRenderer.CurrentWeather)?.Haze ?? 1.0f;
        var overhead = RenderAtmosphere.ComputeTransmittance(atmosphere, haze, 0.05f, Vector3.UnitY);
        var sunIlluminance = (Color3)((Vector3)(sun?.Color ?? new Color3(10.0f)) / Vector3.Max(overhead, new Vector3(0.05f)));
        var timeOfDay = active.TimeOfDay is { Enabled: true } ? active.TimeOfDay : null;
        if (timeOfDay != null)
        {
            render.SunDirection = timeOfDay.ComputeSunDirection();
            render.MoonDirection = timeOfDay.ComputeMoonDirection();
            // Lit fraction of the disk
            var lit = 0.5f * (1.0f - MathF.Cos(timeOfDay.MoonPhase * 2.0f * MathF.PI));
            var moonTint = (Vector3)active.MoonColor;
            var tintLuminance = Math.Max(Vector3.Dot(moonTint, new Vector3(0.2126f, 0.7152f, 0.0722f)), 1e-4f);
            render.FullMoonIlluminance = (Color3)((Vector3)sunIlluminance * moonTint * (active.MoonBrightness / tintLuminance));
            render.MoonIlluminance = render.FullMoonIlluminance * lit;
            render.StarRotation = timeOfDay.ComputeStarRotation();
        }
        else
        {
            render.SunDirection = sun != null ? -Vector3.Normalize(sun.Direction) : Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.4f));
            render.MoonDirection = -render.SunDirection;
            render.MoonIlluminance = new Color3();
            render.FullMoonIlluminance = new Color3();
            render.StarRotation = Matrix.Identity;
        }
        render.SunIlluminance = sunIlluminance;
        var sunElevation = MathF.Asin(Math.Clamp(render.SunDirection.Y, -1.0f, 1.0f));
        render.Night = MathUtil.SmoothStep(Math.Clamp((-sunElevation - 0.05f) / 0.25f, 0.0f, 1.0f));

        // The key light: the sun, or the moon once the sun is down (fading in, as the twilight darkens)
        if (sun != null)
        {
            var useMoon = timeOfDay != null && sunElevation < KeyLightSwitchElevation;
            var direction = useMoon ? render.MoonDirection : render.SunDirection;
            var illuminance = useMoon ? render.MoonIlluminance * MathUtil.SmoothStep(Math.Clamp((KeyLightSwitchElevation - sunElevation) / 0.12f, 0.0f, 1.0f)) : sunIlluminance;
            if (timeOfDay != null)
                sun.Direction = -direction;
            if (active.TintSunLight)
            {
                var transmittance = RenderAtmosphere.ComputeTransmittance(atmosphere, haze, 0.05f, direction);
                illuminance *= (Color3)transmittance;
            }
            sun.Color = illuminance;
        }

        // The light of the sky (once the renderer has captured it)
        if (lights != null && active.SkyLightIntensity > 0.0f && render.Skybox.SpecularLightingParameters.Get(SkyboxKeys.CubeMap) != null)
        {
            data.SkyLight.Intensity = active.SkyLightIntensity;
            data.SkyLight.Type.Update(data.SkyLight);
            lights.Add(data.SkyLight);
        }

        VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentAtmosphere, render);
    }

    private static RenderLight FindBrightestDirectional(RenderLightCollection lights)
    {
        RenderLight brightest = null;
        if (lights == null)
            return null;
        foreach (var light in lights)
        {
            if (light.Type is LightDirectional && (brightest == null || ((Vector3)light.Color).LengthSquared() > ((Vector3)brightest.Color).LengthSquared()))
                brightest = light;
        }
        return brightest;
    }

    public sealed class Data
    {
        public readonly RenderAtmosphere Render = new() { Skybox = new Skybox() };

        public RenderLight SkyLight;
    }
}

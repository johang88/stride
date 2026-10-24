// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Skyboxes;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// The sky of a scene as the atmosphere renderer draws it (from its <see cref="AtmosphereComponent"/>).
/// </summary>
public sealed class RenderAtmosphere
{
    public AtmosphereParameters Parameters;

    /// <summary>
    /// Direction towards the sun and its illuminance above the atmosphere.
    /// </summary>
    public Vector3 SunDirection;

    public Color3 SunIlluminance;

    /// <summary>
    /// Direction towards the moon and its illuminance above the atmosphere (zero without a moon).
    /// </summary>
    public Vector3 MoonDirection;

    public Color3 MoonIlluminance;

    /// <summary>
    /// The directional light of the scene's sun (lit by the moon at night), or null.
    /// </summary>
    public RenderLight KeyLight;

    /// <summary>
    /// The light of the sky on the surfaces: the renderer captures the sky into it.
    /// </summary>
    public Skybox Skybox;

    public float SeaLevel;

    public float AerialPerspectiveScale;

    public float SunDiskSize;

    public float SunDiskIntensity;

    public float MoonDiskSize;

    public float StarBrightness;

    public float NightSkyBrightness;

    /// <summary>
    /// 0 by day to 1 at night (the sun well below the horizon).
    /// </summary>
    public float Night;

    public Matrix StarRotation;

    /// <summary>
    /// Transmittance of the atmosphere from an altitude (km) towards a direction, to the top of the atmosphere (zero when the
    /// planet hides it), with the aerosols multiplied by <paramref name="haze"/>. Mirrors the GPU transmittance table.
    /// </summary>
    public static Vector3 ComputeTransmittance(AtmosphereParameters atmosphere, float haze, float altitudeKm, Vector3 direction, int steps = 40)
    {
        var bottom = atmosphere.PlanetRadius;
        var top = bottom + atmosphere.Height;
        var origin = new Vector3(0, bottom + Math.Max(altitudeKm, 0.001f), 0);
        direction.Normalize();

        // Fades out over a small angle as the planet hides it, like the GPU's
        var height = origin.Y;
        var cosHorizon = -MathF.Sqrt(Math.Max(0, 1 - bottom * bottom / (height * height)));
        var visible = MathUtil.SmoothStep(Math.Clamp((direction.Y - (cosHorizon - 0.004f)) / 0.008f, 0, 1));
        if (visible <= 0)
            return Vector3.Zero;

        var tMax = RaySphereFar(origin, direction, top);
        if (tMax <= 0)
            return Vector3.One;

        var mieExtinction = (atmosphere.MieScattering * haze + atmosphere.MieAbsorption);
        var opticalDepth = Vector3.Zero;
        var dt = tMax / steps;
        for (int i = 0; i < steps; i++)
        {
            var p = origin + direction * ((i + 0.5f) * dt);
            var altitude = p.Length() - bottom;
            var rayleigh = MathF.Exp(-altitude / atmosphere.RayleighScaleHeight);
            var mie = MathF.Exp(-altitude / atmosphere.MieScaleHeight);
            var ozone = Math.Max(0, altitude < 25 ? altitude / 15 - 2.0f / 3.0f : -altitude / 15 + 8.0f / 3.0f);
            opticalDepth += (atmosphere.RayleighScattering * rayleigh + new Vector3(mieExtinction * mie) + atmosphere.OzoneAbsorption * ozone) * dt;
        }
        return new Vector3(MathF.Exp(-opticalDepth.X), MathF.Exp(-opticalDepth.Y), MathF.Exp(-opticalDepth.Z)) * visible;
    }

    private static float RaySphereFar(Vector3 origin, Vector3 direction, float radius)
    {
        var b = Vector3.Dot(origin, direction);
        var c = Vector3.Dot(origin, origin) - radius * radius;
        var delta = b * b - c;
        return delta < 0 ? -1 : -b + MathF.Sqrt(delta);
    }
}

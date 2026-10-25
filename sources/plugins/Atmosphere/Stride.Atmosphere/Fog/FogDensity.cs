// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// The density settings of the fogs: an amount in [0, 1] cubed into extinction per meter (light crossing d meters of fog keeps
/// exp(-extinction × d) of its intensity). Haze to thick fog spans orders of magnitude of extinction, which a linear slider
/// crowds into its first percent; cubed, the amount reads evenly.
/// </summary>
public static class FogDensity
{
    /// <summary>
    /// Extinction of the height fog at density 1 (per meter): about 40 m of visibility. 0.25 is a clear day's haze (2.5 km),
    /// 0.35 hazy (900 m), 0.5 mist (300 m), 0.75 fog (90 m).
    /// </summary>
    public const float HeightFogExtinction = 0.1f;

    /// <summary>
    /// Extinction of fog volumes and painted fog at density 1 (per meter): about 4 m of visibility. 0.25 is a thin mist (250 m),
    /// 0.5 a ground fog (30 m), 0.75 a thick bank (9 m).
    /// </summary>
    public const float LocalFogExtinction = 1.0f;

    /// <summary>
    /// The extinction per meter of a density setting.
    /// </summary>
    /// <param name="density">The setting, in [0, 1].</param>
    /// <param name="extinction">The extinction at density 1.</param>
    public static float ToExtinction(float density, float extinction)
    {
        var amount = Math.Max(density, 0.0f);
        return extinction * amount * amount * amount;
    }
}

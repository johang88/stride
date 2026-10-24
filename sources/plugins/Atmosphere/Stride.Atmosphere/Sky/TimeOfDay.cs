// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// Where the sun, the moon and the stars are, from the time of day, the day of the year and the latitude. When enabled
/// it turns the sun light of the <see cref="AtmosphereComponent"/>: towards the sun by day, the moon by night.
/// </summary>
[DataContract("TimeOfDay")]
public sealed class TimeOfDay
{
    /// <summary>
    /// Whether the time of day places the sun and the moon (otherwise the sun is where its light points, and there is no moon).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(false)]
    public bool Enabled { get; set; }

    /// <summary>
    /// Local solar time in hours (0-24; noon is 12).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(10.0f)]
    [DataMemberRange(0, 24, 0.05, 1, 2)]
    public float Hours { get; set; } = 10.0f;

    /// <summary>
    /// Game hours per real second the time advances by (0: stopped).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.0f)]
    [DataMemberRange(0, 24, 0.001, 0.01, 3)]
    public float Speed { get; set; }

    /// <summary>
    /// Day of the year (1-365), for the sun's declination: high summer suns, low winter ones.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(172)]
    [DataMemberRange(1, 365, 1, 10, 0)]
    public int DayOfYear { get; set; } = 172;

    /// <summary>
    /// Latitude in degrees (positive north).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(48.0f)]
    [DataMemberRange(-89, 89, 0.5, 5, 1)]
    public float Latitude { get; set; } = 48.0f;

    /// <summary>
    /// Direction of north in the world, in degrees clockwise (seen from above) from -Z.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-180, 180, 1, 15, 0)]
    public float North { get; set; }

    /// <summary>
    /// Phase of the moon: 0 new, 0.25 first quarter, 0.5 full, 0.75 last quarter.
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.01, 0.05, 2)]
    public float MoonPhase { get; set; } = 0.5f;

    /// <summary>
    /// Direction towards the sun (world space).
    /// </summary>
    public Vector3 ComputeSunDirection() => ToWorld(HorizontalDirection(SolarDeclination(), HourAngle()));

    /// <summary>
    /// Direction towards the moon (world space): it trails the sun by its phase (opposite to it when full), on the
    /// declination of the sun at that point of the year.
    /// </summary>
    public Vector3 ComputeMoonDirection()
    {
        var phaseAngle = MoonPhase * 2.0f * MathF.PI;
        var declination = SolarDeclination() * MathF.Cos(phaseAngle) + MathUtil.DegreesToRadians(5.1f) * MathF.Sin(phaseAngle);
        return ToWorld(HorizontalDirection(declination, HourAngle() - phaseAngle));
    }

    /// <summary>
    /// Rotation of world directions into the frame of the stars, turning around the celestial pole with the sidereal time.
    /// </summary>
    public Matrix ComputeStarRotation()
    {
        var latitude = MathUtil.DegreesToRadians(Latitude);
        // The pole: north, raised by the latitude
        var pole = ToWorld(new Vector3(0, MathF.Sin(latitude), MathF.Cos(latitude)));
        var sidereal = (Hours / 24.0f + DayOfYear / 365.25f) * 2.0f * MathF.PI;
        return Matrix.RotationAxis(pole, sidereal);
    }

    /// <summary>
    /// Advances the time by elapsed real seconds at <see cref="Speed"/> (wrapping around midnight).
    /// </summary>
    public void Advance(float seconds)
    {
        if (Speed <= 0.0f)
            return;
        var hours = Hours + Speed * seconds;
        Hours = hours - MathF.Floor(hours / 24.0f) * 24.0f;
    }

    private float SolarDeclination() => -MathUtil.DegreesToRadians(23.44f) * MathF.Cos(2.0f * MathF.PI * (DayOfYear + 10) / 365.0f);

    private float HourAngle() => MathUtil.DegreesToRadians(15.0f * (Hours - 12.0f));

    // Local horizontal frame: x = east, y = up, z = north
    private Vector3 HorizontalDirection(float declination, float hourAngle)
    {
        var latitude = MathUtil.DegreesToRadians(Latitude);
        var sinLatitude = MathF.Sin(latitude);
        var cosLatitude = MathF.Cos(latitude);
        var sinDeclination = MathF.Sin(declination);
        var cosDeclination = MathF.Cos(declination);
        var up = sinLatitude * sinDeclination + cosLatitude * cosDeclination * MathF.Cos(hourAngle);
        var east = -cosDeclination * MathF.Sin(hourAngle);
        var north = cosLatitude * sinDeclination - sinLatitude * cosDeclination * MathF.Cos(hourAngle);
        return Vector3.Normalize(new Vector3(east, up, north));
    }

    // East is +X and north -Z before the rotation of north
    private Vector3 ToWorld(Vector3 horizontal)
    {
        var local = new Vector3(horizontal.X, horizontal.Y, -horizontal.Z);
        return Vector3.TransformNormal(local, Matrix.RotationY(-MathUtil.DegreesToRadians(North)));
    }
}

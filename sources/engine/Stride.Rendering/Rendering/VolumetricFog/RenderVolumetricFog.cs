// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering.VolumetricFog;

/// <summary>
/// The fog of a scene (from its volumetric fog component): an exponential height fog, the look of every fog medium
/// (anisotropy, light scattering) and the distance the volumetric fog covers.
/// </summary>
public sealed class RenderVolumetricFog
{
    /// <summary>
    /// Extinction per meter at <see cref="BaseHeight"/>.
    /// </summary>
    public float Density;

    /// <summary>
    /// World height where the height fog has its <see cref="Density"/>.
    /// </summary>
    public float BaseHeight;

    /// <summary>
    /// How fast the height fog thins out above <see cref="BaseHeight"/> (per meter; the density halves every ln(2) / falloff meters).
    /// </summary>
    public float HeightFalloff;

    /// <summary>
    /// Share of the light hitting the height fog that it scatters (the rest is absorbed), per color channel.
    /// </summary>
    public Color3 Albedo;

    /// <summary>
    /// Light the height fog emits per unit of extinction.
    /// </summary>
    public Color3 Emissive;

    /// <summary>
    /// Henyey-Greenstein anisotropy of every fog medium: 0 scatters evenly, towards 1 mostly forward (bright towards the sun).
    /// </summary>
    public float Anisotropy;

    /// <summary>
    /// How much the scattering is spread evenly (0-1), as light bouncing around in thick fog: brighter fog away from the lights.
    /// </summary>
    public float MultipleScattering;

    /// <summary>
    /// Average color of the ground: the sun and sky light it reflects up lights the fog from below.
    /// </summary>
    public Color3 GroundAlbedo;

    /// <summary>
    /// Multiplies the sun light the fog scatters.
    /// </summary>
    public float SunScattering;

    /// <summary>
    /// Multiplies the sky light the fog scatters.
    /// </summary>
    public float AmbientScattering;

    /// <summary>
    /// Multiplies the light of local point and spot lights the fog scatters.
    /// </summary>
    public float LocalLightScattering;

    /// <summary>
    /// Distance (view depth, meters) the volumetric fog covers: local volumes, painted fog, shadows and local lights end there,
    /// the height fog continues analytically.
    /// </summary>
    public float Distance;

    /// <summary>
    /// Strength (0-1) of the noise breaking up the height fog.
    /// </summary>
    public float NoiseStrength;

    /// <summary>
    /// Size of the noise features (meters).
    /// </summary>
    public float NoiseScale;

    /// <summary>
    /// Velocity the noise drifts with (meters per second).
    /// </summary>
    public Vector3 Wind;

    /// <summary>
    /// Whether the sky is fogged (as if it were at the camera's far plane).
    /// </summary>
    public bool FogSky;
}

/// <summary>
/// Shape of a <see cref="RenderFogVolume"/>.
/// </summary>
public enum FogVolumeShape
{
    Box,
    Sphere,
}

/// <summary>
/// A local fog volume: a box or an ellipsoid filled with fog.
/// </summary>
public sealed class RenderFogVolume
{
    /// <summary>
    /// World to the unit volume (a cube or sphere of half size 1 at the origin).
    /// </summary>
    public Matrix WorldToLocal;

    public BoundingBox Bounds;

    public FogVolumeShape Shape;

    /// <summary>
    /// Extinction per meter inside.
    /// </summary>
    public float Density;

    public Color3 Albedo;

    /// <summary>
    /// Light emitted per unit of extinction.
    /// </summary>
    public Color3 Emissive;

    /// <summary>
    /// Fraction of the volume (from its surface towards its center) over which the density fades in.
    /// </summary>
    public float EdgeFade;

    public float NoiseStrength;
}

/// <summary>
/// How a <see cref="RenderFogLayer"/> sets the top of its fog.
/// </summary>
public enum FogLayerHeightMode
{
    /// <summary>
    /// A height above the ground: the fog follows the terrain.
    /// </summary>
    [Display("Above ground")]
    AboveGround,

    /// <summary>
    /// A world altitude: the fog fills valleys up to it, like a lake.
    /// </summary>
    [Display("Up to altitude")]
    Altitude,
}

/// <summary>
/// Fog painted on a <see cref="RenderFogHeightfield"/>.
/// </summary>
public struct RenderFogLayer
{
    /// <summary>
    /// Extinction per meter where fully painted.
    /// </summary>
    public float Density;

    public FogLayerHeightMode HeightMode;

    /// <summary>
    /// Top of the fog: height above the ground or altitude (meters).
    /// </summary>
    public float Height;

    /// <summary>
    /// Depth under the top over which the fog thickens (meters).
    /// </summary>
    public float Softness;

    public Color3 Albedo;

    public Color3 Emissive;

    public float NoiseStrength;
}

/// <summary>
/// A height field (a terrain) with fog layers painted on it, and optionally the shadows it casts from the sun.
/// </summary>
public sealed class RenderFogHeightfield
{
    public const int MaxLayers = 4;

    /// <summary>
    /// World position of the corner at local (0, 0).
    /// </summary>
    public Vector3 Origin;

    /// <summary>
    /// Size of the height field along each side (meters).
    /// </summary>
    public float Size;

    /// <summary>
    /// R16 normalized heights, sample (x, z) at local (x, z) * <see cref="HeightCellSize"/>, with mip maps.
    /// </summary>
    public Texture Heightmap;

    public float HeightCellSize;

    /// <summary>
    /// Heights: local height of 0 and of 1.
    /// </summary>
    public float MinHeight;

    public float HeightRange;

    /// <summary>
    /// Layer weights (RGBA, one channel per layer), sample (x, z) at local (x, z) * <see cref="WeightCellSize"/>.
    /// </summary>
    public Texture LayerWeights;

    public float WeightCellSize;

    public int LayerCount;

    public readonly RenderFogLayer[] Layers = new RenderFogLayer[MaxLayers];

    /// <summary>
    /// Sun shadows of the height field: per texel over the height field, x = normalized height above which a point is lit,
    /// y = normalized distance to the occluder (null: none).
    /// </summary>
    public Texture SunShadow;

    public float SunShadowMinHeight;

    public float SunShadowHeightRange;

    /// <summary>
    /// Penumbra width (meters) per unit of normalized occluder distance.
    /// </summary>
    public float SunShadowPenumbra;
}

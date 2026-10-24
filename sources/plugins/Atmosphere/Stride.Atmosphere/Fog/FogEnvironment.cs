// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// What the atmosphere lights the fog of a view with: its sun, its sky, the shadows of its clouds, the weather's density
/// and the aerial perspective behind the fog. Without an atmosphere the fog finds the sun and the sky in the scene's lights.
/// </summary>
public sealed class FogEnvironment
{
    /// <summary>
    /// The sun light (its light through the atmosphere), or null for the brightest directional light.
    /// </summary>
    public RenderLight Sun;

    /// <summary>
    /// The prefiltered cube map of the sky (null: the scene's skybox light) and its intensity.
    /// </summary>
    public Texture SkyCubeMap;

    public float SkyIntensity = 1.0f;

    /// <summary>
    /// The sky's spherical harmonics and averages (see SkySphericalHarmonics), or null.
    /// </summary>
    public Buffer SkyLighting;

    /// <summary>
    /// Multiplies the density of the scene's fog (the weather).
    /// </summary>
    public float DensityScale = 1.0f;

    /// <summary>
    /// Transmittance of the sunlight through the clouds, top-down (null: none), its region (xy = corner, z = 1 / size,
    /// w = strength) and its light (xyz = towards the sun, w = sea level).
    /// </summary>
    public Texture CloudShadow;

    public Vector4 CloudShadowRegion;

    public Vector4 CloudShadowLight;

    /// <summary>
    /// The aerial perspective volume of the view (null: none) and its parameters (x = kilometers per slice, y = distance
    /// scale, z = 1).
    /// </summary>
    public Texture AerialPerspective;

    public Vector4 AerialPerspectiveParameters;
}

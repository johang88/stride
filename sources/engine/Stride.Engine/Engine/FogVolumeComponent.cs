// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine.Design;
using Stride.Engine.Processors;
using Stride.Rendering.VolumetricFog;

namespace Stride.Engine;

/// <summary>
/// A local volume of fog (box or ellipsoid, placed, rotated and scaled with its entity), drawn by the volumetric fog of the
/// forward renderer and lit like the scene's fog (see <see cref="VolumetricFogComponent"/>, which the scene needs).
/// </summary>
[DataContract("FogVolumeComponent")]
[Display("Fog volume", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(FogVolumeProcessor))]
[ComponentCategory("Lights")]
[ComponentOrder(12110)]
public sealed class FogVolumeComponent : ActivableEntityComponent
{
    [DataMember(10)]
    [DefaultValue(FogVolumeShape.Box)]
    public FogVolumeShape Shape { get; set; } = FogVolumeShape.Box;

    /// <summary>
    /// Size of the volume along its axes (meters, before the entity scale).
    /// </summary>
    [DataMember(20)]
    public Vector3 Size { get; set; } = new(20.0f, 6.0f, 20.0f);

    /// <summary>
    /// Density inside (extinction per meter).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(0.05f)]
    [DataMemberRange(0, 10, 0.005, 0.05, 3)]
    public float Density { get; set; } = 0.05f;

    /// <summary>
    /// Color of the light the fog scatters.
    /// </summary>
    [DataMember(40)]
    public Color3 Albedo { get; set; } = new(0.9f, 0.92f, 0.95f);

    /// <summary>
    /// Light the fog emits per unit of density (glowing mist).
    /// </summary>
    [DataMember(50)]
    public Color3 Emissive { get; set; }

    /// <summary>
    /// Fraction of the volume, from its surface inwards, over which the fog fades in.
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float EdgeFade { get; set; } = 0.4f;

    /// <summary>
    /// How much the noise of the scene's fog breaks up this fog (0-1).
    /// </summary>
    [DataMember(70)]
    [DefaultValue(0.5f)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float NoiseStrength { get; set; } = 0.5f;
}

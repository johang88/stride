// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// Turns the particles of the entity's particle system into volumetric smoke: each particle adds a soft, billowing
/// sphere of fog to the volumetric fog, lit like it (the sun with its shadows and light shafts, the sky, torches and
/// other local lights). Its sprites can still be drawn for fine detail, or hidden by giving the emitters no material.
/// The scene needs a volumetric fog component.
/// </summary>
[DataContract("VolumetricParticlesComponent")]
[Display("Volumetric particles", Expand = ExpandRule.Once)]
[DefaultEntityComponentRenderer(typeof(VolumetricParticlesProcessor))]
[ComponentCategory("Particles")]
[ComponentOrder(12130)]
public sealed class VolumetricParticlesComponent : ActivableEntityComponent
{
    /// <summary>
    /// Density at the center of a particle (extinction per meter).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0, 50, 0.01, 0.1, 3)]
    public float Density { get; set; } = 0.6f;

    /// <summary>
    /// Radius of a particle's smoke relative to its size.
    /// </summary>
    [DataMember(20)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0.05, 10, 0.01, 0.1, 2)]
    public float SizeScale { get; set; } = 0.6f;

    /// <summary>
    /// Color of the light the smoke scatters (the rest is absorbed: dark for soot, white for steam).
    /// </summary>
    [DataMember(30)]
    public Color3 Albedo { get; set; } = new(0.55f, 0.55f, 0.55f);

    /// <summary>
    /// Light the smoke emits per unit of density (embers, glowing gas).
    /// </summary>
    [DataMember(40)]
    public Color3 Emissive { get; set; }

    /// <summary>
    /// Whether the particles' colors tint the smoke (and their alpha its density): fading particles thin out.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(true)]
    public bool UseParticleColor { get; set; } = true;
}

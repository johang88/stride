// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles;
using Stride.Particles.Components;
using Stride.Rendering;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// The smoke particles of a scene as the volumetric fog draws them: per particle, three float4 (position and radius,
/// albedo and density, emission and a noise seed).
/// </summary>
public sealed class RenderFogParticles
{
    public const int Stride = 3;

    /// <summary>
    /// Most particles the fog draws.
    /// </summary>
    public const int MaxParticles = 1024;

    public readonly Vector4[] Data = new Vector4[MaxParticles * Stride];

    public int Count;
}

/// <summary>
/// Gathers the particles of the enabled <see cref="VolumetricParticlesComponent"/>s for the volumetric fog of the renderers.
/// </summary>
public sealed class VolumetricParticlesProcessor : EntityProcessor<VolumetricParticlesComponent, VolumetricParticlesProcessor.Data>, IEntityComponentRenderProcessor
{
    private readonly RenderFogParticles particles = new();

    public VolumetricParticlesProcessor() : base(typeof(ParticleSystemComponent))
    {
        // After the particle systems have moved their particles
        Order = 100;
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override Data GenerateComponentData(Entity entity, VolumetricParticlesComponent component)
    {
        return new Data { ParticleSystem = entity.Get<ParticleSystemComponent>() };
    }

    protected override bool IsAssociatedDataValid(Entity entity, VolumetricParticlesComponent component, Data associatedData)
    {
        return associatedData.ParticleSystem == entity.Get<ParticleSystemComponent>();
    }

    protected override void OnSystemAdd()
    {
        base.OnSystemAdd();
        VisibilityGroup.Tags.Set(VolumetricFogRenderer.CurrentParticles, particles);
    }

    protected override void OnSystemRemove()
    {
        VisibilityGroup?.Tags.Set(VolumetricFogRenderer.CurrentParticles, null);
        base.OnSystemRemove();
    }

    public override void Draw(RenderContext context)
    {
        particles.Count = 0;
        foreach (var (component, data) in ComponentDatas)
        {
            if (!component.Enabled || component.Density <= 0 || data.ParticleSystem is not { Enabled: true, ParticleSystem: { } system })
                continue;
            foreach (var emitter in system.Emitters)
            {
                if (!emitter.Enabled)
                    continue;
                Gather(component, system, emitter);
                if (particles.Count >= RenderFogParticles.MaxParticles)
                    return;
            }
        }
    }

    private void Gather(VolumetricParticlesComponent component, ParticleSystem system, ParticleEmitter emitter)
    {
        var pool = emitter.Pool;
        if (pool == null || pool.LivingParticles == 0 || !pool.FieldExists(ParticleFields.Position))
            return;
        var positionField = pool.GetField(ParticleFields.Position);
        var hasSize = pool.FieldExists(ParticleFields.Size);
        var sizeField = hasSize ? pool.GetField(ParticleFields.Size) : default;
        var hasColor = component.UseParticleColor && pool.FieldExists(ParticleFields.Color);
        var colorField = hasColor ? pool.GetField(ParticleFields.Color) : default;
        var local = emitter.SimulationSpace == EmitterSimulationSpace.Local;
        var albedo = (Vector3)component.Albedo;
        var emissive = (Vector3)component.Emissive;

        foreach (var particle in pool)
        {
            if (particles.Count >= RenderFogParticles.MaxParticles)
                return;
            var position = particle.Get(positionField);
            var size = hasSize ? particle.Get(sizeField) : 1.0f;
            if (local)
            {
                // The system's transform (as the emitter draws its local particles)
                system.Rotation.Rotate(ref position);
                position = position * system.UniformScale + system.Translation;
                size *= system.UniformScale;
            }
            var density = component.Density;
            var tint = albedo;
            if (hasColor)
            {
                var color = particle.Get(colorField);
                density *= Math.Clamp(color.A, 0, 1);
                tint *= new Vector3(color.R, color.G, color.B);
            }
            var radius = size * component.SizeScale;
            if (density <= 0 || radius <= 0)
                continue;

            var b = particles.Count * RenderFogParticles.Stride;
            particles.Data[b] = new Vector4(position, radius);
            particles.Data[b + 1] = new Vector4(tint, density);
            // (a seed per particle slot: its noise stays with it)
            particles.Data[b + 2] = new Vector4(emissive, (particle.Pointer.ToInt64() % 997) / 997.0f);
            particles.Count++;
        }
    }

    public sealed class Data
    {
        public ParticleSystemComponent ParticleSystem;
    }
}

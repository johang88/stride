// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering;
using Stride.Rendering.VolumetricFog;

namespace Stride.Engine.Processors;

/// <summary>
/// Hands the first enabled <see cref="VolumetricFogComponent"/> of the scene to the volumetric fog of the renderers.
/// </summary>
public sealed class VolumetricFogProcessor : EntityProcessor<VolumetricFogComponent>, IEntityComponentRenderProcessor
{
    private readonly RenderVolumetricFog renderFog = new();

    public VisibilityGroup VisibilityGroup { get; set; }

    protected internal override void OnSystemRemove()
    {
        VisibilityGroup?.Tags.Set(VolumetricFogRenderer.CurrentFog, null);
        base.OnSystemRemove();
    }

    public override void Draw(RenderContext context)
    {
        VolumetricFogComponent active = null;
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
            VisibilityGroup.Tags.Set(VolumetricFogRenderer.CurrentFog, null);
            return;
        }

        renderFog.Density = active.Density;
        renderFog.BaseHeight = active.BaseHeight;
        renderFog.HeightFalloff = active.HeightFalloff;
        renderFog.Albedo = active.Albedo;
        renderFog.Emissive = active.Emissive;
        renderFog.Anisotropy = active.Anisotropy;
        renderFog.MultipleScattering = active.MultipleScattering;
        renderFog.GroundAlbedo = active.GroundAlbedo;
        renderFog.SunScattering = active.SunScattering;
        renderFog.AmbientScattering = active.AmbientScattering;
        renderFog.LocalLightScattering = active.LocalLightScattering;
        renderFog.Distance = active.Distance;
        renderFog.NoiseStrength = active.NoiseStrength;
        renderFog.NoiseScale = active.NoiseScale;
        renderFog.Wind = active.Wind;
        renderFog.FogSky = active.FogSky;
        VisibilityGroup.Tags.Set(VolumetricFogRenderer.CurrentFog, renderFog);
    }
}

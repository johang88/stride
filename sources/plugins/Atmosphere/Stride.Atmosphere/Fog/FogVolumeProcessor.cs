// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// Hands the enabled <see cref="FogVolumeComponent"/>s of the scene to the volumetric fog of the renderers.
/// </summary>
public sealed class FogVolumeProcessor : EntityProcessor<FogVolumeComponent, RenderFogVolume>, IEntityComponentRenderProcessor
{
    private readonly List<RenderFogVolume> activeVolumes = new();

    public FogVolumeProcessor() : base(typeof(TransformComponent))
    {
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override void OnSystemAdd()
    {
        base.OnSystemAdd();
        VisibilityGroup.Tags.Set(VolumetricFogRenderer.CurrentVolumes, activeVolumes);
    }

    protected override void OnSystemRemove()
    {
        VisibilityGroup.Tags.Set(VolumetricFogRenderer.CurrentVolumes, null);
        base.OnSystemRemove();
    }

    protected override RenderFogVolume GenerateComponentData(Entity entity, FogVolumeComponent component) => new();

    public override void Draw(RenderContext context)
    {
        activeVolumes.Clear();
        foreach (var (component, volume) in ComponentDatas)
        {
            if (!component.Enabled || component.Density <= 0)
                continue;

            // The unit volume (half size 1) to world
            var halfSize = Vector3.Max(component.Size * 0.5f, new Vector3(1e-3f));
            var unitToWorld = Matrix.Scaling(halfSize) * component.Entity.Transform.WorldMatrix;
            Matrix.Invert(ref unitToWorld, out volume.WorldToLocal);
            var bounds = new BoundingBoxExt(new Vector3(-1), new Vector3(1));
            bounds.Transform(unitToWorld);
            volume.Bounds = (BoundingBox)bounds;

            volume.Shape = component.Shape;
            volume.Density = FogDensity.ToExtinction(component.Density, FogDensity.LocalFogExtinction);
            volume.Albedo = component.Albedo;
            volume.Emissive = component.Emissive;
            volume.EdgeFade = component.EdgeFade;
            volume.NoiseStrength = component.NoiseStrength;
            activeVolumes.Add(volume);
        }
    }
}

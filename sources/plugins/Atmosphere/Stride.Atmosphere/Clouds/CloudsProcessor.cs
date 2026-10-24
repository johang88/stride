// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Atmosphere.Clouds;

/// <summary>
/// The clouds of a scene as the atmosphere renderer draws them (from its <see cref="CloudsComponent"/>).
/// </summary>
public sealed class RenderClouds
{
    public LowCloudLayer Low;

    public MidCloudLayer Middle;

    public HighCloudLayer High;

    public float WeatherScale;

    public int CellsPerPattern;

    public int Seed;

    public float EvolutionPeriod;

    public float DropletSize;

    public float SilverLining;

    public float MultipleScattering;

    public float DarkEdges;

    public float AmbientLight;

    public float ShadowStrength;

    public float FogShadowStrength;

    public float MaxDistance;

    public Texture CloudMap;

    /// <summary>
    /// World XZ of the cloud map's corner and its size.
    /// </summary>
    public Vector2 CloudMapCorner;

    public float CloudMapSize;

    public float CloudMapStrength;
}

/// <summary>
/// Hands the clouds of the first enabled <see cref="CloudsComponent"/> to the atmosphere renderers.
/// </summary>
public sealed class CloudsProcessor : EntityProcessor<CloudsComponent>, IEntityComponentRenderProcessor
{
    private readonly RenderClouds clouds = new();

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override void OnSystemRemove()
    {
        VisibilityGroup?.Tags.Set(AtmosphereRenderer.CurrentClouds, null);
        base.OnSystemRemove();
    }

    public override void Draw(RenderContext context)
    {
        CloudsComponent active = null;
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
            VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentClouds, null);
            return;
        }

        clouds.Low = active.Low ?? new LowCloudLayer();
        clouds.Middle = active.Middle ?? new MidCloudLayer();
        clouds.High = active.High ?? new HighCloudLayer();
        clouds.WeatherScale = active.WeatherScale;
        clouds.CellsPerPattern = active.CellsPerPattern;
        clouds.Seed = active.Seed;
        clouds.EvolutionPeriod = active.EvolutionPeriod;
        clouds.DropletSize = active.DropletSize;
        clouds.SilverLining = active.SilverLining;
        clouds.MultipleScattering = active.MultipleScattering;
        clouds.DarkEdges = active.DarkEdges;
        clouds.AmbientLight = active.AmbientLight;
        clouds.ShadowStrength = active.ShadowStrength;
        clouds.FogShadowStrength = active.FogShadowStrength;
        clouds.MaxDistance = active.MaxDistance;
        clouds.CloudMap = active.CloudMap;
        clouds.CloudMapSize = active.CloudMapSize;
        clouds.CloudMapCorner = active.CloudMapCenter - new Vector2(active.CloudMapSize * 0.5f);
        clouds.CloudMapStrength = active.CloudMap != null ? active.CloudMapStrength : 0.0f;
        VisibilityGroup.Tags.Set(AtmosphereRenderer.CurrentClouds, clouds);
    }
}

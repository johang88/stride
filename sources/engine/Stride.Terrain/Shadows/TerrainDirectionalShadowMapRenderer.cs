// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace Stride.Terrain.Shadows;

/// <summary>
/// Directional light shadow maps that also receive the shadows of the terrain (<see cref="TerrainShadowManager"/>),
/// which reach beyond the cascades. Replaces the stock renderer when the terrain processor registers.
/// </summary>
/// <remarks>
/// The terrain shadows come from the manager of the game being drawn (its services), not one captured at registration:
/// editor games can share compositor objects, and each game's textures belong to its own device.
/// </remarks>
public sealed class TerrainDirectionalShadowMapRenderer : LightDirectionalShadowMapRenderer
{
    public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
    {
        return new TerrainShaderGroupData(shadowType);
    }

    private sealed class TerrainShaderGroupData : ShaderGroupData
    {
        private const string ShaderName = "TerrainShadowMapReceiverDirectional";

        private ObjectParameterKey<Texture> textureKey;
        private ValueParameterKey<Vector4> regionKey;
        private ValueParameterKey<Vector4> paramsKey;
        private ValueParameterKey<Vector4> filterKey;

        public TerrainShaderGroupData(LightShadowType shadowType) : base(shadowType)
        {
        }

        public override ShaderClassSource CreateShaderSource(int lightCurrentCount)
        {
            var isDepthRangeAuto = (ShadowType & LightShadowType.DepthRangeAuto) != 0;
            return new ShaderClassSource(ShaderName, cascadeCount, lightCurrentCount, (ShadowType & LightShadowType.BlendCascade) != 0, isDepthRangeAuto, (ShadowType & LightShadowType.Debug) != 0, (ShadowType & LightShadowType.ComputeTransmittance) != 0);
        }

        public override void UpdateLayout(string compositionKey)
        {
            base.UpdateLayout(compositionKey);
            textureKey = TerrainShadowMapReceiverDirectionalKeys.TerrainShadowTexture.ComposeWith(compositionKey);
            regionKey = TerrainShadowMapReceiverDirectionalKeys.TerrainShadowRegion.ComposeWith(compositionKey);
            paramsKey = TerrainShadowMapReceiverDirectionalKeys.TerrainShadowParams.ComposeWith(compositionKey);
            filterKey = TerrainShadowMapReceiverDirectionalKeys.TerrainShadowFilter.ComposeWith(compositionKey);
        }

        public override void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights)
        {
            base.ApplyViewParameters(context, parameters, currentLights);

            var manager = context.RenderContext.Services.GetService<TerrainShadowManager>();
            // The first light with shadows is the sun
            if (manager != null && currentLights.Count > 0)
                manager.LightDirection = currentLights[0].Light.Direction;

            var (texture, region, terrainParams, filter) = manager?.GetReceiverParameters() ?? default;
            parameters.Set(textureKey, texture ?? context.GraphicsDevice.GetSharedWhiteTexture());
            parameters.Set(regionKey, region);
            parameters.Set(paramsKey, terrainParams);
            parameters.Set(filterKey, filter);
        }
    }
}

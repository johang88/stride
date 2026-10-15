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
public sealed class TerrainDirectionalShadowMapRenderer : LightDirectionalShadowMapRenderer
{
    private readonly TerrainShadowManager manager;

    public TerrainDirectionalShadowMapRenderer(TerrainShadowManager manager)
    {
        this.manager = manager;
    }

    public override ILightShadowMapShaderGroupData CreateShaderGroupData(LightShadowType shadowType)
    {
        return new TerrainShaderGroupData(shadowType, manager);
    }

    private sealed class TerrainShaderGroupData : ShaderGroupData
    {
        private const string ShaderName = "TerrainShadowMapReceiverDirectional";

        private readonly TerrainShadowManager manager;
        private ObjectParameterKey<Texture> textureKey;
        private ValueParameterKey<Vector4> regionKey;
        private ValueParameterKey<Vector4> paramsKey;
        private ValueParameterKey<Vector2> biasKey;

        public TerrainShaderGroupData(LightShadowType shadowType, TerrainShadowManager manager) : base(shadowType)
        {
            this.manager = manager;
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
            biasKey = TerrainShadowMapReceiverDirectionalKeys.TerrainShadowBias.ComposeWith(compositionKey);
        }

        public override void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights)
        {
            base.ApplyViewParameters(context, parameters, currentLights);

            // The first light with shadows is the sun
            if (currentLights.Count > 0)
                manager.LightDirection = currentLights[0].Light.Direction;

            var (texture, region, terrainParams, bias) = manager.GetReceiverParameters();
            parameters.Set(textureKey, texture);
            parameters.Set(regionKey, region);
            parameters.Set(paramsKey, terrainParams);
            parameters.Set(biasKey, bias);
        }
    }
}

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
/// The shadows of the terrain (<see cref="TerrainShadowManager"/>), which reach beyond the cascades, received with the
/// directional light shadow maps (TerrainShadowReceiver, a receiver extension added when the terrain processor registers).
/// </summary>
/// <remarks>
/// The terrain shadows come from the manager of the game being drawn (its services), not one captured at registration:
/// editor games can share compositor objects, and each game's textures belong to its own device.
/// </remarks>
public sealed class TerrainShadowReceiverExtension : IDirectionalShadowReceiverExtension
{
    private static readonly ShaderClassSource Shader = new("TerrainShadowReceiver");

    public ShaderClassCode CreateShaderSource() => Shader;

    public IDirectionalShadowReceiverParameters CreateParameters() => new Parameters();

    private sealed class Parameters : IDirectionalShadowReceiverParameters
    {
        private ObjectParameterKey<Texture> textureKey;
        private ValueParameterKey<Vector4> regionKey;
        private ValueParameterKey<Vector4> paramsKey;
        private ValueParameterKey<Vector4> filterKey;

        public void UpdateLayout(string compositionKey)
        {
            textureKey = TerrainShadowReceiverKeys.TerrainShadowTexture.ComposeWith(compositionKey);
            regionKey = TerrainShadowReceiverKeys.TerrainShadowRegion.ComposeWith(compositionKey);
            paramsKey = TerrainShadowReceiverKeys.TerrainShadowParams.ComposeWith(compositionKey);
            filterKey = TerrainShadowReceiverKeys.TerrainShadowFilter.ComposeWith(compositionKey);
        }

        public void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights)
        {
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

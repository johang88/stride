// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace Stride.Atmosphere.Clouds;

/// <summary>
/// Cloud shadows on the surfaces lit by the sun: combined with the directional shadow maps (CloudShadowReceiver).
/// </summary>
/// <remarks>
/// The shadows come from the atmosphere renderer of the game being drawn (its <see cref="CloudShadows"/> service), not
/// one captured when the extension was added: editor games can share compositor objects.
/// </remarks>
internal sealed class CloudShadowReceiverExtension : IDirectionalShadowReceiverExtension
{
    private static readonly ShaderClassSource Shader = new("CloudShadowReceiver");

    public ShaderClassCode CreateShaderSource() => Shader;

    public IDirectionalShadowReceiverParameters CreateParameters() => new Parameters();

    private sealed class Parameters : IDirectionalShadowReceiverParameters
    {
        private ObjectParameterKey<Texture> textureKey;
        private ValueParameterKey<Vector4> regionKey;
        private ValueParameterKey<Vector4> lightKey;
        private ValueParameterKey<int> lightIndexKey;

        public void UpdateLayout(string compositionKey)
        {
            textureKey = CloudShadowReceiverKeys.CloudShadowTexture.ComposeWith(compositionKey);
            regionKey = CloudShadowReceiverKeys.CloudShadowRegion.ComposeWith(compositionKey);
            lightKey = CloudShadowReceiverKeys.CloudShadowLight.ComposeWith(compositionKey);
            lightIndexKey = CloudShadowReceiverKeys.CloudShadowLightIndex.ComposeWith(compositionKey);
        }

        public void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights)
        {
            var shadows = context.RenderContext.Services.GetService<CloudShadows>();
            var lightIndex = -1;
            if (shadows?.Texture != null)
            {
                for (int i = 0; i < currentLights.Count; i++)
                {
                    if (currentLights[i].Light == shadows.Light)
                    {
                        lightIndex = i;
                        break;
                    }
                }
            }
            parameters.Set(textureKey, lightIndex >= 0 ? shadows.Texture : context.GraphicsDevice.GetSharedWhiteTexture());
            parameters.Set(regionKey, lightIndex >= 0 ? shadows.Region : Vector4.Zero);
            parameters.Set(lightKey, lightIndex >= 0 ? shadows.LightDirection : Vector4.Zero);
            parameters.Set(lightIndexKey, lightIndex);
        }
    }
}

/// <summary>
/// The cloud shadows of a game's atmosphere renderer this frame, for the surfaces' shadow receivers.
/// </summary>
public sealed class CloudShadows
{
    /// <summary>
    /// The shadowed light (the sun light of the atmosphere).
    /// </summary>
    public RenderLight Light;

    /// <summary>
    /// The cloud shadow map (null: no cloud shadows).
    /// </summary>
    public Texture Texture;

    /// <summary>
    /// xy = world XZ of the map's corner, z = 1 / its size, w = strength.
    /// </summary>
    public Vector4 Region;

    /// <summary>
    /// xyz = direction towards the light, w = world height of sea level.
    /// </summary>
    public Vector4 LightDirection;
}

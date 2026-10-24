// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Collections;
using Stride.Rendering.Lights;
using Stride.Shaders;

namespace Stride.Rendering.Shadows;

/// <summary>
/// Shadows combined with those of the shadow maps of directional lights (see <see cref="LightDirectionalShadowMapRenderer.ReceiverExtensions"/>),
/// for example a terrain's shadows reaching beyond the cascades or the shadows of clouds.
/// </summary>
public interface IDirectionalShadowReceiverExtension
{
    /// <summary>
    /// The shader mixed into the shadow receiver after the shadow map's: a <c>ShadowGroup</c> overriding
    /// <c>float3 ComputeShadow(float3 position, int lightIndex)</c>, which combines <c>base.ComputeShadow</c> with its own shadows.
    /// Its parameters are in the <c>PerView.Lighting</c> groups.
    /// </summary>
    ShaderClassCode CreateShaderSource();

    /// <summary>
    /// Creates the parameters of the shader in one light group.
    /// </summary>
    IDirectionalShadowReceiverParameters CreateParameters();
}

/// <summary>
/// The parameters of an <see cref="IDirectionalShadowReceiverExtension"/> in one light group.
/// </summary>
public interface IDirectionalShadowReceiverParameters
{
    /// <summary>
    /// Composes the parameter keys of the shader with the composition of the light group.
    /// </summary>
    void UpdateLayout(string compositionKey);

    /// <summary>
    /// Sets the parameters of the shader for the current view (the lights of the group with their shadow maps).
    /// </summary>
    void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights);
}

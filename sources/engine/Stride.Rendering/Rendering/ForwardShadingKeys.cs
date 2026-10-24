// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Shaders;

namespace Stride.Rendering;

/// <summary>
/// Permutation keys of <c>StrideForwardShadingEffect</c>.
/// </summary>
public static class ForwardShadingKeys
{
    /// <summary>
    /// Shaders mixed in after the lighting of a surface (each a <c>ShadingBase</c> overriding <c>Shading()</c>), for example
    /// participating media that transparent surfaces apply in their own shading.
    /// </summary>
    public static readonly PermutationParameterKey<ShaderSourceCollection> ShadingExtensions = ParameterKeys.NewPermutation((ShaderSourceCollection)null);

    /// <summary>
    /// A shader mixed in before the lighting of opaque surfaces, bringing screen-space lighting terms computed for the view:
    /// ambient occlusion (into <c>matAmbientOcclusion</c>), and reflected and indirect light the sky light blends towards
    /// (the <c>screenSpaceSpecular</c> and <c>screenSpaceDiffuse</c> streams: radiance and confidence).
    /// </summary>
    public static readonly PermutationParameterKey<ShaderSource> ScreenSpaceLighting = ParameterKeys.NewPermutation((ShaderSource)null);
}

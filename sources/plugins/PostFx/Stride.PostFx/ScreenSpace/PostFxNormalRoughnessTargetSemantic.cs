// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Rendering.Compositing;
using Stride.Shaders;

namespace Stride.PostFx.ScreenSpace;

/// <summary>
/// The render target the opaque surfaces write their shading normal (normal maps and decals included) and roughness to, for
/// the screen-space reflections (PostFxGBufferNormalRoughness: R10G10B10A2, octahedral world space normal, perceptual
/// roughness, 1).
/// </summary>
public sealed class PostFxNormalRoughnessTargetSemantic : IRenderTargetSemantic
{
    public ShaderSource ShaderClass { get; } = new ShaderClassSource("PostFxGBufferNormalRoughness");
}

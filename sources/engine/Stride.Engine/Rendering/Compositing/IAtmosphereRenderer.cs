// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Graphics;

namespace Stride.Rendering.Compositing;

/// <summary>
/// The sky and the participating media (atmosphere, clouds, fog, smoke) of a <see cref="ForwardRenderer"/>: drawn behind and
/// over its opaque surfaces, and applied by its transparent surfaces in their own shading.
/// </summary>
/// <remarks>
/// Called for single views only (not in virtual reality).
/// </remarks>
public interface IAtmosphereRenderer : IDisposable
{
    /// <summary>
    /// Collects the scene data of the current view, after its camera was set. <paramref name="transparentStage"/> holds the
    /// surfaces that apply the media in their shading.
    /// </summary>
    void Collect(RenderContext context, RenderStage transparentStage);

    /// <summary>
    /// Renders what the surfaces of the current view sample (look-up tables, volumes, sky lighting). Called after the shadow
    /// maps, before the opaque surfaces.
    /// </summary>
    void Prepare(RenderDrawContext context);

    /// <summary>
    /// Draws the sky behind the opaque surfaces of the current view and the media over them and the sky.
    /// </summary>
    /// <param name="context">The draw context.</param>
    /// <param name="color">The color target (written).</param>
    /// <param name="depthStencil">Its depth buffer (read).</param>
    void Draw(RenderDrawContext context, Texture color, Texture depthStencil);
}

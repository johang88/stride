// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Graphics;

namespace Stride.Rendering.Compositing;

/// <summary>
/// The decals of a <see cref="ForwardRenderer"/>: projected onto the depth of its prepass into buffers that its opaque surfaces
/// read before their lighting (see <see cref="ForwardShadingKeys.Decals"/>).
/// </summary>
/// <remarks>
/// Called for single views with a depth prepass only (not in virtual reality, not with MSAA).
/// </remarks>
public interface IDecalRenderer : IDisposable
{
    /// <summary>
    /// Collects the decals of the current view, after its camera was set. <paramref name="opaqueStage"/> holds the surfaces
    /// that apply them.
    /// </summary>
    void Collect(RenderContext context, RenderStage opaqueStage);

    /// <summary>
    /// Draws the decals of the current view into the buffers its opaque surfaces read. Called after the depth prepass, before
    /// the opaque surfaces, with no render target bound.
    /// </summary>
    /// <param name="context">The draw context.</param>
    /// <param name="depthStencil">The depth of the prepass (read).</param>
    void Draw(RenderDrawContext context, Texture depthStencil);
}

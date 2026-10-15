// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Rendering;

/// <summary>
/// Lets a feature (e.g. terrain virtual texturing) take part in the rendering of camera views drawn by
/// scene renderers such as the forward renderer, without changing the graphics compositor.
/// </summary>
/// <seealso cref="RenderSystem.ViewExtensions"/>
public interface IRenderViewExtension
{
    /// <summary>
    /// Called while a camera view is collected (<see cref="RenderContext.RenderView"/> is that view), e.g. to add render stages to it.
    /// </summary>
    void Collect(RenderContext context);

    /// <summary>
    /// Called before the main passes of the view are drawn. Implementations must restore any render target they change.
    /// </summary>
    void Draw(RenderContext context, RenderDrawContext drawContext);
}

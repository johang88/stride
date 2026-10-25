// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Rendering.Images;

/// <summary>
/// Post effects that upscale: the forward renderer draws the scene at a share of its output's resolution (its color and
/// depth targets, the view's size), and they bring it to the output.
/// </summary>
public interface IUpscalingEffects
{
    /// <summary>
    /// Decides the share of the output's resolution the view is drawn at this frame (1: the output's), before the view is
    /// collected.
    /// </summary>
    float UpdateRenderScale(RenderContext context);

    /// <summary>
    /// Called before any of the frame's drawing of the renderer (shadows included): the start of its GPU time.
    /// </summary>
    void BeginDraw(RenderDrawContext context);
}

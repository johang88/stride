// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Graphics;
using Stride.Rendering.Compositing;

namespace Stride.Rendering.Images;

/// <summary>
/// Post-processing effects that also compute screen-space lighting terms for the opaque surfaces (ambient occlusion,
/// contact shadows): the forward renderer calls them once the depth prepass is drawn, before the opaque stage.
/// </summary>
public interface IScreenSpaceLightingEffects
{
    /// <summary>
    /// The render stage of the opaque surfaces (set by the forward renderer): the surfaces lit with the terms.
    /// </summary>
    RenderStage OpaqueRenderStage { get; set; }

    /// <summary>
    /// The render stage of the transparent surfaces (set by the forward renderer).
    /// </summary>
    RenderStage TransparentRenderStage { get; set; }

    /// <summary>
    /// Adds the render targets the effects need the opaque surfaces to write besides the color (after the forward renderer's
    /// own: the effects find them by their semantic in the targets they are drawn with).
    /// </summary>
    void ValidateOpaqueStageOutput(RenderOutputValidator validator);

    /// <summary>
    /// Draws the terms from the depth of the view (the depth buffer is bound to nothing, readable).
    /// </summary>
    void DrawAfterDepthPrepass(RenderDrawContext context, Texture depthStencil);
}

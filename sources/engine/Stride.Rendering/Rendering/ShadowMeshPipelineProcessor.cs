// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.ComponentModel;
using Stride.Graphics;

namespace Stride.Rendering
{
    /// <summary>
    /// Pipline processor for <see cref="RenderMesh"/> that cast shadows, to properly disable culling and depth clip,
    /// and to render them with a conventional depth range.
    /// </summary>
    public class ShadowMeshPipelineProcessor : PipelineProcessor
    {
        public RenderStage ShadowMapRenderStage { get; set; }

        [DefaultValue(false)]
        public bool DepthClipping { get; set; } = false;

        public override void Process(RenderNodeReference renderNodeReference, ref RenderNode renderNode, RenderObject renderObject, PipelineStateDescription pipelineState)
        {
            // Objects in the shadow map render stage disable culling and depth clip
            if (renderNode.RenderStage == ShadowMapRenderStage)
            {
                pipelineState.RasterizerState = new RasterizerStateDescription(CullMode.None) { DepthClipEnable = DepthClipping };

                // Camera views use a reversed depth range, but shadow maps are projected conventionally: their
                // atlas is cleared to 1 and the caster nearest the light is the one with the smallest depth.
                pipelineState.DepthStencilState = DepthStencilStates.DefaultInverse;
            }
        }
    }
}

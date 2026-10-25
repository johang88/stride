// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Threading;
using Stride.Graphics;

namespace Stride.Rendering;

/// <summary>
/// The stages of a depth prepass and the stage it resolves visibility for: the meshes drawn in both are shaded once per
/// pixel in the shading stage (equal depth, no depth writes, no alpha discard: the prepass already did it), instead of
/// every overlapping layer of an alpha-tested surface running its lighting before the late depth test rejects it.
/// </summary>
public sealed class DepthPrepassStages
{
    /// <summary>
    /// The depth prepass (null: none, the meshes draw as usual).
    /// </summary>
    public RenderStage DepthPrepass { get; set; }

    /// <summary>
    /// The stage shading what the prepass resolved (the opaque stage).
    /// </summary>
    public RenderStage Shading { get; set; }

    /// <summary>
    /// Whether a render object is drawn in the shading stage on the depth of the prepass.
    /// </summary>
    public bool IsResolved(RenderObject renderObject)
    {
        var prepass = DepthPrepass;
        var shading = Shading;
        if (prepass == null || shading == null || renderObject is not RenderMesh mesh)
            return false;
        // (a material testing depth its own way keeps its test)
        if (mesh.MaterialInfo == null || mesh.MaterialInfo.DepthFunction != null)
            return false;
        var stages = renderObject.ActiveRenderStages;
        return prepass.Index < stages.Length && shading.Index < stages.Length && stages[prepass.Index].Active && stages[shading.Index].Active;
    }
}

/// <summary>
/// Leaves the alpha discard out of the shading of the meshes the depth prepass resolved (see <see cref="DepthPrepassStages"/>).
/// </summary>
public sealed class DepthPrepassRenderFeature : SubRenderFeature
{
    public DepthPrepassStages Stages { get; set; }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        base.PrepareEffectPermutations(context);
        var stage = Stages?.Shading;
        if (stage == null || Stages.DepthPrepass == null)
            return;

        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        var renderEffects = RootRenderFeature.RenderData.GetData(rootFeature.RenderEffectKey);
        var effectSlotCount = rootFeature.EffectPermutationSlotCount;
        var effectSlot = rootFeature.GetEffectPermutationSlot(stage);
        Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
        {
            var renderObject = RootRenderFeature.GetObjectNode(objectNodeReference).RenderObject;
            if (!Stages.IsResolved(renderObject))
                return;
            var renderEffect = renderEffects[renderObject.StaticObjectNode * effectSlotCount + effectSlot.Index];
            renderEffect?.EffectValidator.ValidateParameter(ForwardShadingKeys.DepthPrepassResolved, true);
        });
    }
}

/// <summary>
/// Draws the meshes the depth prepass resolved on its depth: equal, without writing it (see <see cref="DepthPrepassStages"/>).
/// </summary>
public sealed class DepthPrepassPipelineProcessor : PipelineProcessor
{
    public DepthPrepassStages Stages { get; set; }

    public override void Process(RenderNodeReference renderNodeReference, ref RenderNode renderNode, RenderObject renderObject, PipelineStateDescription pipelineState)
    {
        if (Stages == null || renderNode.RenderStage != Stages.Shading || !Stages.IsResolved(renderObject))
            return;
        pipelineState.DepthStencilState.DepthBufferFunction = CompareFunction.Equal;
        pipelineState.DepthStencilState.DepthBufferWriteEnable = false;
    }
}

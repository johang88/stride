// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Core.Threading;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;

namespace Stride.Decals;

/// <summary>
/// Sub render feature of the <see cref="MeshRenderFeature"/> giving the opaque surfaces that receive decals the decal buffer of
/// their view (DecalShading, mixed into the forward shading). Added by the <see cref="DecalRenderer"/>.
/// </summary>
[DataContract("DecalRenderFeature")]
public sealed class DecalRenderFeature : SubRenderFeature
{
    private static readonly ShaderClassSource Shader = new("DecalShading");

    private readonly Dictionary<ObjectId, ParameterCollection> layoutParameters = new();
    private LogicalGroupReference viewKey;

    internal DecalViews Decals { get; set; }

    protected override void InitializeCore()
    {
        base.InitializeCore();
        viewKey = ((RootEffectRenderFeature)RootRenderFeature).CreateViewLogicalGroup("Decals");
    }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        base.PrepareEffectPermutations(context);
        var decals = Decals;
        var stage = decals?.OpaqueStage;
        // (always mixed in while the renderer runs, decals or not: no shader compilation when the first one shows up)
        if (stage == null || decals.CollectedFrame != RenderSystem.FrameCounter)
            return;

        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        var renderEffects = RootRenderFeature.RenderData.GetData(rootFeature.RenderEffectKey);
        var effectSlotCount = rootFeature.EffectPermutationSlotCount;
        var effectSlot = rootFeature.GetEffectPermutationSlot(stage);
        var skinnedMeshesReceive = decals.SkinnedMeshesReceive;
        Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
        {
            var renderObject = RootRenderFeature.GetObjectNode(objectNodeReference).RenderObject;
            if (stage.Index >= renderObject.ActiveRenderStages.Length || !renderObject.ActiveRenderStages[stage.Index].Active)
                return;
            if (renderObject is not RenderMesh { ReceiveDecals: true } renderMesh || (!skinnedMeshesReceive && renderMesh.IsSkinned))
                return;
            var renderEffect = renderEffects[renderObject.StaticObjectNode * effectSlotCount + effectSlot.Index];
            renderEffect?.EffectValidator.ValidateParameter(ForwardShadingKeys.Decals, Shader);
        });
    }

    public override void Prepare(RenderDrawContext context)
    {
        base.Prepare(context);
        if (Decals == null)
            return;

        var white = context.GraphicsDevice.GetSharedWhiteTexture();
        foreach (var view in RenderSystem.Views)
        {
            var viewFeature = view.Features[RootRenderFeature.Index];
            if (viewFeature.Layouts.Count == 0)
                continue;
            // (a view without decals this frame: none applied)
            Decals.Views.TryGetValue(view, out var state);
            var current = state != null && state.Frame == RenderSystem.FrameCounter && state.Count > 0 && state.Albedo != null;
            var channels = current ? state.Channels : DecalChannels.None;

            foreach (var viewLayout in viewFeature.Layouts)
            {
                if (viewLayout.State != RenderEffectState.Normal)
                    continue;
                var group = viewLayout.GetLogicalGroup(viewKey);
                if (group.Hash == ObjectId.Empty)
                    continue;
                if (!layoutParameters.TryGetValue(group.Hash, out var parameters))
                {
                    var layout = new ParameterCollectionLayout();
                    layout.ProcessLogicalGroup(viewLayout, ref group);
                    parameters = new ParameterCollection();
                    parameters.UpdateLayout(layout);
                    layoutParameters.Add(group.Hash, parameters);
                }

                parameters.Set(DecalShadingKeys.DecalAlbedoBuffer, current ? state.Albedo : white);
                parameters.Set(DecalShadingKeys.DecalNormalBuffer, current ? state.Normal : white);
                parameters.Set(DecalShadingKeys.DecalSurfaceBuffer, current ? state.Surface : white);
                parameters.Set(DecalShadingKeys.DecalEmissiveBuffer, current ? state.Emissive : white);
                parameters.Set(DecalShadingKeys.DecalChannels, new Vector4(
                    (channels & DecalChannels.Albedo) != 0 ? 1 : 0,
                    (channels & DecalChannels.Normal) != 0 ? 1 : 0,
                    (channels & DecalChannels.Surface) != 0 ? 1 : 0,
                    (channels & DecalChannels.Emissive) != 0 ? 1 : 0));
                viewLayout.Entries[view.Index].Resources.UpdateLogicalGroup(ref group, parameters);
            }
        }
    }
}

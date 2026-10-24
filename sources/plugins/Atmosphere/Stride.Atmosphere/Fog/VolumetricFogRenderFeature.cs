// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Storage;
using Stride.Core.Threading;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;

namespace Stride.Atmosphere.Fog;

/// <summary>
/// Sub render feature of the <see cref="MeshRenderFeature"/> fogging the transparent surfaces: their effects mix in the
/// fog (VolumetricFogShading, a shading extension of the forward effect) and each view binds its fog (<see cref="ViewParameters"/>).
/// Added by <see cref="VolumetricFogRenderer"/>.
/// </summary>
[DataContract("VolumetricFogRenderFeature")]
public sealed class VolumetricFogRenderFeature : SubRenderFeature
{
    private static readonly ShaderSourceCollection ShadingExtensions = [new ShaderClassSource("VolumetricFogShading")];

    private readonly Dictionary<ObjectId, ParameterCollection> layoutParameters = new();
    private LogicalGroupReference viewFogKey;

    /// <summary>
    /// The render stages of transparent surfaces, which apply the fog in their shading.
    /// </summary>
    [DataMemberIgnore]
    public HashSet<RenderStage> TransparentStages { get; } = new();

    /// <summary>
    /// The fog of each view (views without one get no fog).
    /// </summary>
    [DataMemberIgnore]
    public Dictionary<RenderView, VolumetricFogViewParameters> ViewParameters { get; } = new();

    /// <summary>
    /// A 1x1x1 volume holding no fog, for the views without a fog grid.
    /// </summary>
    internal static Texture GetEmptyVolume(GraphicsDevice device)
    {
        return device.GetOrCreateSharedData("VolumetricFogEmptyVolume", static device =>
        {
            // No light scattered, full transmittance
            var texel = new byte[] { 0, 0, 0, 255 };
            unsafe
            {
                fixed (byte* data = texel)
                {
                    var texture = Texture.New3D(device, 1, 1, 1, 1, PixelFormat.R8G8B8A8_UNorm, [new DataBox((nint)data, 4, 4)], TextureFlags.ShaderResource, GraphicsResourceUsage.Immutable);
                    texture.Name = "VolumetricFogEmptyVolume";
                    return texture;
                }
            }
        });
    }

    /// <summary>
    /// A black 1x1 cube map, for the views without a sky cube map.
    /// </summary>
    internal static Texture GetEmptyCubeMap(GraphicsDevice device)
    {
        return device.GetOrCreateSharedData("VolumetricFogEmptyCubeMap", static device =>
        {
            var texel = new byte[] { 0, 0, 0, 255 };
            unsafe
            {
                fixed (byte* data = texel)
                {
                    var face = new DataBox((nint)data, 4, 4);
                    var texture = Texture.NewCube(device, 1, PixelFormat.R8G8B8A8_UNorm, [face, face, face, face, face, face]);
                    texture.Name = "VolumetricFogEmptyCubeMap";
                    return texture;
                }
            }
        });
    }

    protected override void InitializeCore()
    {
        base.InitializeCore();
        viewFogKey = ((RootEffectRenderFeature)RootRenderFeature).CreateViewLogicalGroup("VolumetricFog");
    }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        base.PrepareEffectPermutations(context);
        if (TransparentStages.Count == 0)
            return;

        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        var renderEffects = RootRenderFeature.RenderData.GetData(rootFeature.RenderEffectKey);
        var effectSlotCount = rootFeature.EffectPermutationSlotCount;

        Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
        {
            var renderObject = RootRenderFeature.GetObjectNode(objectNodeReference).RenderObject;
            var staticObjectNode = renderObject.StaticObjectNode;
            foreach (var stage in TransparentStages)
            {
                if (stage.Index >= renderObject.ActiveRenderStages.Length || !renderObject.ActiveRenderStages[stage.Index].Active)
                    continue;
                var effectSlot = rootFeature.GetEffectPermutationSlot(stage);
                var renderEffect = renderEffects[staticObjectNode * effectSlotCount + effectSlot.Index];
                renderEffect?.EffectValidator.ValidateParameter(ForwardShadingKeys.ShadingExtensions, ShadingExtensions);
            }
        });
    }

    public override void Prepare(RenderDrawContext context)
    {
        base.Prepare(context);
        if (viewFogKey.Index < 0)
            return;

        foreach (var view in RenderSystem.Views)
        {
            var viewFeature = view.Features[RootRenderFeature.Index];
            if (viewFeature.Layouts.Count == 0)
                continue;
            // No fog: the empty volume and no height fog
            ViewParameters.TryGetValue(view, out var fog);

            foreach (var viewLayout in viewFeature.Layouts)
            {
                if (viewLayout.State != RenderEffectState.Normal)
                    continue;
                var group = viewLayout.GetLogicalGroup(viewFogKey);
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

                fog.ApplyTo(parameters, context.GraphicsDevice);
                viewLayout.Entries[view.Index].Resources.UpdateLogicalGroup(ref group, parameters);
            }
        }
    }
}

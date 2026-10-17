// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Shadows;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Sub render feature of <see cref="MeshRenderFeature"/> drawing the grass layers of terrains with GPU instancing: each frame
/// it scatters the instances around the camera, and before each view's draws it culls them to the view and sets the
/// instance count of the indirect draws.
/// </summary>
[DataContract("TerrainGrassRenderFeature")]
public sealed class TerrainGrassRenderFeature : SubRenderFeature
{
    private readonly HashSet<TerrainGrassVariant> handled = new();
    private TerrainVegetationState state;
    private readonly Vector4[] frustumPlanes = new Vector4[6];
    private StaticObjectPropertyKey<TerrainGrassVariant> variantKey;
    private StaticObjectPropertyKey<RenderEffect> renderEffectKey;
    private LogicalGroupReference instancingGroupKey;
    private ComputeEffectShader populateShader;
    private ComputeEffectShader argumentsShader;
    private TerrainIndirectComputeShader cullShader;

    protected override void InitializeCore()
    {
        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        variantKey = RootRenderFeature.RenderData.CreateStaticObjectKey<TerrainGrassVariant>();
        renderEffectKey = rootFeature.RenderEffectKey;
        instancingGroupKey = rootFeature.CreateDrawLogicalGroup("Instancing");

        populateShader = new ComputeEffectShader(Context) { ShaderSourceName = "TerrainGrassPopulate" };
        argumentsShader = new ComputeEffectShader(Context) { ShaderSourceName = "TerrainGrassDispatchArgs" };
        cullShader = new TerrainIndirectComputeShader(Context) { ShaderSourceName = "TerrainGrassCull" };
    }

    protected override void Destroy()
    {
        populateShader?.Dispose();
        argumentsShader?.Dispose();
        cullShader?.Dispose();
        base.Destroy();
    }

    public override void Extract()
    {
        var variants = RootRenderFeature.RenderData.GetData(variantKey);
        Context.VisibilityGroup.Tags.TryGetValue(TerrainVegetationState.Key, out state);
        var grassModels = state?.GrassModels;

        foreach (var objectNodeReference in RootRenderFeature.ObjectNodeReferences)
        {
            var objectNode = RootRenderFeature.GetObjectNode(objectNodeReference);
            if (objectNode.RenderObject is not RenderMesh renderMesh)
                continue;

            ref var variant = ref variants[renderMesh.StaticObjectNode];
            variant = null;
            if (grassModels == null || renderMesh.RenderModel == null || !grassModels.TryGetValue(renderMesh.RenderModel, out var grass))
                continue;

            variant = grass;
            renderMesh.IndirectBuffer = grass.GetDrawArguments(renderMesh.Mesh);
        }
    }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        var variants = RootRenderFeature.RenderData.GetData(variantKey);
        var renderEffects = RootRenderFeature.RenderData.GetData(renderEffectKey);
        var effectSlotCount = ((RootEffectRenderFeature)RootRenderFeature).EffectPermutationSlotCount;

        foreach (var renderObject in RootRenderFeature.RenderObjects)
        {
            var staticObjectNode = renderObject.StaticObjectNode;
            if (variants[staticObjectNode] == null)
                continue;

            for (int i = 0; i < effectSlotCount; i++)
            {
                var renderEffect = renderEffects[staticObjectNode * effectSlotCount + i];
                if (renderEffect == null || !renderEffect.IsUsedDuringThisFrame(RenderSystem))
                    continue;
                // Instance matrices are absolute (the entity has no transform)
                renderEffect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.HasInstancing, true);
                renderEffect.EffectValidator.ValidateParameter(StrideEffectBaseKeys.ModelTransformUsage, 0);
            }
        }
    }

    public override void Prepare(RenderDrawContext context)
    {
        var variants = RootRenderFeature.RenderData.GetData(variantKey);
        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;

        // Scatter each model once per frame, around the main camera
        handled.Clear();
        var hasCamera = TryGetCameraPosition(out var cameraPosition);
        foreach (var renderNode in rootFeature.RenderNodes)
        {
            var variant = variants[renderNode.RenderObject.StaticObjectNode];
            if (variant == null || !handled.Add(variant))
                continue;
            if (hasCamera)
                variant.Populate(context, populateShader, cameraPosition);
        }

        // The draws read the culled matrices
        foreach (var renderNode in rootFeature.RenderNodes)
        {
            var variant = variants[renderNode.RenderObject.StaticObjectNode];
            if (variant?.World == null)
                continue;
            var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
            if (perDrawLayout == null || instancingGroupKey.Index < 0)
                continue;
            var group = perDrawLayout.GetLogicalGroup(instancingGroupKey);
            if (group.Hash == ObjectId.Empty || group.DescriptorEntryStart == -1)
                continue;
            renderNode.Resources.DescriptorSet.SetShaderResourceView(group.DescriptorEntryStart, variant.World);
            renderNode.Resources.DescriptorSet.SetShaderResourceView(group.DescriptorEntryStart + 1, variant.WorldInverse);
        }
    }

    public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        var variants = RootRenderFeature.RenderData.GetData(variantKey);

        // Cull each model once for the draws that follow
        handled.Clear();
        for (int index = startIndex; index < endIndex; index++)
        {
            var renderNode = RootRenderFeature.GetRenderNode(renderViewStage.SortedRenderNodes[index].RenderNode);
            var variant = variants[renderNode.RenderObject.StaticObjectNode];
            if (variant == null || !handled.Add(variant))
                continue;

            // Never scattered: its draws have no instance yet
            if (variant.World == null)
                continue;

            // Only camera views get their frustum filled in
            var frustum = renderView is ShadowMapRenderView ? new BoundingFrustum(ref renderView.ViewProjection) : renderView.Frustum;
            SetPlane(0, frustum.LeftPlane);
            SetPlane(1, frustum.RightPlane);
            SetPlane(2, frustum.TopPlane);
            SetPlane(3, frustum.BottomPlane);
            SetPlane(4, frustum.NearPlane);
            SetPlane(5, frustum.FarPlane);
            variant.Cull(context, renderView, argumentsShader, cullShader, frustumPlanes);
        }
    }

    private void SetPlane(int index, Plane plane) => frustumPlanes[index] = new Vector4(plane.Normal, plane.D);

    /// <summary>
    /// The position of the camera of the main view (the first camera view).
    /// </summary>
    private bool TryGetCameraPosition(out Vector3 position)
    {
        foreach (var view in RenderSystem.Views)
        {
            if (view.GetType() != typeof(RenderView))
                continue;
            Matrix.Invert(ref view.View, out var viewInverse);
            position = viewInverse.TranslationVector;
            if (state != null)
            {
                state.CameraPosition = position;
                state.HasCamera = true;
            }
            return true;
        }
        position = default;
        return false;
    }
}

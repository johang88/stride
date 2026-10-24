// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Storage;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Shadows;
using Stride.Terrain.Shadows;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain.Rendering;

/// <summary>
/// Sub render feature of <see cref="MeshRenderFeature"/> that selects the CDLOD nodes of each terrain for each view
/// and binds them (per draw) to the terrain vertex shader.
/// </summary>
[DataContract("TerrainRenderFeature")]
public sealed class TerrainRenderFeature : SubRenderFeature
{
    private static readonly ProfilingKey PrepareKey = new("TerrainRenderFeature.Prepare");

    private readonly List<Vector4> nodes = new();
    private readonly List<Vector4> viewNodes = new();
    private LogicalGroupReference terrainGroupKey;
    private Buffer nodeBuffer;
    private int nodeBufferCapacity;

    protected override void InitializeCore()
    {
        terrainGroupKey = ((RootEffectRenderFeature)RootRenderFeature).CreateDrawLogicalGroup("Terrain");
    }

    protected override void Destroy()
    {
        nodeBuffer?.Dispose();
        nodeBuffer = null;
        base.Destroy();
    }

    public override unsafe void Prepare(RenderDrawContext context)
    {
        using var _ = Profiler.Begin(PrepareKey);
        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;

        // Upload modified terrain data and reset the selections
        var hasTerrain = false;
        foreach (var renderObject in RootRenderFeature.RenderObjects)
        {
            if (renderObject is TerrainRenderObject terrain)
            {
                terrain.Gpu?.Upload(context.CommandList);
                terrain.ViewNodes.Clear();
                hasTerrain = true;
            }
        }
        if (!hasTerrain)
            return;

        // Select the nodes of each terrain in each view that draws it
        nodes.Clear();
        var terrainShadows = Context.Services.GetService<TerrainShadowManager>();
        foreach (var renderNode in rootFeature.RenderNodes)
        {
            if (renderNode.RenderObject is not TerrainRenderObject terrain || terrain.QuadTree == null)
                continue;
            var view = renderNode.RenderView;
            if (terrain.ViewNodes.ContainsKey(view))
                continue;

            viewNodes.Clear();
            // Its terrain shadows already darken what its directional shadow maps would: casting there too would only add their hard edges
            if (view is ShadowMapRenderView { ShadowMapTexture.Renderer: LightDirectionalShadowMapRenderer directional } && terrainShadows is { IsReady: true } && terrainShadows.Active == terrain
                && directional.ReceiverExtensions.OfType<TerrainShadowReceiverExtension>().Any())
            {
                terrain.ViewNodes[view] = new TerrainRenderObject.NodeRange { Offset = nodes.Count, Count = 0 };
                continue;
            }

            var lodView = view is ShadowMapRenderView shadowView && shadowView.RenderView != null ? shadowView.RenderView : view;
            var lodOrigin = GetViewPosition(lodView);
            // Only camera views get their frustum filled in: shadow views would select no node and cast nothing
            var frustum = view is ShadowMapRenderView ? new BoundingFrustum(ref view.ViewProjection) : view.Frustum;
            terrain.QuadTree.Select(terrain.World.TranslationVector, lodOrigin, ref frustum, view.VisiblityIgnoreDepthPlanes, viewNodes);
            terrain.ViewNodes[view] = new TerrainRenderObject.NodeRange { Offset = nodes.Count, Count = viewNodes.Count };
            nodes.AddRange(viewNodes);
        }

        if (nodes.Count == 0)
            return;

        // Upload all the views' nodes in one buffer
        if (nodeBuffer == null || nodeBufferCapacity < nodes.Count)
        {
            nodeBuffer?.Dispose();
            nodeBufferCapacity = Math.Max(1024, (int)BitOperationsCeilPow2((uint)nodes.Count));
            nodeBuffer = Buffer.Structured.New(context.GraphicsDevice, nodeBufferCapacity, sizeof(Vector4));
            nodeBuffer.Name = "TerrainNodes";
        }
        nodeBuffer.SetData(context.CommandList, (ReadOnlySpan<Vector4>)CollectionsMarshal.AsSpan(nodes));

        // Bind the nodes of each draw
        foreach (var renderNode in rootFeature.RenderNodes)
        {
            if (renderNode.RenderObject is not TerrainRenderObject terrain)
                continue;

            var perDrawLayout = renderNode.RenderEffect.Reflection?.PerDrawLayout;
            if (perDrawLayout == null || terrainGroupKey.Index < 0)
                continue;

            var group = perDrawLayout.GetLogicalGroup(terrainGroupKey);
            if (group.Hash == ObjectId.Empty)
                continue;
            if (!terrain.ViewNodes.TryGetValue(renderNode.RenderView, out var range))
                continue;

            if (group.DescriptorEntryStart != -1)
                renderNode.Resources.DescriptorSet.SetShaderResourceView(group.DescriptorEntryStart, nodeBuffer);

            if (group.ConstantBufferSize > 0)
            {
                var view = renderNode.RenderView;
                var lodView = view is ShadowMapRenderView shadowView && shadowView.RenderView != null ? shadowView.RenderView : view;
                var localOrigin = GetViewPosition(lodView) - terrain.World.TranslationVector;
                var data = (Vector4*)((byte*)renderNode.Resources.ConstantBuffer.Data + group.ConstantBufferOffset);
                *data = new Vector4(localOrigin, range.Offset);
            }
        }
    }

    private static Vector3 GetViewPosition(RenderView view)
    {
        Matrix.Invert(ref view.View, out var viewInverse);
        return viewInverse.TranslationVector;
    }

    private static uint BitOperationsCeilPow2(uint value) => System.Numerics.BitOperations.RoundUpToPowerOf2(value);
}

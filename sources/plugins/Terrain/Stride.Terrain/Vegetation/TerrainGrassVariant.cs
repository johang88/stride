// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Shaders;
using Stride.Terrain.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// One model of a grass layer: an entity drawing the model with GPU instancing, and the buffers its instances are scattered
/// into each frame (around the camera) and culled into for each view.
/// </summary>
internal sealed class TerrainGrassVariant : IDisposable
{
    // Most grid cells along each side of the scatter grid
    // The counters' reset value (a span literal allocates in unoptimized builds)
    private static readonly uint[] Zero = [0];
    private const int MaxGridSize = 1024;
    private const int CullThreadsPerGroup = 64;

    private readonly GraphicsDevice device;
    private readonly Dictionary<Mesh, Buffer> meshArguments = new();
    private Buffer instances;
    private Buffer instanceCounter;
    private Buffer world;
    private Buffer worldInverse;
    private Buffer visibleCounter;
    private Buffer dispatchArgsWritable;
    private Buffer dispatchArgs;
    private int capacity;

    public TerrainGrassVariant(GraphicsDevice device, TerrainGrassLayer layer, int layerIndex, Model source, int variant, int variantCount)
    {
        this.device = device;
        Layer = layer;
        LayerIndex = layerIndex;
        Source = source;
        Variant = variant;
        VariantCount = variantCount;

        // A copy of the model whose meshes are never frustum culled: the instances are culled on the GPU
        var model = new Model();
        foreach (var material in source.Materials)
            model.Materials.Add(material);
        var huge = new BoundingBox(new Vector3(-1e6f), new Vector3(1e6f));
        foreach (var mesh in source.Meshes)
        {
            model.Meshes.Add(new Mesh(mesh) { BoundingBox = huge, BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f) });
            BoundingRadius = Math.Max(BoundingRadius, mesh.BoundingSphere.Radius + mesh.BoundingSphere.Center.Length());
        }
        model.BoundingBox = huge;
        model.BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f);
        if (BoundingRadius <= 0)
            BoundingRadius = 1;

        Model = model;
        Entity = new Entity($"Grass {layer.Name} {variant}")
        {
            new ModelComponent(model) { IsShadowCaster = layer.CastShadows },
        };
    }

    public TerrainGrassLayer Layer { get; }

    public int LayerIndex { get; }

    public Model Source { get; }

    public int Variant { get; }

    public int VariantCount { get; }

    /// <summary>
    /// The model drawn (the source model's meshes without bounds).
    /// </summary>
    public Model Model { get; }

    public Entity Entity { get; }

    public ModelComponent ModelComponent => Entity.Get<ModelComponent>();

    public float BoundingRadius { get; }

    /// <summary>
    /// The terrain the plants grow on (set each frame by the processor).
    /// </summary>
    public TerrainRenderObject Terrain { get; set; }

    public Buffer World => world;

    public Buffer WorldInverse => worldInverse;

    /// <summary>
    /// The indirect draw arguments of a mesh of the drawn model (the instance count is written by the cull pass).
    /// </summary>
    public Buffer GetDrawArguments(Mesh mesh)
    {
        if (!meshArguments.TryGetValue(mesh, out var buffer))
        {
            var draw = mesh.Draw;
            DrawArguments[] arguments =
            [
                new DrawArguments
                {
                    IndexOrVertexCountPerInstance = (uint)draw.DrawCount,
                    InstanceCount = 0,
                    StartLocation = (uint)draw.StartLocation,
                    BaseVertexLocation = 0,
                    StartInstanceLocation = 0,
                }
            ];
            buffer = Buffer.New(device, (ReadOnlySpan<DrawArguments>)arguments, BufferFlags.ArgumentBuffer);
            buffer.Name = "TerrainGrassDrawArguments";
            meshArguments.Add(mesh, buffer);
        }
        return buffer;
    }

    /// <summary>
    /// Multiplies the layer's draw distance (quality, set each frame by the processor).
    /// </summary>
    public float DistanceScale { get; set; } = 1.0f;

    /// <summary>
    /// Multiplies the layer's density (quality, set each frame by the processor).
    /// </summary>
    public float DensityScale { get; set; } = 1.0f;

    /// <summary>
    /// Scatters the instances around the camera.
    /// </summary>
    public void Populate(RenderDrawContext context, ComputeEffectShader shader, Vector3 cameraPosition)
    {
        var terrain = Terrain;
        var density = terrain?.Gpu?.GrassDensity;
        var layerDensity = Layer.Density * DensityScale;
        var drawDistance = Layer.DrawDistance * DistanceScale;
        if (terrain?.Data == null || density == null || layerDensity <= 0 || drawDistance <= 0)
        {
            // Nothing to scatter: the views cull nothing
            instanceCounter?.SetData(context.CommandList, Zero);
            return;
        }

        // The grid covers the draw distance at full density (a dense layer draws less far, rather than skipping cells)
        var spacing = 1.0f / MathF.Sqrt(layerDensity);
        var gridSize = Math.Min(MaxGridSize, (int)MathF.Ceiling(2 * drawDistance / spacing) + 1);
        drawDistance = Math.Min(drawDistance, (gridSize - 1) * 0.5f * spacing);
        var thinOutStart = drawDistance * Math.Clamp(Layer.ThinOutStart, 0.0f, 0.99f);
        // Where fully painted, split between the models, with some margin
        var expected = ThinnedArea(thinOutStart, drawDistance) * layerDensity / VariantCount;
        EnsureCapacity(Math.Min(gridSize * gridSize, (int)(expected * 1.25f) + 256));

        instanceCounter.SetData(context.CommandList, Zero);

        var origin = terrain.World.TranslationVector;
        var parameters = shader.Parameters;
        TerrainMaterialParameters.ApplyBase(parameters, terrain);
        parameters.Set(TerrainGrassPopulateKeys.GrassDensity, density);
        parameters.Set(TerrainGrassPopulateKeys.GrassInstances, instances);
        parameters.Set(TerrainGrassPopulateKeys.GrassInstanceCounter, instanceCounter);
        parameters.Set(TerrainGrassPopulateKeys.GrassCameraLocal, cameraPosition - origin);
        parameters.Set(TerrainGrassPopulateKeys.GrassTerrainOrigin, origin);
        parameters.Set(TerrainGrassPopulateKeys.GrassGridSize, (uint)gridSize);
        parameters.Set(TerrainGrassPopulateKeys.GrassSpacing, spacing);
        parameters.Set(TerrainGrassPopulateKeys.GrassDrawDistance, drawDistance);
        parameters.Set(TerrainGrassPopulateKeys.GrassThinOutStart, thinOutStart);
        // Each plant shrinks away over the last meters before it goes
        parameters.Set(TerrainGrassPopulateKeys.GrassShrinkLength, Math.Clamp((drawDistance - thinOutStart) * 0.1f, 0.5f, 6.0f));
        parameters.Set(TerrainGrassPopulateKeys.GrassMinScale, Layer.MinScale);
        parameters.Set(TerrainGrassPopulateKeys.GrassMaxScale, Math.Max(Layer.MinScale, Layer.MaxScale));
        parameters.Set(TerrainGrassPopulateKeys.GrassLayer, (float)LayerIndex);
        parameters.Set(TerrainGrassPopulateKeys.GrassVariant, (uint)Variant);
        parameters.Set(TerrainGrassPopulateKeys.GrassVariantCount, (uint)VariantCount);
        parameters.Set(TerrainGrassPopulateKeys.GrassAlignToTerrain, Layer.AlignToTerrain ? 1.0f : 0.0f);
        parameters.Set(TerrainGrassPopulateKeys.GrassMaxInstances, (uint)capacity);
        shader.ThreadNumbers = new Int3(8, 8, 1);
        shader.ThreadGroupCounts = new Int3((gridSize + 7) / 8, (gridSize + 7) / 8, 1);
        shader.Draw(context, "TerrainGrass.Populate");
    }

    // Area covered at full density: the disc up to the thinning start, then the ring where the kept fraction falls as
    // (1 - t)^2 (t from 0 at the start to 1 at the draw distance, as in the populate shader)
    private static float ThinnedArea(float start, float end)
    {
        var area = MathF.PI * start * start;
        const int steps = 32;
        var step = (end - start) / steps;
        for (int i = 0; i < steps; i++)
        {
            var t = (i + 0.5f) / steps;
            var radius = start + t * (end - start);
            area += (1 - t) * (1 - t) * 2 * MathF.PI * radius * step;
        }
        return area;
    }

    /// <summary>
    /// Keeps the instances in a view and sets the instance count of the draws.
    /// </summary>
    public void Cull(RenderDrawContext context, RenderView view, ComputeEffectShader argumentsShader, TerrainIndirectComputeShader cullShader, Vector4[] frustumPlanes)
    {
        var commandList = context.CommandList;

        // Thread groups of the cull from the scattered count: written to a raw buffer (an argument buffer can't be bound
        // for writing) then copied
        argumentsShader.Parameters.Set(TerrainGrassDispatchArgsKeys.GrassInstanceCounter, instanceCounter);
        argumentsShader.Parameters.Set(TerrainGrassDispatchArgsKeys.GrassDispatchArgs, dispatchArgsWritable);
        argumentsShader.Parameters.Set(TerrainGrassDispatchArgsKeys.GrassThreadsPerGroup, (uint)CullThreadsPerGroup);
        argumentsShader.Parameters.Set(TerrainGrassDispatchArgsKeys.GrassMaxInstances, (uint)capacity);
        argumentsShader.ThreadNumbers = new Int3(1, 1, 1);
        argumentsShader.ThreadGroupCounts = new Int3(1, 1, 1);
        argumentsShader.Draw(context, "TerrainGrass.CullArguments");
        commandList.Copy(dispatchArgsWritable, dispatchArgs);

        visibleCounter.SetData(commandList, Zero);
        // The previous draws of the model still have the matrices bound for reading
        commandList.ResourceBarrierTransition(world, GraphicsResourceState.UnorderedAccess);
        commandList.ResourceBarrierTransition(worldInverse, GraphicsResourceState.UnorderedAccess);

        var parameters = cullShader.Parameters;
        parameters.Set(TerrainGrassCullKeys.GrassBoundingRadius, BoundingRadius);
        parameters.Set(TerrainGrassCullKeys.GrassFrustumPlanes, frustumPlanes);
        parameters.Set(TerrainGrassCullKeys.GrassInstances, instances);
        parameters.Set(TerrainGrassCullKeys.GrassInstanceCounter, instanceCounter);
        parameters.Set(TerrainGrassCullKeys.GrassMaxInstances, (uint)capacity);
        parameters.Set(TerrainGrassCullKeys.GrassWorld, world);
        parameters.Set(TerrainGrassCullKeys.GrassWorldInverse, worldInverse);
        parameters.Set(TerrainGrassCullKeys.GrassVisibleCounter, visibleCounter);
        cullShader.IndirectBuffer = dispatchArgs;
        cullShader.ThreadNumbers = new Int3(CullThreadsPerGroup, 1, 1);
        cullShader.Draw(context, "TerrainGrass.Cull");
        // Back to reading for the draws: on Direct3D 11 matrices still bound for writing would be unbound from the vertex
        // shader (the draws would see no instance)
        commandList.ResourceBarrierTransition(world, GraphicsResourceState.AllShaderResource);
        commandList.ResourceBarrierTransition(worldInverse, GraphicsResourceState.AllShaderResource);

        // The visible count is the instance count of every mesh's draw (second argument)
        foreach (var arguments in meshArguments.Values)
            commandList.CopyRegion(visibleCounter, 0, new ResourceRegion(0, 0, 0, sizeof(uint), 1, 1), arguments, 0, sizeof(uint));
    }

    private void EnsureCapacity(int count)
    {
        if (instances != null && capacity >= count)
            return;
        DisposeInstanceBuffers();
        capacity = Math.Max(count, capacity * 3 / 2);
        instances = Buffer.Structured.New(device, capacity, 32, true);
        instances.Name = "TerrainGrassInstances";
        world = Buffer.Structured.New<Matrix>(device, capacity, true);
        world.Name = "TerrainGrassWorld";
        worldInverse = Buffer.Structured.New<Matrix>(device, capacity, true);
        worldInverse.Name = "TerrainGrassWorldInverse";
        instanceCounter ??= Buffer.Raw.New(device, sizeof(uint), BufferFlags.UnorderedAccess | BufferFlags.ShaderResource);
        visibleCounter ??= Buffer.Raw.New(device, sizeof(uint), BufferFlags.UnorderedAccess | BufferFlags.ShaderResource);
        dispatchArgsWritable ??= Buffer.Raw.New(device, 4 * sizeof(uint), BufferFlags.UnorderedAccess | BufferFlags.ShaderResource);
        dispatchArgs ??= Buffer.New(device, new BufferDescription { BufferFlags = BufferFlags.ArgumentBuffer, SizeInBytes = 4 * sizeof(uint) });
    }

    private void DisposeInstanceBuffers()
    {
        instances?.Dispose();
        world?.Dispose();
        worldInverse?.Dispose();
        instances = world = worldInverse = null;
    }

    public void Dispose()
    {
        DisposeInstanceBuffers();
        instanceCounter?.Dispose();
        visibleCounter?.Dispose();
        dispatchArgsWritable?.Dispose();
        dispatchArgs?.Dispose();
        foreach (var buffer in meshArguments.Values)
            buffer.Dispose();
        meshArguments.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawArguments
    {
        public uint IndexOrVertexCountPerInstance;
        public uint InstanceCount;
        public uint StartLocation;
        public int BaseVertexLocation;
        public uint StartInstanceLocation;
    }
}

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
    /// Scatters the instances around the camera.
    /// </summary>
    public void Populate(RenderDrawContext context, ComputeEffectShader shader, Vector3 cameraPosition)
    {
        var terrain = Terrain;
        var density = terrain?.Gpu?.GrassDensity;
        if (terrain?.Data == null || density == null || Layer.Density <= 0 || Layer.DrawDistance <= 0)
        {
            // Nothing to scatter: the views cull nothing
            instanceCounter?.SetData(context.CommandList, (ReadOnlySpan<uint>)[0u]);
            return;
        }

        var spacing = 1.0f / MathF.Sqrt(Layer.Density);
        var gridSize = Math.Min(MaxGridSize, (int)MathF.Ceiling(2 * Layer.DrawDistance / spacing) + 1);
        // At full density over the whole disc, split between the models, with some margin
        var expected = MathF.PI * Layer.DrawDistance * Layer.DrawDistance * Layer.Density / VariantCount;
        EnsureCapacity(Math.Min(gridSize * gridSize, (int)(expected * 1.25f) + 256));

        instanceCounter.SetData(context.CommandList, (ReadOnlySpan<uint>)[0u]);

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
        parameters.Set(TerrainGrassPopulateKeys.GrassDrawDistance, Layer.DrawDistance);
        parameters.Set(TerrainGrassPopulateKeys.GrassFadeStart, Math.Clamp(Layer.FadeStart, 0, 0.999f));
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

        visibleCounter.SetData(commandList, (ReadOnlySpan<uint>)[0u]);

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

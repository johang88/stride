// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;
using Half = System.Half;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Draws the instances of one vegetation type of a terrain: an instanced entity per level of detail of its model, filled each
/// frame with the instances in range of the camera, and an impostor entity drawing every instance as a billboard of the
/// atlas baked from the most detailed level (dithered into the meshes over the fade range).
/// </summary>
internal sealed class TerrainVegetationTypeRenderer : IDisposable
{
    // Size of the cells instances are bucketed in (meters), to only visit those in range of the camera
    private const float GridCell = 64.0f;

    private readonly GraphicsDevice device;
    private readonly List<Instance> instances = new();
    private readonly Dictionary<(int X, int Z), List<int>> grid = new();
    private readonly List<FastList<Matrix>> lodMatrices = new();
    private readonly List<float> lodDistances = new();
    private float[] lodThresholdsSquared = [];
    private readonly Mesh impostorMesh;
    private Buffer impostorInstances;
    private bool impostorInstancesDirty;
    private bool bakeAttempted;

    private struct Instance
    {
        public Vector3 Position;
        public float Rotation;
        public float Scale;
    }

    public TerrainVegetationTypeRenderer(GraphicsDevice device, ContentManager content, TerrainVegetationType definition)
    {
        this.device = device;
        Definition = definition;
        Model = definition.Model;
        Views = definition.ImpostorViews;
        Resolution = definition.ImpostorResolution;
        CastShadows = definition.CastShadows;

        Lods = TerrainVegetationLods.Split(Model);
        var huge = new BoundingBox(new Vector3(-1e6f), new Vector3(1e6f));
        for (int lod = 0; lod < Lods.Count; lod++)
        {
            var entity = new Entity($"{definition.Name} LOD {lod}")
            {
                new ModelComponent(Lods[lod]) { Enabled = false, IsShadowCaster = definition.CastShadows, BoundingBox = huge, BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f) },
                new InstancingComponent { Enabled = false, Type = new InstancingUserArray() },
            };
            Entities.Add(entity);
            lodMatrices.Add(new FastList<Matrix>());
        }

        // The billboards are generated in the vertex shader from the instance buffer: six vertices each, no vertex buffer
        impostorMesh = new Mesh
        {
            Draw = new MeshDraw { PrimitiveType = PrimitiveType.TriangleList, VertexBuffers = [], DrawCount = 0 },
            BoundingBox = huge,
            BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f),
        };
        ImpostorMaterial = Material.New(device, TerrainImpostorMaterial.CreateDescriptor(), content);
        var impostorModel = new Model { impostorMesh };
        impostorModel.Materials.Add(new MaterialInstance(ImpostorMaterial));
        impostorModel.BoundingBox = huge;
        impostorModel.BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f);
        ImpostorEntity = new Entity($"{definition.Name} impostors")
        {
            new ModelComponent(impostorModel) { Enabled = false, IsShadowCaster = definition.ImpostorsCastShadows, BoundingBox = huge, BoundingSphere = new BoundingSphere(Vector3.Zero, 1e6f) },
        };
        Entities.Add(ImpostorEntity);
    }

    public TerrainVegetationType Definition { get; }

    /// <summary>
    /// The settings this renderer was built with (a change rebuilds it).
    /// </summary>
    public Model Model { get; }

    public int Views { get; }

    public int Resolution { get; }

    public bool CastShadows { get; }

    /// <summary>
    /// The levels of detail of the model, most detailed first.
    /// </summary>
    public List<Model> Lods { get; }

    /// <summary>
    /// The entities drawing the levels of detail, then the impostors.
    /// </summary>
    public List<Entity> Entities { get; } = new();

    public Entity ImpostorEntity { get; }

    public Material ImpostorMaterial { get; }

    public TerrainImpostorAtlas Atlas { get; private set; }

    public int InstanceCount => instances.Count;

    /// <summary>
    /// Instances drawn per level of detail in the last frame (the impostors are drawn for all).
    /// </summary>
    public int[] LastLodCounts { get; private set; } = [];

    /// <summary>
    /// Sets the instances of the type (world positions on the terrain).
    /// </summary>
    public void SetInstances(TerrainData data, Vector3 origin, int type)
    {
        instances.Clear();
        grid.Clear();
        foreach (var instance in data.Vegetation)
        {
            if (instance.Type != type || !data.Contains(instance.Position.X, instance.Position.Y))
                continue;
            var height = data.SampleHeight(instance.Position.X, instance.Position.Y) + instance.HeightOffset;
            var position = origin + new Vector3(instance.Position.X, height, instance.Position.Y);
            var key = ((int)MathF.Floor(position.X / GridCell), (int)MathF.Floor(position.Z / GridCell));
            if (!grid.TryGetValue(key, out var cell))
                grid.Add(key, cell = new List<int>());
            cell.Add(instances.Count);
            instances.Add(new Instance { Position = position, Rotation = instance.Rotation, Scale = instance.Scale > 0 ? instance.Scale : 1 });
        }
        impostorInstancesDirty = true;
    }

    /// <summary>
    /// Fills the levels of detail with the instances in range of the camera (game thread).
    /// </summary>
    public void Update(Vector3 camera, bool hasCamera, bool impostorPreview, bool enabled, RenderGroup renderGroup)
    {
        var impostorDistance = impostorPreview ? 0 : Math.Max(Definition.ImpostorDistance, 0);
        TerrainVegetationLods.GetLodDistances(Definition.LodDistances, Lods.Count, impostorDistance, lodDistances);
        if (lodThresholdsSquared.Length != lodDistances.Count)
            lodThresholdsSquared = new float[lodDistances.Count];
        for (int i = 0; i < lodDistances.Count; i++)
            lodThresholdsSquared[i] = lodDistances[i] * lodDistances[i];

        foreach (var matrices in lodMatrices)
            matrices.Clear();

        if (enabled && hasCamera && impostorDistance > 0)
        {
            var distanceSquared = impostorDistance * impostorDistance;
            var minCell = ((int)MathF.Floor((camera.X - impostorDistance) / GridCell), (int)MathF.Floor((camera.Z - impostorDistance) / GridCell));
            var maxCell = ((int)MathF.Floor((camera.X + impostorDistance) / GridCell), (int)MathF.Floor((camera.Z + impostorDistance) / GridCell));
            var lastLod = Lods.Count - 1;
            for (int z = minCell.Item2; z <= maxCell.Item2; z++)
            {
                for (int x = minCell.Item1; x <= maxCell.Item1; x++)
                {
                    if (!grid.TryGetValue((x, z), out var cell))
                        continue;
                    foreach (var index in cell)
                    {
                        var instance = instances[index];
                        var dx = instance.Position.X - camera.X;
                        var dz = instance.Position.Z - camera.Z;
                        var d2 = dx * dx + dz * dz;
                        if (d2 >= distanceSquared)
                            continue;
                        var lod = lastLod;
                        for (int level = 0; level < lodThresholdsSquared.Length; level++)
                        {
                            if (d2 < lodThresholdsSquared[level])
                            {
                                lod = level;
                                break;
                            }
                        }
                        lodMatrices[lod].Add(Matrix.Scaling(instance.Scale) * Matrix.RotationY(instance.Rotation) * Matrix.Translation(instance.Position));
                    }
                }
            }
        }

        if (LastLodCounts.Length != Lods.Count)
            LastLodCounts = new int[Lods.Count];
        for (int lod = 0; lod < Lods.Count; lod++)
        {
            var matrices = lodMatrices[lod];
            var entity = Entities[lod];
            var model = entity.Get<ModelComponent>();
            var instancing = entity.Get<InstancingComponent>();
            LastLodCounts[lod] = matrices.Count;
            var visible = matrices.Count > 0;
            if (visible)
                ((InstancingUserArray)instancing.Type).UpdateWorldMatrices(matrices.Items, matrices.Count);
            instancing.Enabled = visible;
            model.Enabled = visible;
            model.RenderGroup = renderGroup;
            model.IsShadowCaster = Definition.CastShadows;
        }

        var impostors = ImpostorEntity.Get<ModelComponent>();
        impostors.Enabled = enabled && Atlas != null && instances.Count > 0;
        impostors.RenderGroup = renderGroup;
        impostors.IsShadowCaster = Definition.ImpostorsCastShadows;
    }

    /// <summary>
    /// Bakes the impostor atlas when needed and keeps the impostor buffers and parameters up to date (render thread).
    /// </summary>
    public void Draw(RenderDrawContext context, TerrainImpostorBaker baker, Vector3 camera, bool impostorPreview)
    {
        if (!bakeAttempted && Lods.Count > 0)
        {
            // Once, from the most detailed level, even if it fails (rather than every frame)
            bakeAttempted = true;
            Atlas = baker.Bake(context, Lods[0], Math.Clamp(Views, 2, 32), Math.Clamp(Resolution, 16, 2048));
            impostorInstancesDirty = true;
        }
        if (Atlas == null)
            return;

        if (impostorInstancesDirty)
        {
            impostorInstancesDirty = false;
            impostorInstances?.Dispose();
            impostorInstances = null;
            impostorMesh.Draw.DrawCount = instances.Count * 6;
            if (instances.Count > 0)
            {
                var packed = new Vector4[instances.Count];
                for (int i = 0; i < instances.Count; i++)
                {
                    var instance = instances[i];
                    packed[i] = new Vector4(instance.Position, PackHalves(instance.Scale, instance.Rotation));
                }
                impostorInstances = Buffer.Structured.New(device, packed);
                impostorInstances.Name = "TerrainImpostorInstances";
            }
        }

        var parameters = ImpostorMaterial.Passes[0].Parameters;
        if (impostorInstances != null)
            parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorInstances, impostorInstances);
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorWorldSize, Atlas.WorldSize);
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorCenterOffset, Atlas.CenterOffset);
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorGridSize, (float)Atlas.GridSize);
        var impostorDistance = impostorPreview ? 0 : Math.Max(Definition.ImpostorDistance, 0);
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorFadeStart, impostorDistance);
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorFadeEnd, Math.Max(impostorDistance - Definition.ImpostorFadeRange, 0));
        parameters.Set(MaterialTerrainImpostorDisplacementKeys.ImpostorCameraPosition, camera);
        parameters.Set(ComputeColorTerrainImpostorDiffuseKeys.ImpostorDiffuseAtlas, Atlas.Diffuse);
        parameters.Set(ComputeColorTerrainImpostorNormalKeys.ImpostorNormalAtlas, Atlas.Normal);
    }

    // Two halves in the bits of a float (the shader unpacks them)
    private static float PackHalves(float low, float high)
    {
        var packed = ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16) | BitConverter.HalfToUInt16Bits((Half)low);
        return BitConverter.Int32BitsToSingle((int)packed);
    }

    public void Dispose()
    {
        impostorInstances?.Dispose();
        impostorInstances = null;
        Atlas?.Dispose();
        Atlas = null;
    }
}

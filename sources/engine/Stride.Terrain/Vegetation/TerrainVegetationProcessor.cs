// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Draws the grass layers and vegetation types of <see cref="TerrainComponent"/>s: creates the entities drawing them (root
/// entities of the terrain's scene, not saved), follows the definitions and the terrain data, and picks the levels of detail.
/// Created by the <see cref="TerrainProcessor"/> of each visibility group.
/// </summary>
public sealed class TerrainVegetationProcessor : EntityProcessor<TerrainComponent, TerrainVegetationProcessor.TerrainVegetation>
{
    private static readonly Stride.Core.Diagnostics.Logger Log = Stride.Core.Diagnostics.GlobalLogger.GetLogger("TerrainVegetation");

    private GraphicsDevice graphicsDevice;
    private ContentManager content;
    private TerrainVegetationState state;
    private TerrainImpostorBaker baker;

    public TerrainVegetationProcessor() : base(typeof(TransformComponent))
    {
        // After the terrain processor (render objects) and before the model processors pick up the entities' changes
        Order = -100;
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    /// <summary>
    /// The vegetation of a terrain (null if the terrain isn't processed).
    /// </summary>
    public TerrainVegetation GetVegetation(TerrainComponent component) => ComponentDatas.TryGetValue(component, out var data) ? data : null;

    protected override void OnSystemAdd()
    {
        graphicsDevice = Services.GetSafeServiceAs<IGraphicsDeviceService>().GraphicsDevice;
        content = Services.GetService<ContentManager>();
        state = new TerrainVegetationState();
    }

    // The visibility group is set once the processor is added: the render feature finds the state in its tags
    private void RegisterState()
    {
        if (VisibilityGroup != null && !VisibilityGroup.Tags.ContainsKey(TerrainVegetationState.Key))
            VisibilityGroup.Tags.Set(TerrainVegetationState.Key, state);
    }

    protected override void OnSystemRemove()
    {
        foreach (var data in ComponentDatas.Values)
            data.Dispose(state);
        VisibilityGroup?.Tags.Remove(TerrainVegetationState.Key);
        baker?.Dispose();
        baker = null;
    }

    protected override TerrainVegetation GenerateComponentData(Entity entity, TerrainComponent component) => new(component);

    protected override bool IsAssociatedDataValid(Entity entity, TerrainComponent component, TerrainVegetation associatedData) => associatedData.Component == component;

    protected override void OnEntityComponentRemoved(Entity entity, TerrainComponent component, TerrainVegetation data) => data.Dispose(state);

    public override void Update(GameTime time)
    {
        RegisterState();
        var modelProcessor = EntityManager.GetProcessor<ModelRenderProcessor>();
        var terrainProcessor = EntityManager.GetProcessor<TerrainProcessor>();
        foreach (var data in ComponentDatas.Values)
        {
            try
            {
                data.Update(graphicsDevice, content, state, modelProcessor, terrainProcessor);
            }
            catch (Exception e)
            {
                Log.Error($"Vegetation of terrain [{data.Component.Entity?.Name}] failed", e);
                data.Dispose(state);
            }
        }
    }

    public override void Draw(RenderContext context)
    {
        RegisterState();
        EnsureRenderFeature(VisibilityGroup.RenderSystem);
        var drawContext = context.GetThreadContext();
        foreach (var data in ComponentDatas.Values)
        {
            foreach (var type in data.Types)
            {
                if (type == null)
                    continue;
                baker ??= new TerrainImpostorBaker(Services, graphicsDevice);
                type.Draw(drawContext, baker, state.CameraPosition, data.Component.ImpostorPreview);
            }
        }
    }

    private static void EnsureRenderFeature(RenderSystem renderSystem)
    {
        foreach (var feature in renderSystem.RenderFeatures)
        {
            if (feature is MeshRenderFeature meshRenderFeature && !meshRenderFeature.RenderFeatures.Any(x => x is TerrainGrassRenderFeature))
                meshRenderFeature.RenderFeatures.Add(new TerrainGrassRenderFeature());
        }
    }

    /// <summary>
    /// The vegetation of a terrain.
    /// </summary>
    public sealed class TerrainVegetation
    {
        private readonly List<TerrainGrassVariant> grass = new();
        private readonly List<TerrainVegetationTypeRenderer> types = new();
        private Scene scene;
        private TerrainData data;
        private Vector3 origin;
        private bool instancesDirty;

        internal TerrainVegetation(TerrainComponent component)
        {
            Component = component;
        }

        public TerrainComponent Component { get; }

        internal IReadOnlyList<TerrainVegetationTypeRenderer> Types => types;

        /// <summary>
        /// Levels of detail, triangle counts and instances of each vegetation type, for the editor.
        /// </summary>
        public IReadOnlyList<TerrainVegetationTypeInfo> GetTypeInfos()
        {
            return types.Where(x => x != null).Select(x => new TerrainVegetationTypeInfo(
                x.Definition.Name,
                x.Lods.Select(lod => lod.Meshes.Sum(mesh => (mesh.Draw?.DrawCount ?? 0) / 3)).ToArray(),
                x.LastLodCounts.ToArray(),
                x.InstanceCount,
                x.Atlas != null,
                x.Atlas?.GridSize ?? 0,
                x.Atlas?.FrameResolution ?? 0)).ToList();
        }

        internal void Update(GraphicsDevice device, ContentManager content, TerrainVegetationState state, ModelRenderProcessor modelProcessor, TerrainProcessor terrainProcessor)
        {
            var component = Component;
            var enabled = component.Enabled;

            // The data the instances are placed from
            if (component.Terrain != data)
            {
                if (data != null)
                    data.Changed -= OnDataChanged;
                data = component.Terrain;
                if (data != null)
                    data.Changed += OnDataChanged;
                instancesDirty = true;
            }

            var entityScene = component.Entity?.Scene;
            if (entityScene != scene)
            {
                RemoveEntities();
                scene = entityScene;
                AddEntities();
            }

            SyncGrass(device, state);
            SyncTypes(device, content);

            var newOrigin = component.Origin;
            if (newOrigin != origin)
            {
                origin = newOrigin;
                instancesDirty = true;
            }
            if (instancesDirty && data != null && data.Resolution >= 2)
            {
                instancesDirty = false;
                for (int i = 0; i < types.Count; i++)
                    types[i]?.SetInstances(data, origin, i);
            }

            // Grass: each model's entity, mapped to its render model for the render feature
            var terrain = terrainProcessor?.GetRenderObject(component);
            foreach (var variant in grass)
            {
                var model = variant.ModelComponent;
                model.Enabled = enabled && terrain != null;
                model.RenderGroup = component.RenderGroup;
                model.IsShadowCaster = variant.Layer.CastShadows;
                variant.Terrain = terrain;
                if (modelProcessor != null && modelProcessor.RenderModels.TryGetValue(model, out var renderModel))
                    state.GrassModels[renderModel] = variant;
            }

            foreach (var type in types)
                type?.Update(state.CameraPosition, state.HasCamera, component.ImpostorPreview, enabled, component.RenderGroup);
        }

        private void OnDataChanged(object sender, TerrainDataChangedEventArgs e)
        {
            if ((e.Flags & (TerrainChangeFlags.Vegetation | TerrainChangeFlags.Heights)) != 0)
                instancesDirty = true;
        }

        private void SyncGrass(GraphicsDevice device, TerrainVegetationState state)
        {
            // Rebuilt when a layer or its models change (the other settings are read as they are)
            var layers = Component.Grass;
            var expected = new List<(TerrainGrassLayer Layer, int Index, Model Model, int Variant, int Count)>();
            for (int i = 0; i < layers.Count && i < TerrainData.MaxGrassLayers; i++)
            {
                var models = layers[i].Models.Where(x => x != null).ToList();
                for (int v = 0; v < models.Count; v++)
                    expected.Add((layers[i], i, models[v], v, models.Count));
            }

            var same = expected.Count == grass.Count;
            for (int i = 0; same && i < expected.Count; i++)
            {
                var (layer, index, model, variant, count) = expected[i];
                var current = grass[i];
                same = current.Layer == layer && current.LayerIndex == index && current.Source == model && current.Variant == variant && current.VariantCount == count;
            }
            if (same)
                return;

            ClearGrass(state);
            foreach (var (layer, index, model, variant, count) in expected)
            {
                if (model.Meshes.Count == 0)
                    continue;
                var grassVariant = new TerrainGrassVariant(device, layer, index, model, variant, count);
                grass.Add(grassVariant);
                scene?.Entities.Add(grassVariant.Entity);
            }
        }

        private void SyncTypes(GraphicsDevice device, ContentManager content)
        {
            var definitions = Component.Vegetation;
            var same = definitions.Count == types.Count;
            for (int i = 0; same && i < types.Count; i++)
            {
                var type = types[i];
                var definition = definitions[i];
                same = type == null
                    ? !IsDrawable(definition)
                    : type.Definition == definition && type.Model == definition?.Model && type.Views == definition.ImpostorViews && type.Resolution == definition.ImpostorResolution;
            }
            if (same)
                return;

            ClearTypes();
            foreach (var definition in definitions)
            {
                // A type without a model (or whose model isn't loaded yet) keeps its index, as the instances refer to types
                // by index, but draws nothing
                types.Add(IsDrawable(definition) ? new TerrainVegetationTypeRenderer(device, content, definition) : null);
            }
            AddTypeEntities();
            instancesDirty = true;
        }

        private static bool IsDrawable(TerrainVegetationType definition) => definition?.Model != null && definition.Model.Meshes.Count > 0;

        private void AddEntities()
        {
            if (scene == null)
                return;
            foreach (var variant in grass)
                scene.Entities.Add(variant.Entity);
            AddTypeEntities();
        }

        private void AddTypeEntities()
        {
            if (scene == null)
                return;
            foreach (var type in types)
            {
                foreach (var entity in type?.Entities ?? [])
                {
                    if (entity.Scene == null)
                        scene.Entities.Add(entity);
                }
            }
        }

        private void RemoveEntities()
        {
            foreach (var variant in grass)
                variant.Entity.Scene = null;
            foreach (var type in types)
            {
                foreach (var entity in type?.Entities ?? [])
                    entity.Scene = null;
            }
        }

        private void ClearGrass(TerrainVegetationState state)
        {
            foreach (var variant in grass)
            {
                variant.Entity.Scene = null;
                foreach (var key in state.GrassModels.Where(x => x.Value == variant).Select(x => x.Key).ToList())
                    state.GrassModels.Remove(key);
                variant.Dispose();
            }
            grass.Clear();
        }

        private void ClearTypes()
        {
            foreach (var type in types)
            {
                foreach (var entity in type?.Entities ?? [])
                    entity.Scene = null;
                type?.Dispose();
            }
            types.Clear();
        }

        internal void Dispose(TerrainVegetationState state)
        {
            if (data != null)
                data.Changed -= OnDataChanged;
            data = null;
            if (state != null)
                ClearGrass(state);
            ClearTypes();
            scene = null;
        }
    }
}

/// <summary>
/// What a vegetation type draws (see <see cref="TerrainVegetationProcessor.TerrainVegetation.GetTypeInfos"/>).
/// </summary>
/// <param name="Name">The type's name.</param>
/// <param name="LodTriangles">Triangles of each level of detail, most detailed first.</param>
/// <param name="LodInstances">Instances drawn at each level of detail in the last frame.</param>
/// <param name="Instances">Instances placed.</param>
/// <param name="HasImpostor">Whether the impostor atlas is baked.</param>
/// <param name="ImpostorViews">Views along each side of the atlas.</param>
/// <param name="ImpostorResolution">Resolution of each view.</param>
public sealed record TerrainVegetationTypeInfo(string Name, int[] LodTriangles, int[] LodInstances, int Instances, bool HasImpostor, int ImpostorViews, int ImpostorResolution);

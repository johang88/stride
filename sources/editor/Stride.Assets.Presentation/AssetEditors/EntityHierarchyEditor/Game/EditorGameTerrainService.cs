using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Core.Annotations;
using Stride.Core.BuildEngine;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Core.Mathematics;
using Stride.Core.Collections;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.Services;
using Stride.Core.Assets.Editor.Services;
using Stride.Terrain;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Graphics.GeometricPrimitives;
using Stride.Extensions;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Assets.Terrain;
using Microsoft.CodeAnalysis.Differencing;
using Stride.Terrain.Tools;
using ICSharpCode.AvalonEdit.Editing;
using Stride.Rendering.Materials;
using Stride.Graphics;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Terrain.BlendTypes;
using Stride.Core.Shaders.Ast;

namespace Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game
{
    /// <summary>
    /// Manages in editor editing of height maps or at least that's the plan :)
    /// </summary>
    public class EditorGameTerrainService : EditorGameMouseServiceBase
    {
        public const RenderGroupMask TerrainGizmoGroupMask = RenderGroupMask.Group18;
        public const RenderGroup TerrainGizmoGroup = RenderGroup.Group18;

        private EntityHierarchyEditorViewModel entityHierarchyEditorViewModel;
        private EntityHierarchyEditorGame game;

        public override bool IsControllingMouse { get; protected set; }

        private HashSet<Entity> terrainEntities = new HashSet<Entity>();
        private TerrainToolsComponent _terrainTools = null;

        private Entity gizmoEntity = null;

        public EditorGameTerrainService(EntityHierarchyEditorViewModel entityHierarchyEditorViewModel)
        {
            this.entityHierarchyEditorViewModel = entityHierarchyEditorViewModel ?? throw new ArgumentNullException(nameof(entityHierarchyEditorViewModel));
        }

        protected override Task<bool> Initialize([NotNull] EditorServiceGame editorGame)
        {
            game = (EntityHierarchyEditorGame)editorGame;

            var selectionService = game.EditorServices.Get<IEditorGameEntitySelectionService>();
            selectionService.SelectionUpdated += SelectionService_SelectionUpdated;

            var editorScene = game.EditorScene;

            // Setup gizmo rendering
            //var terrainGizmoRenderStage = new RenderStage("Terrain Gizmo", "Main") { SortMode = new BackToFrontSortMode() };
            //game.EditorSceneSystem.GraphicsCompositor.RenderStages.Add(terrainGizmoRenderStage);

            //var meshRenderFeature = game.EditorSceneSystem.GraphicsCompositor.RenderFeatures.OfType<MeshRenderFeature>().First();
            //meshRenderFeature.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector
            //{
            //    EffectName = EditorGraphicsCompositorHelper.EditorForwardShadingEffect,
            //    RenderGroup = TerrainGizmoGroupMask,
            //    RenderStage = terrainGizmoRenderStage
            //});

            //meshRenderFeature.PipelineProcessors.Add(new AlphaBlendPipelineProcessor { RenderStage = terrainGizmoRenderStage });

            //var editorCompositor = (EditorTopLevelCompositor)game.EditorSceneSystem.GraphicsCompositor.Game;
            //editorCompositor.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = terrainGizmoRenderStage });
            var terrainMainGizmoRenderStage = new RenderStage("Terrain Painter Gizmo", "Main") { SortMode = new BackToFrontSortMode() };
            game.EditorSceneSystem.GraphicsCompositor.RenderStages.Add(terrainMainGizmoRenderStage);

            var meshRenderFeature = game.EditorSceneSystem.GraphicsCompositor.RenderFeatures.OfType<MeshRenderFeature>().First();
            meshRenderFeature.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector
            {
                EffectName = EditorGraphicsCompositorHelper.EditorForwardShadingEffect,
                RenderGroup = TerrainGizmoGroupMask,
                RenderStage = terrainMainGizmoRenderStage
            });

            meshRenderFeature.PipelineProcessors.Add(new AlphaBlendPipelineProcessor { RenderStage = terrainMainGizmoRenderStage });

            var editorCompositor = (EditorTopLevelCompositor)game.EditorSceneSystem.GraphicsCompositor.Game;
            editorCompositor.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = terrainMainGizmoRenderStage });

            MicrothreadLocalDatabases.MountCommonDatabase();

            // Create gizmo
            var material = Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Emissive = new MaterialEmissiveMapFeature(new ComputeColor { Value = Color4.PremultiplyAlpha(new Color4(0, 1.0f, 0, 0.35f)) })
                    {
                        UseAlpha = true
                    },
                    Transparency = new MaterialTransparencyBlendFeature(),
                    CullMode = CullMode.None
                }
            });

            gizmoEntity = new Entity("Terrain Gizmo");
            gizmoEntity.Components.Add(new ModelComponent
            {
                RenderGroup = TerrainGizmoGroup,
                Model = new Model
                {
                    material, new Mesh { Draw = GeometricPrimitive.Sphere.New(game.GraphicsDevice, 1.0f).ToMeshDraw() }
                }
            });

            editorScene.Entities.Add(gizmoEntity);

            game.Script.AddTask(Execute);

            return Task.FromResult(true);
        }

        private void SelectionService_SelectionUpdated(object sender, EntitySelectionEventArgs e)
        {
            if (e.NewSelection.Count == 1)
            {
                _terrainTools = e.NewSelection.First().Get<TerrainToolsComponent>();
            }
            else
            {
                _terrainTools = null;
            }
        }

        public override void RegisterScene(Scene scene)
        {
            base.RegisterScene(scene);

            game.SceneSystem.SceneInstance.EntityAdded += (_, e) =>
            {
                if (e.Get<TerrainComponent>() != null)
                    terrainEntities.Add(e);
            };
            game.SceneSystem.SceneInstance.EntityRemoved += (_, e) => terrainEntities.Remove(e);
            game.SceneSystem.SceneInstance.ComponentChanged += (sender, e) =>
            {
                if (e.PreviousComponent != null)
                    terrainEntities.Remove(e.PreviousComponent.Entity);

                if (e.NewComponent is TerrainComponent)
                    terrainEntities.Add(e.NewComponent.Entity);
            };
        }

        private async Task Execute()
        {
            MicrothreadLocalDatabases.MountCommonDatabase();

            var editedTerrains = new HashSet<TerrainComponent>();
            var intersectionPoint = Vector3.Zero;

            var invalidationData = new ToolInvalidationData
            {
                ModifiedIndices = new HashSet<int>()
            };

            var session = entityHierarchyEditorViewModel.Session;

            while (game.IsRunning)
            {
                await game.Script.NextFrame();

                var dt = (float)game.UpdateTime.Elapsed.TotalSeconds;

                // Find intersecting terrain
                var mousePosition = game.Input.MousePosition;
                var cameraService = game.EditorServices.Get<IEditorGameCameraService>();

                CreateWorldSpaceCameraRay(mousePosition, cameraService.Component, out var rayStartWS, out var rayEndWS);

                TerrainComponent editableTerain = null;
                if (_terrainTools != null)
                {
                    foreach (var entity in terrainEntities)
                    {
                        var terrainComponent = entity.Get<TerrainComponent>();

                        // Can happen in some reload scenarios
                        if (terrainComponent.Terrain == null || terrainComponent.Terrain.Heightmap == null)
                            continue;

                        var worldMatrix = entity.Transform.WorldMatrix;
                        Matrix.Invert(ref worldMatrix, out var invWorldMatrix);

                        // Transform ray to object space
                        Vector3.Transform(ref rayStartWS, ref invWorldMatrix, out Vector3 rayStartOS);
                        Vector3.Transform(ref rayEndWS, ref invWorldMatrix, out Vector3 rayEndOS);

                        var direction = rayEndOS - rayStartOS;
                        direction.Normalize();

                        var ray = new Ray(rayStartOS, direction);

                        // TODO: Should check if brush intersects for nice overlaps?
                        if (terrainComponent.Terrain.Intersects(ray, out intersectionPoint))
                        {
                            editableTerain = terrainComponent;
                        }
                    }

                    // Update Gizmo position and size
                    if (editableTerain != null)
                    {
                        gizmoEntity.Transform.Position = Vector3.Transform(intersectionPoint, editableTerain.Entity.Transform.WorldMatrix).XYZ();

                        var relativeSizeX = (_terrainTools.Size / (float)editableTerain.Terrain.Resolution.X) * editableTerain.Terrain.Size.X;
                        var relativeSizeZ = (_terrainTools.Size / (float)editableTerain.Terrain.Resolution.Y) * editableTerain.Terrain.Size.Z;

                        gizmoEntity.Transform.Scale = new Vector3(relativeSizeX, Math.Max(relativeSizeX, relativeSizeZ), relativeSizeZ);
                    }
                }

                gizmoEntity.Get<ModelComponent>().Enabled = editableTerain != null;

                // Paint terrain
                if (editableTerain != null && game.Input.IsMouseButtonDown(Input.MouseButton.Left))
                {
                    IsControllingMouse = true;

                    if (!editableTerain.Terrain.PositionToHeightMapIndex(intersectionPoint.X, intersectionPoint.Z, out var point))
                        continue; // This should not happen if the ray trace was successfull :/ 

                    var processor = game.SceneSystem.SceneInstance.GetProcessor<TerrainProcessor>();

                    var strength = 1.0f;
                    if (game.Input.IsKeyDown(Input.Keys.LeftShift))
                        strength = -1.0f;

                    if (strength < 0.0f && _terrainTools.Tool is SetHeight setHeightTool)
                    {
                        // Quantum is beautiful ... :)
                        //var componentVM = GetComponentForViewModel<TerrainComponent>(editableTerain.Entity);
                        //var assetNode = entityHierarchyEditorViewModel.NodeContainer.GetOrCreateNode(componentVM);
                        //var toolsNode = assetNode[nameof(TerrainComponent.Tools)].Target;
                        //var toolNode = toolsNode[nameof(TerrainTools.Tool)].Target;
                        //var heightNode = toolNode[nameof(SetHeight.Height)];
                        //heightNode.Update(intersectionPoint.Y);
                        // TODO: This crasches ...
                    }
                    else
                    {
                        _terrainTools.Apply(processor, editableTerain, point, strength * dt, invalidationData);
                    }

                    invalidationData.ModifiedIndices.Clear(); // Splat map status stays, indices can be cleared
                    editedTerrains.Add(editableTerain);
                }
                else if (IsControllingMouse)
                {
                    IsControllingMouse = false;

                    // TODO: The selection system triggers if we don't move the cursor, maybe something can be done to prevent this

                    // Update asset data, this too is quite slow, but we have at least made it somewhat faster
                    using (var transaction = session.UndoRedoService.CreateTransaction())
                    {
                        session.UndoRedoService.SetName(transaction, "Modify terrain heights");
                        foreach (var terrainComponent in editedTerrains)
                        {
                            var terrain = terrainComponent.Terrain;
                            var entity = terrainComponent.Entity;

                            // Apparently things are not in sync, so get the entity view model and fetch the components from there
                            // We wont be able to get the correct asset reference otherwise
                            var terrainComponentVM = GetComponentForViewModel<TerrainComponent>(entity);
                            var terrainAssetVM = ContentReferenceHelper.GetReferenceTarget(session, terrainComponentVM.Terrain);

                            var assetNode = session.AssetNodeContainer.GetOrCreateNode(terrainAssetVM.Asset);

                            // For now this seems to be the fastest option :/
                            // Unless we can make quantum faaaaster, which I do believe should be possible ... somehow ... maybe
                            assetNode[nameof(TerrainDataAsset.Heightmap)].Update(terrain.Heightmap);

                            // TODO: Only update layer splat maps if needed
                            if (invalidationData.SplatMaps)
                            {
                                var layersNode = assetNode[nameof(TerrainDataAsset.Layers)].Target;
                                for (var i = 0; i < terrain.Layers.Count; i++)
                                {
                                    if (terrain.Layers[i].Layer.BlendType is TerrainLayerBlendTypeSplatMap)
                                    {
                                        var layerNode = layersNode.IndexedTarget(new NodeIndex(i));
                                        layerNode[nameof(TerrainLayerData.Data)].Update(terrain.Layers[i].Data);
                                    }
                                }
                            }
                        }
                    }

                    invalidationData.SplatMaps = false;
                    invalidationData.ModifiedIndices.Clear();

                    editedTerrains.Clear();
                }
            }
        }

        private TComponent GetComponentForViewModel<TComponent>(Entity entity) where TComponent : EntityComponent
        {
            var controller = entityHierarchyEditorViewModel.Controller;

            // Apparently things are not in sync, so get the entity view model and fetch the components from there
            // We wont be able to get the correct asset reference otherwise
            var entityId = controller.GetAbsoluteId(entity);
            var entityVM = (EntityViewModel)entityHierarchyEditorViewModel.FindPartViewModel(entityId);

            var componentVM = (TComponent)entityVM.Components.First(c => c is TComponent);

            return componentVM;
        }

        private static void CreateWorldSpaceCameraRay(Vector2 screenPos, CameraComponent camera, out Vector3 rayStart, out Vector3 rayEnd)
        {
            var invViewProj = Matrix.Invert(camera.ViewProjectionMatrix);

            Vector3 sPos;
            sPos.X = screenPos.X * 2f - 1f;
            sPos.Y = 1f - screenPos.Y * 2f;

            sPos.Z = 0f;
            var vectorNear = Vector3.Transform(sPos, invViewProj);
            vectorNear /= vectorNear.W;

            sPos.Z = 1f;
            var vectorFar = Vector3.Transform(sPos, invViewProj);
            vectorFar /= vectorFar.W;

            rayStart = vectorNear.XYZ();
            rayEnd = vectorFar.XYZ();
        }
    }
}

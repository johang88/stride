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

        private Entity gizmoEntity = null;

        public float BrushRadius = 8.0f;

        public EditorGameTerrainService(EntityHierarchyEditorViewModel entityHierarchyEditorViewModel)
        {
            this.entityHierarchyEditorViewModel = entityHierarchyEditorViewModel ?? throw new ArgumentNullException(nameof(entityHierarchyEditorViewModel));
        }

        protected override Task<bool> Initialize([NotNull] EditorServiceGame editorGame)
        {
            game = (EntityHierarchyEditorGame)editorGame;

            var editorScene = game.EditorScene;

            // Setup gizmo rendering
            var terrainMainGizmoRenderStage = new RenderStage("TerrainGizmoOpaque", "Main");
            var terrainTransparentGizmoRenderStage = new RenderStage("TerrainGizmoTransparent", "Main") { SortMode = new BackToFrontSortMode() };
            game.EditorSceneSystem.GraphicsCompositor.RenderStages.Add(terrainMainGizmoRenderStage);
            game.EditorSceneSystem.GraphicsCompositor.RenderStages.Add(terrainTransparentGizmoRenderStage);

            var meshRenderFeature = game.EditorSceneSystem.GraphicsCompositor.RenderFeatures.OfType<MeshRenderFeature>().First();
            // Reset all stages for TransformationGrizmoGroup
            meshRenderFeature.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector
            {
                RenderGroup = TerrainGizmoGroupMask,
                //RenderStage = terrainMainGizmoRenderStage
            });
            meshRenderFeature.RenderStageSelectors.Add(new MeshTransparentRenderStageSelector
            {
                EffectName = EditorGraphicsCompositorHelper.EditorForwardShadingEffect,
                RenderGroup = TerrainGizmoGroupMask,
                OpaqueRenderStage = terrainMainGizmoRenderStage,
                TransparentRenderStage = terrainTransparentGizmoRenderStage,
            });
            meshRenderFeature.PipelineProcessors.Add(new MeshPipelineProcessor { TransparentRenderStage = terrainTransparentGizmoRenderStage });

            var editorCompositor = (EditorTopLevelCompositor)game.EditorSceneSystem.GraphicsCompositor.Game;
            editorCompositor.PostGizmoCompositors.Add(new ClearRenderer { ClearFlags = ClearRendererFlags.DepthOnly });
            editorCompositor.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = terrainMainGizmoRenderStage, Name = "Terrain Opaque Gizmos" });
            editorCompositor.PostGizmoCompositors.Add(new SingleStageRenderer { RenderStage = terrainTransparentGizmoRenderStage, Name = "Terrain Transparent Gizmos" });

            MicrothreadLocalDatabases.MountCommonDatabase();

            // Create gizmo
            var material = GizmoUniformColorMaterial.Create(game.GraphicsDevice, new Color(0, 255, 0, 64), false);
            gizmoEntity = new Entity("Terrain Gizmo");
            gizmoEntity.Components.Add(new ModelComponent
            {
                RenderGroup = TerrainGizmoGroup,
                Model = new Model
                {
                    material, new Mesh { Draw = GeometricPrimitive.Sphere.New(game.GraphicsDevice).ToMeshDraw() }
                }
            });

            editorScene.Entities.Add(gizmoEntity);

            game.Script.AddTask(Execute);

            return Task.FromResult(true);
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
            var frameEditedIndices = new HashSet<int>();
            var allEditedIndices = new HashSet<int>();

            while (game.IsRunning)
            {
                await game.Script.NextFrame();

                var dt = (float)game.UpdateTime.Elapsed.TotalSeconds;

                // We should probably check this once I understand how things work
                //if (!IsActive)
                //    continue;

                if (game.Input.IsKeyDown(Input.Keys.OemPlus))
                {
                    BrushRadius += dt * 5;
                }
                else if (game.Input.IsKeyDown(Input.Keys.OemMinus))
                {
                    BrushRadius = Math.Max(1.0f, BrushRadius - dt * 5);
                }

                // Find intersecting terrain
                var mousePosition = game.Input.MousePosition;
                var cameraService = game.EditorServices.Get<IEditorGameCameraService>();

                CreateWorldSpaceCameraRay(mousePosition, cameraService.Component, out var rayStartWS, out var rayEndWS);

                TerrainComponent editableTerain = null;
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

                    if (terrainComponent.Intersects(ray, out intersectionPoint))
                    {
                        editableTerain = terrainComponent;
                    }
                }

                // Update Gizmo
                if (editableTerain != null)
                {
                    gizmoEntity.Transform.Position = Vector3.Transform(intersectionPoint, editableTerain.Entity.Transform.WorldMatrix).XYZ();
                    gizmoEntity.Transform.Scale = new Vector3(BrushRadius, BrushRadius, BrushRadius);
                }

                gizmoEntity.Get<ModelComponent>().Enabled = editableTerain != null;

                // Paint terrain
                if (editableTerain != null && game.Input.IsMouseButtonDown(Input.MouseButton.Left))
                {
                    IsControllingMouse = true;

                    if (!editableTerain.PositionToHeightMapIndex(intersectionPoint.X, intersectionPoint.Z, out var point))
                        continue; // This should not happen if the ray trace was successfull :/ 

                    var terrain = editableTerain.Terrain;

                    // Calculate radius relative to height map

                    // Better hope those heightmaps are square :D
                    var radius = (int)(Math.Min(1.0f, BrushRadius / editableTerain.Size.X) * terrain.Size.X);

                    var raise = true;
                    var strength = 500.0f;
                    if (game.Input.IsKeyDown(Input.Keys.LeftShift))
                        raise = false;

                    for (var y = -radius; y < radius; y++)
                    {
                        for (var x = -radius; x < radius; x++)
                        {
                            var xi = point.X + x;
                            var yi = point.Y + y;

                            if (xi < 0 || yi < 0 || xi >= terrain.Size.X || yi >= terrain.Size.Y)
                                continue;

                            var distance = Math.Sqrt(y * y + x * x);
                            var factor = radius - distance;

                            if (factor <= 0.0f)
                                continue;

                            var index = yi * terrain.Size.X + xi;

                            var height = terrain.Heightmap[index];
                            var distortion = (strength * factor * dt);

                            if (!raise)
                            {
                                if (distortion > height)
                                {
                                    height = 0;
                                }
                                else
                                {
                                    height -= (ushort)distortion;
                                }
                            }
                            else
                            {
                                height += (ushort)distortion;
                            }

                            terrain.Heightmap[index] = height;
                            
                            frameEditedIndices.Add(index);
                            allEditedIndices.Add(index);
                        }
                    }

                    // Notify processor so that the mesh is updated
                    var processor = game.SceneSystem.SceneInstance.GetProcessor<TerrainProcessor>();
                    processor.Invalidate(editableTerain, frameEditedIndices);
                    frameEditedIndices.Clear();

                    //editableTerain.Invalidate(true, false);
                    editedTerrains.Add(editableTerain);
                }
                else if (IsControllingMouse)
                {
                    IsControllingMouse = false;

                    // TODO: The selection system triggers if we don't move the cursor, maybe something can be done to prevent this
                    // TODO: Update asset data here 

                    // Apparently things are not in sync, so get the entity view model and fetch the components from there
                    // We wont be able to get the correct asset reference otherwise

                    // Update normals, this is slow so only done after editing
                    foreach (var terrainComponent in editedTerrains)
                    {
                        // TODO: Currently not needed as quantum invalidates the entire asset reference, which will force the mesh to be recreated
                        //terrainComponent.Invalidate(true, true);
                    }

                    // Update asset data, this too is quite slow
                    var session = entityHierarchyEditorViewModel.Session;
                    var controller = entityHierarchyEditorViewModel.Controller;

                    using (var transaction = session.UndoRedoService.CreateTransaction())
                    {
                        session.UndoRedoService.SetName(transaction, "Modify terrain heights");
                        foreach (var terrainComponent in editedTerrains)
                        {
                            var terrain = terrainComponent.Terrain;
                            var entity = terrainComponent.Entity;

                            var entityId = controller.GetAbsoluteId(entity);
                            var entityVM = (EntityViewModel)entityHierarchyEditorViewModel.FindPartViewModel(entityId);

                            var terrainComponentVM = (TerrainComponent)entityVM.Components.First(c => c is TerrainComponent);
                            var terrainAssetVM = ContentReferenceHelper.GetReferenceTarget(session, terrainComponentVM.Terrain);

                            var assetNode = session.AssetNodeContainer.GetOrCreateNode(terrainAssetVM.Asset);
                            var heightmapNode = (MemberNode)assetNode.TryGetChild(nameof(TerrainData.Heightmap));
                            var targetNode = (Core.Quantum.ObjectNode)heightmapNode.Target;

                            //targetNode.UpdateValues(allEditedIndices.Select(i => (new NodeIndex(i), (object)terrain.Heightmap[i])));
                            // For now this seems to be the fastest option :/
                            // Unless we can make quantum faaaaster, which I do believe should be possible ... somehow
                            assetNode[nameof(TerrainDataAsset.Heightmap)].Update(terrain.Heightmap);
                        }
                    }

                    editedTerrains.Clear();
                    allEditedIndices.Clear();
                }


                // Apparently things are not in sync, so get the entity view model and fetch the components from there
                // We wont be able to get the correct asset reference otherwise
                //var entityId = controller.GetAbsoluteId(entity);
                //var entityVM = (EntityViewModel)entityHierarchyEditorViewModel.FindPartViewModel(entityId);

                //var terrainComponent = (TerrainComponent)entityVM.Components.First(c => c is TerrainComponent);


                // TODO: Store previous mouse coordinates and use old indexes if mouse have not moved, should make editing more pleasant as intersection
                // point will move when raising / lowering terrain ... 
                // Or maybe it will work better when we have a gizmo

                //using (var transaction = session.UndoRedoService.CreateTransaction())
                //{


                /*            var entityId = Editor.Controller.GetAbsoluteId(entity);
        if (!SelectableIds.Contains(entityId) || SelectedIds.Contains(entityId))
        return;

        IsControllingMouse = true;
        Editor.Dispatcher.InvokeAsync(() =>
        {
        var viewModel = (EntityHierarchyElementViewModel)Editor.FindPartViewModel(entityId);
        if (viewModel?.IsSelectable == true)
        Editor.SelectedContent.Add(viewModel);
        Editor.Controller.InvokeAsync(() => IsControllingMouse = false);
        });*/
                //var heightmapAssetVM = ContentReferenceHelper.GetReferenceTarget(session, heightmap);

                // Find quantum node
                //var assetNode = session.AssetNodeContainer.GetOrCreateNode(heightmapAssetVM);
                //var index = heightmap.GetHeightIndex((int)intersectionPoint.X, (int)intersectionPoint.Z);

                //var shorts = heightmap.Shorts;
                //shorts[index] += 1;

                //assetNode[nameof(Heightmap.Shorts)].Update(shorts);
                // assetNode[nameof(LightProbeComponent.Coefficients)].Update(result[matchingLightProbe.Entity.Id]);
                //}
            }

            // game.SceneSystem.SceneInstance.EntityAdded += (sender, entity) => entityPicker.CacheEntity(entity, false);
            // return scriptComponent.SceneSystem.SceneInstance.GetProcessor<PhysicsProcessor>()?.Simulation;

            // TODO: Find thing under cursor
            // Trace using physics as there should 
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

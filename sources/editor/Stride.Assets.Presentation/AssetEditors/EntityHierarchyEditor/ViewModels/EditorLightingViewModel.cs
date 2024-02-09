// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Annotations;
using Stride.Core.Collections;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.LightProbes;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Physics;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.ViewModels;

namespace Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels
{
    public class EditorLightingViewModel : DispatcherViewModel
    {
        private readonly IEditorGameController controller;
        private readonly EntityHierarchyEditorViewModel editor;
        private int lightProbeBounces = 1;

        /// <summary>
        /// Initializes a new instance of the <see cref="EditorNavigationViewModel"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider for this view model.</param>
        /// <param name="controller">The controller object for the related editor game.</param>
        public EditorLightingViewModel([NotNull] IViewModelServiceProvider serviceProvider, [NotNull] IEditorGameController controller, [NotNull] EntityHierarchyEditorViewModel editor)
            : base(serviceProvider)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (editor == null) throw new ArgumentNullException(nameof(editor));

            this.controller = controller;
            this.editor = editor;

            RequestLightProbesComputeCommand = new AnonymousTaskCommand(ServiceProvider, () => RebuildLightProbes(LightProbeBounces));
            RequestLightProbesResetCommand = new AnonymousTaskCommand(ServiceProvider, () => RebuildLightProbes(0));
            AutoPlaceLightProbesCommand = new AnonymousTaskCommand(ServiceProvider, () => AutoPlaceLightProbes());

            CaptureCubemapCommand = new AnonymousTaskCommand(ServiceProvider, CaptureCubemap);
        }

        public int LightProbeBounces { get { return lightProbeBounces; } set { SetValue(lightProbeBounces != value, () => lightProbeBounces = value); } }

        public bool LightProbeWireframeVisible { get { return LightProbeService.IsLightProbeVolumesVisible; } set { SetValue(LightProbeWireframeVisible != value, () => LightProbeService.IsLightProbeVolumesVisible = value); } }

        public ICommandBase RequestLightProbesComputeCommand { get; }
        public ICommandBase RequestLightProbesResetCommand { get; }
        public ICommandBase AutoPlaceLightProbesCommand { get; }

        public ICommandBase CaptureCubemapCommand { get; }

        private IEditorGameLightProbeService LightProbeService => controller.GetService<IEditorGameLightProbeService>();

        private IEditorGameCubemapService CubemapService => controller.GetService<IEditorGameCubemapService>();

        private static string AutoLightProbesEntityName = "Light Probes (Auto)";
        private Task AutoPlaceLightProbes()
        {
            using (var transaction = editor.UndoRedoService.CreateTransaction())
            {
                // Find or create parent light probe entity
                var parentEntityViewModel = editor.HierarchyRoot.Children.OfType<EntityViewModel>().FirstOrDefault(x => x.Name == AutoLightProbesEntityName);
                Entity parentEntity = null;
                if (parentEntityViewModel == null)
                {
                    parentEntity = new Entity { Name = AutoLightProbesEntityName };
                }
                else
                {
                    // TODO: Delete existing childrent instead of aborting
                    // User has to manually delete the parent entity first for now ...
                    return Task.CompletedTask;
                }

                // Just refetch it for now, it's not pretty but it works ^^ 
                //parentEntityViewModel = editor.HierarchyRoot.Children.OfType<EntityViewModel>().FirstOrDefault(x => x.Name == AutoLightProbesEntityName);

                // Find the height map of the level, must be at root level for now ..
                // and only one
                // TODO: Don't use the reflection stuff, plugin system would be nice :D
                var terrainComponent = editor.HierarchyRoot.Children.OfType<EntityViewModel>()
                    .SelectMany(x => x.Components)
                    .Where(x => x.GetType().Name == "TerrainComponent")
                    .FirstOrDefault();

                if (terrainComponent == null)
                {
                    // Not much to do here
                    return Task.CompletedTask;
                }

                // Get component properties
                var terrainComponentAssetNode = (IAssetObjectNode)editor.NodeContainer.GetOrCreateNode(terrainComponent);
                terrainComponent = (EntityComponent)terrainComponentAssetNode.GetContent("Game").Retrieve(); // Very pretty ^^

                var size = (float)terrainComponent.GetType().GetProperty("Size").GetValue(terrainComponent);
                var terrainOffset = size / 2.0f;

                var getHeightMethod = terrainComponent.GetType().GetMethod("GetHeightAt");

                // Do the placement
                var probeDistance = 32.0f; // TODO: Should be configureable
                var baseHeightOffset = 2.0f;
                var probeDistanceY = 4.0f;
                var probesPerRow = (int)(size / probeDistance);
                var i = 0;
                for (var z = 0; z < probesPerRow; z++)
                {
                    for (var x = 0; x < probesPerRow; x++)
                    {
                        var position = new Vector3(x * probeDistance, 0, z * probeDistance);
                        position.Y = (float)getHeightMethod.Invoke(terrainComponent, new object[] { position.X, position.Z }) + baseHeightOffset;

                        position.X -= terrainOffset;
                        position.Z -= terrainOffset;

                        position += terrainComponent.Entity.Transform.Position;

                        parentEntity.AddChild(CreateLightProbeEntity(position, i++));
                        parentEntity.AddChild(CreateLightProbeEntity(position + new Vector3(0, probeDistanceY, 0), i++));
                    }
                }

                AssetCollectionItemIdHelper.GenerateMissingItemIds(parentEntity);

                var parent = editor.HierarchyRoot;
                var parentEntityDesign = new EntityDesign(parentEntity);
                var collection = new AssetPartCollection<EntityDesign, Entity> { parentEntityDesign };

                foreach (var childTransform in parentEntity.Transform.Children)
                {
                    collection.Add(new EntityDesign(childTransform.Entity));
                }

                parent.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(collection, parentEntityDesign, (parent.Owner as EntityViewModel)?.AssetSideEntity, parent.Owner.EntityCount);

                editor.UndoRedoService.SetName(transaction, $"Auto place light probes'");
            }

            return Task.CompletedTask;
        }

        private Entity CreateLightProbeEntity(Vector3 position, int index)
        {
            var entity = new Entity { Name = $"Light Probe ({index})" };
            entity.Transform.Position = position;
            entity.Components.Add(new LightProbeComponent());

            return entity;
        }

        private async Task RebuildLightProbes(int bounces)
        {
            // Reset values
            using (var transaction = editor.UndoRedoService.CreateTransaction())
            {
                foreach (var entity in editor.HierarchyRoot.Children.SelectDeep(x => x.Children).OfType<EntityViewModel>())
                {
                    foreach (var lightProbe in entity.Components.OfType<LightProbeComponent>())
                    {
                        // Find this light probe in Quantum
                        var assetNode = editor.NodeContainer.GetOrCreateNode(lightProbe);
                        var zeroCoefficients = new FastList<Color3>();
                        for (int i = 0; i < LightProbeGenerator.LambertHamonicOrder * LightProbeGenerator.LambertHamonicOrder; ++i)
                            zeroCoefficients.Add(default(Color3));
                        assetNode[nameof(LightProbeComponent.Coefficients)].Update(zeroCoefficients);
                    }
                }

                // Update coefficients (just updated to zero)
                await LightProbeService.UpdateLightProbeCoefficients();

                for (int bounce = 0; bounce < bounces; ++bounce)
                {
                    // Compute coefficients
                    var result = await LightProbeService.RequestLightProbesStep();

                    // Copy coefficients back to view model
                    editor.UndoRedoService.SetName(transaction, "Capture LightProbes");
                    foreach (var entity in editor.HierarchyRoot.Children.SelectDeep(x => x.Children).OfType<EntityViewModel>().Where(x => result.ContainsKey(x.AssetSideEntity.Id)))
                    {
                        // TODO: Use LightProbe Id instead of entity id once copy/paste and duplicate properly remap them
                        var matchingLightProbe = entity.Components.OfType<LightProbeComponent>().FirstOrDefault();
                        if (matchingLightProbe != null)
                        {
                            // Find this light probe in Quantum
                            var assetNode = editor.NodeContainer.GetOrCreateNode(matchingLightProbe);
                            assetNode[nameof(LightProbeComponent.Coefficients)].Update(result[matchingLightProbe.Entity.Id]);
                        }
                    }

                    // Update coefficients
                    await LightProbeService.UpdateLightProbeCoefficients();
                }
            }
        }

        private async Task CaptureCubemap()
        {
            var filepath = await ServiceProvider.Get<IDialogService>().SaveFilePickerAsync(
                editor.Session.SolutionPath?.GetFullDirectory().ToWindowsPath(),
                [new FilePickerFilter("DDS texture") { Patterns = ["*.dds"] }],
                "dds");
            if (filepath is not null)
            {
                // Capture cubemap
                using var image = await CubemapService.CaptureCubemap();
                // And save it
                using var file = File.Create(filepath);
                image.Save(file, ImageFileType.Dds);
            }
        }
    }
}

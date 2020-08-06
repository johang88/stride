using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Terrain;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Editor.Thumbnails;
using Stride.Engine;
using Stride.Rendering;
using Stride.Rendering.ProceduralModels;
using Stride.Terrain;
using Stride.Core.Mathematics;

namespace Stride.Assets.Presentation.Thumbnails
{
    [AssetCompiler(typeof(TerrainLayerAsset), typeof(ThumbnailCompilationContext))]
    public class TerrainLayerThumbnailCompiler : ThumbnailCompilerBase<TerrainLayerAsset>
    {
        public TerrainLayerThumbnailCompiler()
        {
            IsStatic = false;
        }

        protected override void CompileThumbnail(ThumbnailCompilerContext context, string thumbnailStorageUrl, AssetItem assetItem, Package originalPackage, AssetCompilerResult result)
        {
            result.BuildSteps.Add(new ThumbnailBuildStep(new TerrainLayerThumbnailBuildCommand(context, thumbnailStorageUrl, assetItem, originalPackage, new ThumbnailCommandParameters(Asset, thumbnailStorageUrl, context.ThumbnailResolution))));
        }

        /// <summary>
        /// Command used to build the thumbnail of the texture in the storage
        /// </summary>
        private class TerrainLayerThumbnailBuildCommand : ThumbnailFromEntityCommand<TerrainLayer>
        {
            private const string EditorMaterialPreviewEffect = "StrideEditorMaterialPreviewEffect";

            private Model model;

            public TerrainLayerThumbnailBuildCommand(ThumbnailCompilerContext context, string url, AssetItem assetItem, IAssetFinder assetFinder, ThumbnailCommandParameters description)
                : base(context, assetItem, assetFinder, url, description)
            {
            }

            protected override string ModelEffectName => EditorMaterialPreviewEffect;

            protected override Entity CreateEntity()
            {
                // create a sphere model to display the material
                var proceduralModel = new ProceduralModelDescriptor { Type = new SphereProceduralModel { MaterialInstance = { Material = LoadedAsset.Material } } };
                model = proceduralModel.GenerateModel(Services);

                // create the entity, create and set the model component
                var materialEntity = new Entity { Name = "thumbnail Entity of terrain layer: " + AssetUrl };
                materialEntity.Add(new ModelComponent { Model = model });

                return materialEntity;
            }

            protected override void AdjustEntity()
            {
                base.AdjustEntity();

                // override the rotation so that the part of the model facing the screen display the center of the material by default and not the extremities
                Entity.Transform.Rotation = Quaternion.RotationY(MathUtil.Pi);
            }

            protected override void DestroyScene(Scene scene)
            {
                // TODO dispose resources allocated by the procedural model "model.Dispose"
                base.DestroyScene(scene);
            }
        }
    }
}

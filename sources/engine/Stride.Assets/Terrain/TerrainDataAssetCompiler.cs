using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Materials;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Terrain;
using Stride.Terrain.BlendTypes;
using static Stride.Assets.Materials.MaterialAssetCompiler;

namespace Stride.Assets.Terrain
{
    [AssetCompiler(typeof(TerrainDataAsset), typeof(AssetCompilationContext))]
    internal class TerrainDataAssetCompiler : AssetCompilerBase
    {
        public override IEnumerable<BuildDependencyInfo> GetInputTypes(AssetItem assetItem)
        {
            yield return new BuildDependencyInfo(typeof(TerrainLayerAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime | BuildDependencyType.CompileAsset);
            yield return new BuildDependencyInfo(typeof(MaterialAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime);
        }

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainDataAsset)assetItem.Asset;

            result.BuildSteps = new ListBuildStep();

            // Generate and compile material asset
            var materialUrl = assetItem.Location + "__MATERIAL__";
            var materialAsset = new MaterialAsset();

            foreach (var layerData in asset.Layers)
            {
                var reference = AttachedReferenceManager.GetAttachedReference(layerData.Layer);
                var layerAssetItem = assetItem.Package.FindAsset(reference.Id);
                var layer = (TerrainLayerAsset)layerAssetItem.Asset;

                if (layer.BlendType == null)
                {
                    result.Warning($"Terrain layer {layerAssetItem.Location} has no blend type");
                    continue;
                }

                var blendLayer = new MaterialBlendLayer
                {
                    Enabled = true,
                    Material = layer.Material,
                    Name = layer.Name
                };

                blendLayer.Overrides.UVScale = layer.UVScale;

                switch (layer.BlendType)
                {
                    case TerrainLayerBlendTypeParametric parametric:
                        blendLayer.BlendMap = new ComputeShaderClassScalar
                        {
                            MixinReference = "TerrainBlendLayerParametric",
                            CompositionNodes = new Dictionary<string, IComputeScalar>()
                            {
                                { "MinHeight", new ComputeFloat(parametric.MinHeight) },
                                { "MaxHeight", new ComputeFloat(parametric.MaxHeight) },
                                { "MinSlope", new ComputeFloat(parametric.MinSlope) },
                                { "MaxSlope", new ComputeFloat(parametric.MaxSlope) },
                            }
                        };
                        break;
                    case TerrainLayerBlendTypeSplatMap splatMap:
                        blendLayer.BlendMap = new ComputeTextureScalar
                        {
                            // TODO: Set texture ... and compile ...
                        };
                        break;
                }

                materialAsset.Layers.Add(blendLayer);
            }

            var materialAssetItem = new AssetItem(materialUrl, materialAsset, assetItem.Package);

            var materialAssetBuildStep = new AssetBuildStep(materialAssetItem);
            materialAssetBuildStep.Add(new MaterialCompileCommand(materialUrl, materialAssetItem, materialAsset, context));
            result.BuildSteps.Add(materialAssetBuildStep);

            // TODO: Need to build textures for blend layers here, they can probably be updated at runtime if needed

            if (!result.HasErrors)
            {
                var assetBuildStep = new AssetBuildStep(assetItem);
                assetBuildStep.Add(new TerrainDataAssetCompileCommand(targetUrlInStorage, new TerrainCompileParameters
                {
                    Data = asset,
                    MaterialUrl = materialUrl
                }, assetItem.Package));

                result.BuildSteps.Add(assetBuildStep);

                BuildStep.LinkBuildSteps(materialAssetBuildStep, assetBuildStep);
            }
        }

        public class TerrainDataAssetCompileCommand : AssetCommand<TerrainCompileParameters>
        {
            public TerrainDataAssetCompileCommand(string url, TerrainCompileParameters parameters, IAssetFinder assetFinder)
                : base(url, parameters, assetFinder)
            {
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                var terrain = Parameters.Data;

                if (terrain.Resolution.X <= 0 || terrain.Resolution.Y <= 0)
                    throw new ArgumentException("Resolution must be greater than zero", nameof(terrain.Resolution));

                var terrainData = new TerrainData
                {
                    Resolution = terrain.Resolution,
                    Size = terrain.Size,
                    SplatMapResolution = terrain.SplatMapResolution,
                    Heightmap = terrain.Heightmap ?? new float[terrain.Resolution.X * terrain.Resolution.Y],
                    Layers = terrain.Layers.Select(x => new TerrainLayerData
                    {
                        Layer = x.Layer,
                        Data = x.Data
                    }).ToList(),
                    Material = AttachedReferenceManager.CreateProxyObject<Material>(AssetId.Empty, Parameters.MaterialUrl)
                };

                var assetManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                assetManager.Save(Url, terrainData);

                return Task.FromResult(ResultStatus.Successful);
            }
        }

        [DataContract]
        public class TerrainCompileParameters
        {
            public TerrainDataAsset Data { get; set; }
            public string MaterialUrl { get; set; }
        }
    }
}

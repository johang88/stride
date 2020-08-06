using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Materials;
using Stride.Assets.Textures;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
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
            yield return new BuildDependencyInfo(typeof(MaterialAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime | BuildDependencyType.CompileAsset);
            yield return new BuildDependencyInfo(typeof(TextureAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime | BuildDependencyType.CompileAsset);
        }

        public override IEnumerable<ObjectUrl> GetInputFiles(AssetItem assetItem)
        {
            yield return new ObjectUrl(UrlType.Content, "StrideDefaultTerrainSplatMap");
        }

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainDataAsset)assetItem.Asset;

            result.BuildSteps = new ListBuildStep();

            // Generate and compile material asset
            var materialUrl = assetItem.Location + "__MATERIAL__";
            var materialAsset = new MaterialAsset
            {
                Id = AssetId.Empty
            };

            foreach (var layerData in asset.Layers)
            {
                if (layerData.Layer == null)
                    continue;

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
                    case TerrainLayerBlendTypeSplatMap _:
                        blendLayer.BlendMap = new ComputeTextureScalar
                        {
                            // Texture is set at runtime
                            // We want to use the key, but it's not serialized and the material description is clone during the compilation process
                            // This means that the key info is lost
                            // One alternative would be to generate the material at runtime
                            // But unfortnately the material generator does not support generating blend layers at runtime
                            //Key = ParameterKeys.NewObject<Texture>(null, $"Splat_{i}"),
                            // ShaderGenerator requires a value of it will use a fallback shader ...
                            // It's currently not possible to generate texture asset without source files so we can't generate them at compile time for our splat maps
                            // So we use a simple fallback texture :)
                            Texture = AttachedReferenceManager.CreateProxyObject<Texture>(new AssetId("2b20e38b-aac4-449e-96a4-719fbc6e54bb"), "StrideDefaultTerrainSplatMap") 
                        };
                        break;
                }

                materialAsset.Layers.Add(blendLayer);
            }

            var materialAssetItem = new AssetItem(materialUrl, materialAsset, assetItem.Package);

            var materialAssetBuildStep = new AssetBuildStep(materialAssetItem);
            materialAssetBuildStep.Add(new MaterialCompileCommand(materialUrl, materialAssetItem, materialAsset, context));
            result.BuildSteps.Add(materialAssetBuildStep);

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

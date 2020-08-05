using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Materials;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization.Contents;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [AssetCompiler(typeof(TerrainLayerAsset), typeof(AssetCompilationContext))]
    internal class TerrainLayerAssetCompiler : AssetCompilerBase
    {
        public override IEnumerable<BuildDependencyInfo> GetInputTypes(AssetItem assetItem)
        {
            yield return new BuildDependencyInfo(typeof(MaterialAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime | BuildDependencyType.CompileAsset);
        }

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainLayerAsset)assetItem.Asset;

            result.BuildSteps = new AssetBuildStep(assetItem);
            result.BuildSteps.Add(new TerrainLayerAssetCompileCommand(targetUrlInStorage, asset, assetItem.Package));
        }

        public class TerrainLayerAssetCompileCommand : AssetCommand<TerrainLayerAsset>
        {
            public TerrainLayerAssetCompileCommand(string url, TerrainLayerAsset parameters, IAssetFinder assetFinder)
                : base(url, parameters, assetFinder)
            {
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                var terrainLayer = new TerrainLayer
                {
                    Name = Parameters.Name,
                    Material = Parameters.Material,
                    UVScale = Parameters.UVScale,
                    BlendType = Parameters.BlendType
                };

                var assetManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                assetManager.Save(Url, terrainLayer);

                return Task.FromResult(ResultStatus.Successful);
            }
        }
    }
}

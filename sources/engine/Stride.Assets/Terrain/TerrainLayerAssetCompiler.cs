using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization.Contents;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [AssetCompiler(typeof(TerrainLayerAsset), typeof(AssetCompilationContext))]
    internal class TerrainLayerAssetCompiler : AssetCompilerBase
    {
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
                    UVSCale = Parameters.UVScale,
                    BlendType = Parameters.BlendType
                };

                var assetManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                assetManager.Save(Url, terrainLayer);

                return Task.FromResult(ResultStatus.Successful);
            }
        }
    }
}

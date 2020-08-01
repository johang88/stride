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
    [AssetCompiler(typeof(TerrainDataAsset), typeof(AssetCompilationContext))]
    internal class TerrainDataAssetCompiler : AssetCompilerBase
    {
        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainDataAsset)assetItem.Asset;

            result.BuildSteps = new AssetBuildStep(assetItem);
            result.BuildSteps.Add(new TerrainDataAssetCompileCommand(targetUrlInStorage, asset, assetItem.Package));
        }

        public class TerrainDataAssetCompileCommand : AssetCommand<TerrainDataAsset>
        {
            public TerrainDataAssetCompileCommand(string url, TerrainDataAsset parameters, IAssetFinder assetFinder)
                : base(url, parameters, assetFinder)
            {
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                if (Parameters.Resolution.X <= 0 || Parameters.Resolution.Y <= 0)
                    throw new ArgumentException("Size must be greater than zero", nameof(Parameters.Resolution));

                var terrainData = new TerrainData
                {
                    Resolution = Parameters.Resolution,
                    // TODO: Should we copy the array? probably
                    Heightmap = Parameters.Heightmap ?? new ushort[Parameters.Resolution.X * Parameters.Resolution.Y] 
                };

                var assetManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                assetManager.Save(Url, terrainData);

                return Task.FromResult(ResultStatus.Successful);
            }
        }
    }
}

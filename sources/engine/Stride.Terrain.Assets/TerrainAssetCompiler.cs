// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;
using Stride.Terrain;

namespace Stride.Assets.Terrain;

[AssetCompiler(typeof(TerrainAsset), typeof(AssetCompilationContext))]
internal sealed class TerrainAssetCompiler : AssetCompilerBase
{
    public override IEnumerable<BuildDependencyInfo> GetInputTypes(AssetItem assetItem)
    {
        yield return new BuildDependencyInfo(typeof(TerrainMaterialAsset), typeof(AssetCompilationContext), BuildDependencyType.Runtime);
    }

    protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
    {
        var asset = (TerrainAsset)assetItem.Asset;
        var sourcePath = !UPath.IsNullOrEmpty(asset.DataFile) ? GetAbsolutePath(assetItem, asset.DataFile) : null;
        result.BuildSteps = new AssetBuildStep(assetItem);
        result.BuildSteps.Add(new TerrainCompileCommand(targetUrlInStorage, asset, sourcePath, assetItem.Package));
    }

    private sealed class TerrainCompileCommand : AssetCommand<TerrainAsset>
    {
        private readonly UFile sourcePath;

        public TerrainCompileCommand(string url, TerrainAsset parameters, UFile sourcePath, IAssetFinder assetFinder)
            : base(url, parameters, assetFinder)
        {
            this.sourcePath = sourcePath;
            Version = 1;
        }

        public override IEnumerable<ObjectUrl> GetInputFiles()
        {
            if (sourcePath != null && System.IO.File.Exists(sourcePath))
                yield return new ObjectUrl(UrlType.File, sourcePath);
        }

        protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
        {
            TerrainData data;
            try
            {
                data = Parameters.LoadData(sourcePath);
            }
            catch (System.Exception e)
            {
                commandContext.Logger.Error($"Failed to load the terrain source [{sourcePath}]", e);
                return Task.FromResult(ResultStatus.Failed);
            }

            if (sourcePath == null || !System.IO.File.Exists(sourcePath))
                commandContext.Logger.Verbose($"Terrain source [{sourcePath}] doesn't exist yet, using a flat terrain.");

            data.Material = Parameters.Material;
            var contentManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
            contentManager.Save(Url, data);
            return Task.FromResult(ResultStatus.Successful);
        }
    }
}

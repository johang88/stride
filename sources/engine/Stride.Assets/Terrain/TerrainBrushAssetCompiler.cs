using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.Serialization.Contents;
using Stride.Terrain;
using Stride.TextureConverter;
using Stride.Graphics;
using Stride.Physics;
using Stride.Core.IO;
using Stride.Core.Mathematics;

namespace Stride.Assets.Terrain
{
    [AssetCompiler(typeof(TerrainBrushAsset), typeof(AssetCompilationContext))]
    internal class TerrainBrushAssetCompiler : AssetCompilerBase
    {
        public override IEnumerable<BuildDependencyInfo> GetInputTypes(AssetItem assetItem)
        {
            yield return new BuildDependencyInfo(typeof(TextureAsset), typeof(AssetCompilationContext), BuildDependencyType.CompileContent);
        }

        public override IEnumerable<ObjectUrl> GetInputFiles(AssetItem assetItem)
        {
            var asset = (TerrainBrushAsset)assetItem.Asset;
            var url = asset.Source.FullPath;
            if (!string.IsNullOrEmpty(url))
            {
                yield return new ObjectUrl(UrlType.File, url);
            }
        }

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainBrushAsset)assetItem.Asset;

            result.BuildSteps = new AssetBuildStep(assetItem);
            result.BuildSteps.Add(new TerrainBrushAssetCompileCommand(targetUrlInStorage, asset, assetItem.Package) { InputFilesGetter = () => GetInputFiles(assetItem) });
        }

        public class TerrainBrushAssetCompileCommand : AssetCommand<TerrainBrushAsset>
        {
            public TerrainBrushAssetCompileCommand(string url, TerrainBrushAsset parameters, IAssetFinder assetFinder)
                : base(url, parameters, assetFinder)
            {
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                var source = Parameters.Source;
                var (data, size) = GetBrushHeightData(source);

                var terrainBrush = new TerrainBrush
                {
                    Data = data,
                    Size = size
                };

                var assetManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                assetManager.Save(Url, terrainBrush);

                return Task.FromResult(ResultStatus.Successful);
            }

            private (float[] data, Int2 size) GetBrushHeightData(UFile source)
            {
                // TODO: Maybe we could generalize this with the physics heightmap compiler?
                // Will probably need the same tools for importing terrains later on
                using (var textureTool = new TextureTool())
                using (var texImage = textureTool.Load(source, Parameters.IsSRgb))
                {
                    // Convert the pixel format to single component one.
                    switch (texImage.Format)
                    {
                        case PixelFormat.R32_Float:
                        case PixelFormat.R16_SNorm:
                        case PixelFormat.R8_UNorm:
                            break;

                        case PixelFormat.R32G32B32A32_Float:
                        case PixelFormat.R16G16B16A16_Float:
                        case PixelFormat.R16_Float:
                            textureTool.Convert(texImage, PixelFormat.R32_Float);
                            break;

                        case PixelFormat.R16_UNorm:
                        case PixelFormat.R16G16B16A16_UNorm:
                        case PixelFormat.R16G16_UNorm:
                            textureTool.Convert(texImage, PixelFormat.R16_SNorm);
                            break;

                        case PixelFormat.R16G16B16A16_SNorm:
                        case PixelFormat.R16G16_SNorm:
                            textureTool.Convert(texImage, PixelFormat.R16_SNorm);
                            break;

                        case PixelFormat.R8_SNorm:
                        case PixelFormat.B8G8R8A8_UNorm:
                        case PixelFormat.B8G8R8X8_UNorm:
                        case PixelFormat.R8G8B8A8_UNorm:
                        case PixelFormat.R8G8_UNorm:
                            textureTool.Convert(texImage, PixelFormat.R8_UNorm);
                            break;

                        case PixelFormat.R8G8B8A8_SNorm:
                        case PixelFormat.R8G8_SNorm:
                            textureTool.Convert(texImage, PixelFormat.R8_UNorm);
                            break;

                        case PixelFormat.B8G8R8A8_UNorm_SRgb:
                        case PixelFormat.B8G8R8X8_UNorm_SRgb:
                        case PixelFormat.R8G8B8A8_UNorm_SRgb:
                            textureTool.Convert(texImage, PixelFormat.R8_UNorm);
                            break;

                        default:
                            throw new Exception($"{ texImage.Format } format is not supported.");
                    }

                    using (var image = textureTool.ConvertToStrideImage(texImage))
                    {
                        var pixelBuffer = image.PixelBuffer[0];
                        float[] floats = null;

                        switch (image.Description.Format)
                        {
                            case PixelFormat.R32_Float:
                                floats = pixelBuffer.GetPixels<float>();
                                break;

                            case PixelFormat.R16_SNorm:
                                floats = ConvertToFloatHeights(pixelBuffer.GetPixels<short>());
                                break;

                            case PixelFormat.R8_UNorm:
                                floats = ConvertToFloatHeights(pixelBuffer.GetPixels<byte>());
                                break;
                        }

                        return (floats, new Int2(texImage.Width, texImage.Height));
                    }
                }
            }

            public static float ConvertToFloatHeight(float minValue, float maxValue, float value) => MathUtil.InverseLerp(minValue, maxValue, MathUtil.Clamp(value, minValue, maxValue));

            public static float[] ConvertToFloatHeights(float[] values, float minValue, float maxValue) => values.Select((v) => ConvertToFloatHeight(minValue, maxValue, v)).ToArray();
            public static float[] ConvertToFloatHeights(short[] values, short minValue = short.MinValue, short maxValue = short.MaxValue) => values.Select((v) => ConvertToFloatHeight(minValue, maxValue, v)).ToArray();
            public static float[] ConvertToFloatHeights(byte[] values, byte minValue = byte.MinValue, byte maxValue = byte.MaxValue) => values.Select((v) => ConvertToFloatHeight(minValue, maxValue, v)).ToArray();
        }
    }
}

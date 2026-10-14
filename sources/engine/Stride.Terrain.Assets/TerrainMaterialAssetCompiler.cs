// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Textures;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Compiler;
using Stride.Core.BuildEngine;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;
using Stride.Graphics.Data;
using Stride.Terrain;
using Stride.TextureConverter;

namespace Stride.Assets.Terrain
{
    [AssetCompiler(typeof(TerrainMaterialAsset), typeof(AssetCompilationContext))]
    internal sealed class TerrainMaterialAssetCompiler : AssetCompilerBase
    {
        public const string AlbedoHeightSuffix = "__TerrainAlbedoHeight";
        public const string NormalRoughnessSuffix = "__TerrainNormalRoughness";

        public override IEnumerable<BuildDependencyInfo> GetInputTypes(AssetItem assetItem)
        {
            // The layer textures are only read at compile time (packed into the layer arrays): they aren't game content by themselves
            yield return new BuildDependencyInfo(typeof(TextureAsset), typeof(AssetCompilationContext), BuildDependencyType.CompileAsset);
            yield return new BuildDependencyInfo(typeof(GameSettingsAsset), typeof(AssetCompilationContext), BuildDependencyType.CompileAsset);
        }

        protected override void Prepare(AssetCompilerContext context, AssetItem assetItem, string targetUrlInStorage, AssetCompilerResult result)
        {
            var asset = (TerrainMaterialAsset)assetItem.Asset;
            var gameSettings = context.GetGameSettingsAsset();
            var profile = gameSettings.GetOrCreate<RenderingSettings>().DefaultGraphicsProfile;

            var parameters = new TerrainMaterialCompileParameters
            {
                Asset = asset,
                Quality = gameSettings.GetOrCreate<TextureSettings>().TextureQuality,
                UseBC7 = profile >= GraphicsProfile.Level_11_0,
            };

            foreach (var layer in asset.Layers)
            {
                parameters.Layers.Add(new TerrainLayerSources
                {
                    Albedo = ResolveTexture(assetItem, layer.Albedo, result, out _),
                    Normal = ResolveTexture(assetItem, layer.Normal, result, out var normalAsset),
                    NormalInvertY = (normalAsset?.Type as NormapMapTextureType)?.InvertY ?? false,
                    Roughness = ResolveTexture(assetItem, layer.RoughnessMap, result, out _),
                    Occlusion = ResolveTexture(assetItem, layer.AmbientOcclusion, result, out _),
                    Height = ResolveTexture(assetItem, layer.Height, result, out _),
                });
            }

            result.BuildSteps = new AssetBuildStep(assetItem);
            result.BuildSteps.Add(new TerrainMaterialCompileCommand(targetUrlInStorage, parameters, assetItem.Package));
        }

        private static string ResolveTexture(AssetItem assetItem, Texture texture, AssetCompilerResult result, out TextureAsset textureAsset)
        {
            textureAsset = null;
            if (texture == null)
                return null;

            var reference = AttachedReferenceManager.GetAttachedReference(texture);
            var textureItem = reference != null ? assetItem.Package.Session.FindAsset(reference.Id) : null;
            textureAsset = textureItem?.Asset as TextureAsset;
            if (textureAsset == null || UPath.IsNullOrEmpty(textureAsset.Source))
            {
                result.Warning($"Terrain material layer texture [{reference?.Url}] couldn't be resolved to a texture asset with a source file.");
                return null;
            }
            return GetAbsolutePath(textureItem, textureAsset.Source);
        }

        private sealed class TerrainMaterialCompileCommand : AssetCommand<TerrainMaterialCompileParameters>
        {
            public TerrainMaterialCompileCommand(string url, TerrainMaterialCompileParameters parameters, IAssetFinder assetFinder)
                : base(url, parameters, assetFinder)
            {
                Version = 1;
            }

            public override IEnumerable<ObjectUrl> GetInputFiles()
            {
                foreach (var layer in Parameters.Layers)
                {
                    foreach (var path in new[] { layer.Albedo, layer.Normal, layer.Roughness, layer.Occlusion, layer.Height })
                    {
                        if (path != null && System.IO.File.Exists(path))
                            yield return new ObjectUrl(UrlType.File, path);
                    }
                }
            }

            protected override Task<ResultStatus> DoCommandOverride(ICommandContext commandContext)
            {
                var asset = Parameters.Asset;
                var size = (int)asset.TextureSize;
                // Texture arrays need at least two slices to always get an array view
                var sliceCount = Math.Max(2, asset.Layers.Count);
                var albedoSlices = new Image[sliceCount];
                var normalSlices = new Image[sliceCount];

                try
                {
                    using var textureTool = new TextureTool();
                    // Layers are decoded and resized in parallel (several large source images each)
                    Parallel.For(0, sliceCount, i =>
                    {
                        using var layerTool = new TextureTool();
                        var layer = i < asset.Layers.Count ? asset.Layers[i] : null;
                        var sources = i < Parameters.Layers.Count ? Parameters.Layers[i] : new TerrainLayerSources();
                        albedoSlices[i] = BuildAlbedoHeight(layerTool, layer, sources, size, commandContext);
                        normalSlices[i] = BuildNormalRoughness(layerTool, layer, sources, size, commandContext);
                    });

                    var contentManager = new ContentManager(MicrothreadLocalDatabases.ProviderService);
                    var albedoFormat = Parameters.UseBC7 ? PixelFormat.BC7_UNorm_SRgb : PixelFormat.BC3_UNorm_SRgb;
                    var normalFormat = Parameters.UseBC7 ? PixelFormat.BC7_UNorm : PixelFormat.BC3_UNorm;
                    SaveArray(contentManager, textureTool, albedoSlices, true, albedoFormat, Url + AlbedoHeightSuffix);
                    SaveArray(contentManager, textureTool, normalSlices, false, normalFormat, Url + NormalRoughnessSuffix);

                    var material = new TerrainMaterial
                    {
                        AlbedoHeightArray = AttachedReferenceManager.CreateProxyObject<Texture>(AssetId.Empty, Url + AlbedoHeightSuffix),
                        NormalRoughnessArray = AttachedReferenceManager.CreateProxyObject<Texture>(AssetId.Empty, Url + NormalRoughnessSuffix),
                        HeightBlendSharpness = asset.HeightBlendSharpness,
                        CliffLayer = asset.CliffLayer,
                        CliffSlopeStart = asset.CliffSlopeStart,
                        CliffSlopeEnd = asset.CliffSlopeEnd,
                        ParallaxDepth = asset.ParallaxDepth,
                        ParallaxFadeDistance = asset.ParallaxFadeDistance,
                    };
                    for (int i = 0; i < asset.Layers.Count; i++)
                    {
                        var layer = asset.Layers[i];
                        material.Layers.Add(new TerrainLayer
                        {
                            Name = layer.Name,
                            TextureIndex = i,
                            TileSize = layer.TileSize,
                            Tint = layer.Albedo != null ? layer.Tint : new Color3(1.0f),
                            NormalStrength = layer.NormalStrength,
                            HeightBlendScale = layer.HeightBlendScale,
                            HeightBlendOffset = layer.HeightBlendOffset,
                            TilingVariation = layer.TilingVariation,
                            SurfaceTag = layer.SurfaceTag,
                        });
                    }
                    contentManager.Save(Url, material);
                }
                finally
                {
                    foreach (var image in albedoSlices)
                        image?.Dispose();
                    foreach (var image in normalSlices)
                        image?.Dispose();
                }

                return Task.FromResult(ResultStatus.Successful);
            }

            private void SaveArray(ContentManager contentManager, TextureTool textureTool, Image[] slices, bool isSRgb, PixelFormat format, string url)
            {
                var texImages = new TexImage[slices.Length];
                try
                {
                    // Slices are compressed independently and in parallel (block compression is single threaded and slow), then
                    // assembled: the array creation only copies the slice data
                    var quality = (TextureConverter.Requests.TextureQuality)Parameters.Quality;
                    Parallel.For(0, slices.Length, i =>
                    {
                        using var sliceTool = new TextureTool();
                        var texImage = sliceTool.Load(slices[i], isSRgb);
                        texImages[i] = texImage;
                        sliceTool.GenerateMipMaps(texImage, Filter.MipMapGeneration.Box);
                        sliceTool.Compress(texImage, format, quality);
                    });

                    using var array = textureTool.CreateTextureArray(texImages.ToList());
                    using var image = textureTool.ConvertToStrideImage(array);
                    contentManager.Save(url, image.ToSerializableVersion(), typeof(Texture));
                }
                finally
                {
                    foreach (var texImage in texImages)
                        texImage?.Dispose();
                }
            }

            private static Image BuildAlbedoHeight(TextureTool tool, TerrainLayerDescription layer, TerrainLayerSources sources, int size, ICommandContext commandContext)
            {
                var albedo = LoadPixels(tool, sources.Albedo, true, false, size, commandContext);
                var height = LoadPixels(tool, sources.Height, false, false, size, commandContext);
                var tint = layer != null && sources.Albedo == null ? (Color)new Color4(layer.Tint.ToSRgb(), 1.0f) : new Color(128, 128, 128);
                var pixels = new Color[size * size];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var c = albedo != null ? albedo[i] : tint;
                    c.A = height != null ? height[i].R : (byte)128;
                    pixels[i] = c;
                }
                return CreateImage(pixels, size, PixelFormat.R8G8B8A8_UNorm_SRgb);
            }

            private static Image BuildNormalRoughness(TextureTool tool, TerrainLayerDescription layer, TerrainLayerSources sources, int size, ICommandContext commandContext)
            {
                var normal = LoadPixels(tool, sources.Normal, false, sources.NormalInvertY, size, commandContext);
                var roughness = LoadPixels(tool, sources.Roughness, false, false, size, commandContext);
                var occlusion = LoadPixels(tool, sources.Occlusion, false, false, size, commandContext);
                var constantRoughness = (byte)Math.Clamp((int)MathF.Round((layer?.Roughness ?? 0.85f) * 255), 0, 255);
                var invertRoughness = layer?.InvertRoughnessMap ?? false;
                var pixels = new Color[size * size];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var n = normal != null ? normal[i] : new Color(128, 128, 255, 255);
                    var r = roughness != null ? (invertRoughness ? (byte)(255 - roughness[i].R) : roughness[i].R) : constantRoughness;
                    var ao = occlusion != null ? occlusion[i].R : (byte)255;
                    pixels[i] = new Color(n.R, n.G, r, ao);
                }
                return CreateImage(pixels, size, PixelFormat.R8G8B8A8_UNorm);
            }

            private static Color[] LoadPixels(TextureTool tool, string path, bool isSRgb, bool invertY, int size, ICommandContext commandContext)
            {
                if (path == null)
                    return null;
                if (!System.IO.File.Exists(path))
                {
                    commandContext.Logger.Warning($"Terrain layer texture source [{path}] doesn't exist.");
                    return null;
                }

                using var texImage = tool.Load(path, isSRgb);
                tool.Decompress(texImage, isSRgb);
                if (invertY)
                    tool.InvertY(texImage);
                var format = isSRgb ? PixelFormat.R8G8B8A8_UNorm_SRgb : PixelFormat.R8G8B8A8_UNorm;
                if (texImage.Format != format)
                    tool.Convert(texImage, format);
                if (texImage.Width != size || texImage.Height != size)
                    tool.Resize(texImage, size, size, Filter.Rescaling.Lanczos3);
                using var image = tool.ConvertToStrideImage(texImage);
                return image.PixelBuffer[0].GetPixels<Color>();
            }

            private static unsafe Image CreateImage(Color[] pixels, int size, PixelFormat format)
            {
                var image = Image.New2D(size, size, 1, format);
                image.PixelBuffer[0].SetPixels(pixels);
                return image;
            }
        }
    }

    /// <summary>
    /// Build parameters of a terrain material (hashed to detect changes).
    /// </summary>
    [DataContract]
    public sealed class TerrainMaterialCompileParameters
    {
        public TerrainMaterialAsset Asset { get; set; }

        public List<TerrainLayerSources> Layers { get; set; } = new();

        public TextureQuality Quality { get; set; }

        public bool UseBC7 { get; set; }
    }

    /// <summary>
    /// Resolved source files of a terrain layer.
    /// </summary>
    [DataContract]
    public sealed class TerrainLayerSources
    {
        public string Albedo { get; set; }

        public string Normal { get; set; }

        public bool NormalInvertY { get; set; }

        public string Roughness { get; set; }

        public string Occlusion { get; set; }

        public string Height { get; set; }
    }
}

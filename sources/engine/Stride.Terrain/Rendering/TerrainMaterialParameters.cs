// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Terrain.Rendering;

/// <summary>
/// Fills the material parameters read by the terrain shaders.
/// </summary>
internal static class TerrainMaterialParameters
{
    private static readonly ConditionalWeakTable<GraphicsDevice, FallbackTextures> Fallbacks = new();

    public static void Apply(ParameterCollection parameters, TerrainRenderObject renderObject)
    {
        var data = renderObject.Data;
        parameters.Set(TerrainBaseKeys.TerrainHeightmap, renderObject.Gpu.Heightmap);
        parameters.Set(TerrainBaseKeys.TerrainSplatMap, renderObject.Gpu.SplatMap);
        parameters.Set(TerrainBaseKeys.TerrainCellSize, data.CellSize);
        parameters.Set(TerrainBaseKeys.TerrainResolution, (float)data.Resolution);
        parameters.Set(TerrainBaseKeys.TerrainMinHeight, data.MinHeight);
        parameters.Set(TerrainBaseKeys.TerrainHeightRange, data.MaxHeight - data.MinHeight);
        parameters.Set(TerrainMaterialVertexKeys.TerrainPatchQuads, (float)renderObject.QuadTree.PatchQuads);
        ApplyMorph(parameters, renderObject.QuadTree);
        ApplyLayers(parameters, renderObject.Gpu.Heightmap.GraphicsDevice, data.Material);
    }

    public static void ApplyMorph(ParameterCollection parameters, TerrainQuadTree quadTree)
    {
        var morph = new Vector4[TerrainQuadTree.MaxLevels];
        quadTree.GetMorphConstants(morph);
        parameters.Set(TerrainMaterialVertexKeys.TerrainMorph, morph);
    }

    public static void ApplyLayers(ParameterCollection parameters, GraphicsDevice device, TerrainMaterial material)
    {
        var layerParams = new Vector4[TerrainSplat.MaxLayers];
        var layerTints = new Vector4[TerrainSplat.MaxLayers];
        var layerVariations = new Vector4[TerrainSplat.MaxLayers];
        for (int i = 0; i < layerParams.Length; i++)
        {
            var layer = material != null && i < material.Layers.Count ? material.Layers[i] : null;
            if (layer == null)
            {
                layerParams[i] = new Vector4(0.25f, 1.0f, 0.0f, 0.0f);
                layerTints[i] = new Vector4(1.0f);
                continue;
            }
            layerParams[i] = new Vector4(1.0f / Math.Max(layer.TileSize, 0.01f), layer.NormalStrength, layer.HeightBlendScale, layer.HeightBlendOffset);
            layerTints[i] = new Vector4(layer.Tint.R, layer.Tint.G, layer.Tint.B, layer.RoughnessScale);
            layerVariations[i] = new Vector4(Math.Clamp(layer.TilingVariation, 0, 1), 0, 0, 0);
        }

        parameters.Set(TerrainLayersKeys.TerrainLayerParams, layerParams);
        parameters.Set(TerrainLayersKeys.TerrainLayerTints, layerTints);
        parameters.Set(TerrainLayersKeys.TerrainLayerVariations, layerVariations);
        parameters.Set(TerrainLayersKeys.TerrainHeightBlendSharpness, Math.Max(material?.HeightBlendSharpness ?? 0.2f, 1e-3f));
        var cliffLayer = material != null && material.CliffLayer >= 0 && material.CliffLayer < material.Layers.Count ? material.CliffLayer : -1;
        parameters.Set(TerrainLayersKeys.TerrainCliffParams, new Vector4(cliffLayer, material?.CliffSlopeStart ?? 0.35f, material?.CliffSlopeEnd ?? 0.5f, 0));
        parameters.Set(TerrainLayersKeys.TerrainParallax, new Vector4(material?.ParallaxDepth ?? 0, Math.Max(material?.ParallaxFadeDistance ?? 40, 1), 0, 0));

        var fallback = Fallbacks.GetValue(device, d => new FallbackTextures(d));
        parameters.Set(TerrainLayersKeys.TerrainAlbedoHeightArray, material?.AlbedoHeightArray ?? fallback.AlbedoHeight);
        parameters.Set(TerrainLayersKeys.TerrainNormalRoughnessArray, material?.NormalRoughnessArray ?? fallback.NormalRoughness);
    }

    /// <summary>
    /// Single-slice layer textures used when a terrain has no material.
    /// </summary>
    private sealed class FallbackTextures
    {
        public readonly Texture AlbedoHeight;
        public readonly Texture NormalRoughness;

        public FallbackTextures(GraphicsDevice device)
        {
            AlbedoHeight = CreateArray(device, new Color(140, 140, 140, 128), PixelFormat.R8G8B8A8_UNorm_SRgb);
            NormalRoughness = CreateArray(device, new Color(128, 128, 220, 255), PixelFormat.R8G8B8A8_UNorm);
        }

        private static unsafe Texture CreateArray(GraphicsDevice device, Color color, PixelFormat format)
        {
            // Two slices so the view is always an array view (a single slice would get a plain 2D view)
            const int size = 4;
            const int slices = 2;
            var pixels = new Color[size * size];
            Array.Fill(pixels, color);
            fixed (Color* data = pixels)
            {
                var boxes = new DataBox[slices];
                for (int i = 0; i < slices; i++)
                    boxes[i] = new DataBox((nint)data, size * sizeof(Color), size * size * sizeof(Color));
                var description = TextureDescription.New2D(size, size, 1, format, TextureFlags.ShaderResource, slices, GraphicsResourceUsage.Immutable);
                return Texture.New(device, description, boxes);
            }
        }
    }
}

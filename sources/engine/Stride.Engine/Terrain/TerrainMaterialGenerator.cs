using System;
using System.Collections.Generic;
using System.Text;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Stride.Terrain.BlendTypes;

namespace Stride.Terrain
{
    public static class TerrainMaterialGenerator
    {
        public static void Generate(GraphicsDevice device, TerrainData terrain, TerrainRenderData renderData)
        {
            if (!IsDirty(terrain, renderData)) 
            {
                return;
            }

            // TODO: Dispose old material?

            var desc = new MaterialDescriptor();

            foreach (var layerData in terrain.Layers)
            {
                if (layerData.Layer.BlendType == null)
                    continue;

                var blendLayer = new MaterialBlendLayer
                {
                    Enabled = true,
                    Material = layerData.Layer.Material,
                    Name = layerData.Layer.Name
                };

                blendLayer.Overrides.UVScale = layerData.Layer.UVSCale;

                switch (layerData.Layer.BlendType)
                {
                    case TerrainLayerBlendTypeParametric parametric:
                        blendLayer.BlendMap = new ComputeShaderClassScalar
                        {
                            MixinReference = "TerrainBlendLayerParametric",
                            CompositionNodes = new Dictionary<string, IComputeScalar>()
                            {
                                { "MinHeight", new ComputeFloat(parametric.MinHeight) },
                                { "MaxHeight", new ComputeFloat(parametric.MaxHeight) },
                                { "HeightStrength", new ComputeFloat(parametric.HeightStrength) },
                                { "MinSlope", new ComputeFloat(parametric.MinSlope) },
                                { "MaxSlope", new ComputeFloat(parametric.MaxSlope) },
                                { "SlopeStrength", new ComputeFloat(parametric.SlopeStrength) }
                            }
                        };
                        break;
                    case TerrainLayerBlendTypeSplatMap splatMap:
                        blendLayer.BlendMap = new ComputeTextureScalar
                        {
                            Texture = renderData.CreateLayerTexture(device, layerData)
                        };
                        break;
                }

                desc.Layers.Add(blendLayer);
            }

            renderData.Material = Material.New(device, desc);
        }

        private static bool IsDirty(TerrainData terrain, TerrainRenderData renderData)
        {
            if (renderData.Material == null)
                return true;

            var desc = renderData.Material.Descriptor;

            if (desc.Layers.Count != terrain.Layers.Count)
                return true;

            for (var i = 0; i < desc.Layers.Count; i++)
            {
                var materialLayer = desc.Layers[i];
                var terrainLayer = terrain.Layers[i].Layer;

                if (materialLayer.Name != terrainLayer.Name)
                    return true;

                if (materialLayer.Overrides.UVScale != terrainLayer.UVSCale)
                    return true;

                if (materialLayer.BlendMap is ComputeShaderClassScalar materialLayerParametric 
                    && terrainLayer.BlendType is TerrainLayerBlendTypeParametric terrainLayerParametric)
                {
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "MinHeight", terrainLayerParametric.MinHeight))
                        return true;
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "MaxHeight", terrainLayerParametric.MaxHeight))
                        return true;
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "HeightStrength", terrainLayerParametric.MaxHeight))
                        return true;
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "MinSlope", terrainLayerParametric.MinSlope))
                        return true;
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "MaxSlope", terrainLayerParametric.MaxSlope))
                        return true;
                    if (!CheckCompositionNode(materialLayerParametric.CompositionNodes, "SlopeStrength", terrainLayerParametric.SlopeStrength))
                        return true;
                }
                else if (!(materialLayer.BlendMap is ComputeTextureScalar && terrainLayer.BlendType is TerrainLayerBlendTypeSplatMap))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CheckCompositionNode(Dictionary<string, IComputeScalar> compositionNodes, string nodeName, float minHeight)
        {
            return compositionNodes.TryGetValue(nodeName, out var computeScalar)
                && computeScalar is ComputeFloat computeFloat
                && computeFloat.Value == minHeight;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Shaders.Ast;
using Stride.Terrain.BlendTypes;

namespace Stride.Terrain.Tools
{
    [DataContract]
    public class Paint : BaseTool
    {
        public TerrainLayer Layer { get; set; }
        private TerrainLayerData _layerData = null;

        protected override bool IsValid(TerrainData terrain)
        {
            if (Layer == null || !(Layer.BlendType is TerrainLayerBlendTypeSplatMap))
                return false;

            return GetLayerData(terrain) != null;
        }

        private TerrainLayerData GetLayerData(TerrainData terrain)
        {
            foreach (var layer in terrain.Layers)
            {
                if (Layer == layer.Layer)
                {
                    _layerData = layer;
                    return layer;
                }
            }

            return null;
        }

        protected override void ApplyTool(TerrainData terrain, int x, int y, float intensity, ToolInvalidationData invalidationData)
        {
            // Rescale intensity for painting
            intensity = (intensity * terrain.Size.Y) / 255.0f;

            var resolutionRatioX = (float)terrain.SplatMapResolution.X / terrain.Resolution.X;
            var resolutionRatioY = (float)terrain.SplatMapResolution.Y / terrain.Resolution.Y;

            x = (int)(x * resolutionRatioX);
            y = (int)(y * resolutionRatioY);

            var index = y * terrain.SplatMapResolution.X + x;

            var value = (_layerData.Data[index] / 255.0f) + intensity * 50;
            value = MathUtil.Clamp(value, 0.0f, 1.0f);

            _layerData.Data[index] = (byte)(value * 255.0f);

            // Normalize the other splat map layers
            var totalWeights = 0;
            foreach (var layer in terrain.Layers)
            {
                if (Layer != layer.Layer && layer.Layer?.BlendType is TerrainLayerBlendTypeSplatMap) 
                {
                    totalWeights += layer.Data[index];
                }
            }

            var currentLayerValue = (int)_layerData.Data[index];
            if (totalWeights > 0 && totalWeights + currentLayerValue > 255)
            {
                var remainingContribution = 255 - currentLayerValue;
                foreach (var layer in terrain.Layers)
                {
                    if (Layer != layer.Layer && layer.Layer?.BlendType is TerrainLayerBlendTypeSplatMap)
                    {
                        var relativeContribution = layer.Data[index] / (float)totalWeights;
                        layer.Data[index] = (byte)(remainingContribution * relativeContribution);
                    }
                }
            }

            invalidationData.SplatMaps = true;
        }
    }
}

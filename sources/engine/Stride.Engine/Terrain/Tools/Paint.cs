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

        public override void Apply(TerrainData terrain, TerrainBrush brush, float intensity, int size, Int2 point, ToolInvalidationData invalidationData)
        {
            if (!IsValid(terrain))
                return;

            var resolutionRatioX = (float)terrain.SplatMapResolution.X / terrain.Resolution.X;
            var resolutionRatioY = (float)terrain.SplatMapResolution.Y / terrain.Resolution.Y;

            size = (int)(size * resolutionRatioX);
            point = new Int2((int)(point.X * resolutionRatioX), (int)(point.Y * resolutionRatioY));

            var brushIndexMultiplierX = (size * 2.0f) / brush.Size.X;
            var brushIndexMultiplierY = (size * 2.0f) / brush.Size.Y;

            for (var y = -size; y < size; y++)
            {
                for (var x = -size; x < size; x++)
                {
                    var xi = point.X + x;
                    var yi = point.Y + y;

                    if (xi < 0 || yi < 0 || xi >= terrain.SplatMapResolution.X || yi >= terrain.SplatMapResolution.Y)
                        continue;

                    var brushIndexX = (int)Math.Round((x + size) / brushIndexMultiplierX, MidpointRounding.AwayFromZero);
                    var brushIndexY = (int)Math.Round((y + size) / brushIndexMultiplierY, MidpointRounding.AwayFromZero);

                    var factor = brush.Data[brushIndexY * brush.Size.X + brushIndexX];
                    if (factor > 0)
                    {
                        ApplyTool(terrain, xi, yi, intensity * factor, invalidationData);
                    }
                }
            }
        }

        protected override void ApplyTool(TerrainData terrain, int x, int y, float intensity, ToolInvalidationData invalidationData)
        {
            // Rescale intensity for painting
            intensity = (intensity * terrain.Size.Y) / 255.0f;

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

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
        public static Material Generate(GraphicsDevice device, TerrainData terrain)
        {
            var desc = new MaterialDescriptor();

            foreach (var layerData in terrain.Layers)
            {
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
                        // TODO
                        break;
                    case TerrainLayerBlendTypeSplatMap splatMap:
                        // TODO
                        break;
                }

                desc.Layers.Add(blendLayer);
            }

            return Material.New(device, desc);
        }
    }
}

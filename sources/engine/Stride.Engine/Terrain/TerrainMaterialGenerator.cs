using System;
using System.Collections.Generic;
using System.Text;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
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

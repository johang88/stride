using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using SharpFont;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain.BlendTypes;

namespace Stride.Terrain
{
    public class TerrainRenderData
    {
        public Vector3 Size { get; set; }
        public Int2 Resolution { get; set; }

        public TerrainData Terrain { get; set; }

        public Mesh Mesh { get; set; }
        public ModelComponent ModelComponent { get; set; } = new ModelComponent();

        public VertexPositionNormalTangentTexture[] Vertices;
        public Buffer<VertexPositionNormalTangentTexture> VertexBuffer { get; set; }
        public HashSet<int> InvalidatesIndices { get; private set; } = new HashSet<int>();
        public Dictionary<TerrainLayerData, (Texture Texture, ObjectParameterKey<Texture> ParameterKey)> SplatMaps { get; set; } = new Dictionary<TerrainLayerData, (Texture Texture, ObjectParameterKey<Texture> ParameterKey)>();
        public bool SplatMapsInvalidated { get; set; } = true;

        public TerrainRenderData()
        {
            ModelComponent.Model = new Model
            {
                new Mesh()
            };
        }

        public void Update(TerrainComponent component)
        {
            Terrain = component.Terrain;
            Size = Terrain.Size;
            Resolution = Terrain.Resolution;
        }

        public bool IsDirty(TerrainComponent component)
        {
            if (Terrain == null || Mesh == null)
                return true;

            if (Size != component.Terrain.Size)
                return true;

            if (Resolution != component.Terrain.Resolution)
                return true;

            return false;
        }

        public void AllocateSplatMaps(GraphicsDevice graphicsDevice, TerrainComponent component)
        {
            var count = 0;
            var invalidate = false;
            foreach (var layer in component.Terrain.Layers)
            {
                if (layer.Layer is null)
                    continue;

                if (layer.Layer.BlendType is TerrainLayerBlendTypeSplatMap)
                {
                    count++;

                    if (!SplatMaps.ContainsKey(layer))
                    {
                        invalidate = true;
                    }
                }
            }

            // Recrate splatmaps if needed
            if (count == 0 || count != SplatMaps.Count || invalidate)
            {
                foreach (var splatMap in SplatMaps.Values)
                {
                    splatMap.Texture.Dispose();
                }

                SplatMaps.Clear();
            }

            // Setup splat maps
            if (count != SplatMaps.Count)
            {
                var i = 0;
                foreach (var layer in component.Terrain.Layers)
                {
                    if (layer.Layer is null)
                        continue;

                    if (layer.Layer.BlendType is TerrainLayerBlendTypeSplatMap)
                    {
                        var texture = Texture.New2D(graphicsDevice, component.Terrain.SplatMapResolution.X, component.Terrain.SplatMapResolution.Y, PixelFormat.R8_UNorm, usage: GraphicsResourceUsage.Dynamic);

                        var keyName = "Material.BlendMap";
                        if (i > 0)
                            keyName += ".i" + i;

                        var parameterKey = ParameterKeys.NewObject<Texture>(null, keyName);

                        SplatMaps.Add(layer, (texture, parameterKey));
                        i++;
                    }
                }

                // Make sure all data gets uploaded
                SplatMapsInvalidated = true;
            }
        }

        public void UpdateSplatMaps(GraphicsContext graphicsContext, TerrainComponent component)
        {
            if (!SplatMapsInvalidated)
                return;

            foreach (var layer in component.Terrain.Layers)
            {
                if (layer.Layer == null || layer.Data == null)
                    continue;

                if (!(layer.Layer.BlendType is TerrainLayerBlendTypeSplatMap))
                    continue;

                // Should not happen, but maybe the editor does something funny :)
                // Should be fixed by next frame in that case
                if (!SplatMaps.TryGetValue(layer, out var splatMap))
                    continue;

                splatMap.Texture.SetData(graphicsContext.CommandList, layer.Data);
            }

            SplatMapsInvalidated = false;
        }
    }
}

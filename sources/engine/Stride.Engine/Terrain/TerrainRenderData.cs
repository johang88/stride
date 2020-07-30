using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Terrain
{
    public class TerrainRenderData
    {
        public TerrainData Terrain { get; set; }
        public Vector3 Size { get; set; }
        public Mesh Mesh { get; set; }
        public ModelComponent ModelComponent { get; set; } = new ModelComponent();
        public Material Material { get; set; }

        public VertexPositionNormalTangentTexture[] Vertices;
        public Buffer<VertexPositionNormalTangentTexture> VertexBuffer { get; set; }

        public TerrainRenderData()
        {
            ModelComponent.Model = new Model
            {
                new Mesh()
            };
        }

        public void Update(TerrainComponent component)
        {
            Size = component.Size;
            Terrain = component.Terrain;
        }

        public bool IsDirty(TerrainComponent component)
            => Size != component.Size || Terrain != component.Terrain || Mesh == null;
    }
}

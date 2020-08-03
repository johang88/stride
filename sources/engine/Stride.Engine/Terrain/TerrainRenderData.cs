using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Collections;
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
        public HashSet<int> InvalidatesIndices { get; private set; } = new HashSet<int>();

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
        }

        public bool IsDirty(TerrainComponent component)
            => Terrain == null || Terrain.Size != component.Terrain.Size || Terrain.Resolution.X != component.Terrain.Resolution.X || Terrain.Resolution.Y != component.Terrain.Resolution.Y || Mesh == null;
    }
}

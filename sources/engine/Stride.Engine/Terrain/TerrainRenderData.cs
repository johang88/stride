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
            if (Terrain == null ||Mesh == null)
                return true;

            if (Size != component.Terrain.Size)
                return true;

            if (Resolution != component.Terrain.Resolution)
                return true;

            return false;
        }
    }
}

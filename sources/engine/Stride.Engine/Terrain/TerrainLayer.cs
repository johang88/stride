using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;
using Stride.Rendering;

namespace Stride.Terrain
{
    /// <summary>
    /// A layer of terrain that can be painted in the editor,
    /// these can be linked to material blend layers, vegetation placement etc.
    /// </summary>
    [DataContract]
    [ContentSerializer(typeof(DataContentSerializer<TerrainLayer>))]
    [DataSerializerGlobal(typeof(CloneSerializer<TerrainLayer>), Profile = "Clone")]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainLayer>), Profile = "Content")]
    public class TerrainLayer
    {
        public string Name { get; set; }
        public Material Material { get; set; }
        public Vector2 UVScale { get; set; }
        public ITerrainLayerBlendType BlendType { get; set; }
    }
}

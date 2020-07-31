using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;

namespace Stride.Terrain
{
    /// <summary>
    /// A layer of terrain that can be painted in the editor,
    /// these can be linked to material blend layers, vegetation placement etc.
    /// 
    /// TODO: What data should we place here? Don't think there is much useful data that can be in here :/
    /// </summary>
    [DataContract]
    [ContentSerializer(typeof(DataContentSerializer<TerrainLayer>))]
    [DataSerializerGlobal(typeof(CloneSerializer<TerrainLayer>), Profile = "Clone")]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainLayer>), Profile = "Content")]
    public class TerrainLayer
    {
        public string Name { get; set; }
    }
}

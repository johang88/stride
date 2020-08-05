using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Terrain
{
    [DataContract]
    [ContentSerializer(typeof(DataContentSerializer<TerrainBrush>))]
    [DataSerializerGlobal(typeof(CloneSerializer<TerrainBrush>), Profile = "Clone")]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainBrush>), Profile = "Content")]
    public class TerrainBrush
    {
        [DataMember(10)]
        public Int2 Size { get; set; }

        [DataMember(20)]
        public float[] Data { get; set; }

        public Texture CreateTexture(GraphicsDevice device)
        {
            if (device == null)
            {
                return null;
            }

            var data = new byte[Data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(Data[i] * 255.0f);
            }

            return Texture.New2D(device, Size.X, Size.Y, PixelFormat.R8_UNorm, data);
        }
    }
}

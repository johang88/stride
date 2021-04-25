using System;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Stride.Assets.Terrain;
using Stride.Core.Assets.Serializers;
using Stride.Core.Reflection;
using Stride.Core.Yaml.Events;
using Stride.Core.Yaml.Serialization;
using Stride.Core.Yaml.Serialization.Serializers;
using Stride.Engine;
using Stride.Terrain;

namespace Stride.Assets.Serializers
{
    /// <summary>
    /// Serializes height data and splat maps as compressed base 64 strings
    /// It improve the serialization speed somewhat, but it depends on the map details
    /// </summary>
    [YamlSerializerFactory(YamlAssetProfile.Name)]
    public class TerrainSerializer : ObjectSerializer
    {
        public override IYamlSerializable TryCreate(SerializerContext context, ITypeDescriptor typeDescriptor)
        {
            if (typeDescriptor.Type == typeof(TerrainDataAsset) || typeDescriptor.Type == typeof(TerrainDataAsset.TerrainLayerData))
            {
                return this;
            }
            else
            {
                return null;
            }
        }

        protected override void CreateOrTransformObject(ref ObjectContext objectContext)
        {
            base.CreateOrTransformObject(ref objectContext);

            if (objectContext.Instance is TerrainDataAsset terrain)
            {
                var splatMapSize = terrain.SplatMapResolution.X * terrain.SplatMapResolution.Y;
                foreach (var layer in terrain.Layers)
                {
                    if (layer.Data == null || layer.Data.Length != splatMapSize)
                    {
                        layer.Data = new byte[splatMapSize];
                    }
                }
            }
        }

        protected override void WriteMemberValue(ref ObjectContext objectContext, IMemberDescriptor member, object memberValue, Type memberType)
        {
            if (member.Name == nameof(TerrainData.Heightmap))
            {
                var floatArrayData = (float[])memberValue;
                var byteArrayData = new byte[floatArrayData.Length * sizeof(float)];
                Buffer.BlockCopy(floatArrayData, 0, byteArrayData, 0, byteArrayData.Length);

                var base64String = Convert.ToBase64String(Compress(byteArrayData));
                objectContext.Writer.Emit(new ScalarEventInfo(base64String, typeof(string))
                {
                    RenderedValue = base64String,
                    IsPlainImplicit = true
                });
            }
            else if (member.Name == nameof(TerrainDataAsset.TerrainLayerData.Data))
            {
                var base64String = Convert.ToBase64String(Compress((byte[])memberValue));
                objectContext.Writer.Emit(new ScalarEventInfo(base64String, typeof(string))
                {
                    RenderedValue = base64String,
                    IsPlainImplicit = true
                });
            }
            else
            {
                base.WriteMemberValue(ref objectContext, member, memberValue, memberType);
            }
        }

        protected override object ReadMemberValue(ref ObjectContext objectContext, IMemberDescriptor member, object memberValue, Type memberType)
        {
            if (member.Name == nameof(TerrainDataAsset.Heightmap))
            {
                var scalar = objectContext.Reader.Expect<Scalar>();
                var base64String = scalar.Value;
                var byteArrayData = Decompress(Convert.FromBase64String(base64String));

                var floatArrayData = new float[byteArrayData.Length / 4];
                Buffer.BlockCopy(byteArrayData, 0, floatArrayData, 0, byteArrayData.Length);

                return floatArrayData;
            }
            else if (member.Name == nameof(TerrainDataAsset.TerrainLayerData.Data))
            {
                var scalar = objectContext.Reader.Expect<Scalar>();
                var base64String = scalar.Value;
                var byteArrayData = Decompress(Convert.FromBase64String(base64String));

                return byteArrayData;
            }
            else
            {
                return base.ReadMemberValue(ref objectContext, member, memberValue, memberType);
            }
        }

        private static byte[] Compress(byte[] data)
        {
            using (var result = new MemoryStream())
            {
                using (var compressionStream = new GZipStream(result, CompressionLevel.Fastest))
                {
                    compressionStream.Write(data, 0, data.Length);
                    compressionStream.Flush();
                }

                return result.ToArray();
            }
        }

        private static byte[] Decompress(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                using (var decompressionStream = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new MemoryStream())
                {
                    decompressionStream.CopyTo(reader);
                    return reader.ToArray();
                }
            }
        }
    }
}

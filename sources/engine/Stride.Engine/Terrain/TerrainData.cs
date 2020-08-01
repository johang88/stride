using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;

namespace Stride.Terrain
{
    /// <summary>
    /// Terrain data asset used by the terrain system
    /// </summary>
    [DataContract]
    [ContentSerializer(typeof(DataContentSerializer<TerrainData>))]
    [DataSerializerGlobal(typeof(CloneSerializer<TerrainData>), Profile = "Clone")]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainData>), Profile = "Content")]
    public class TerrainData
    {
        /// <summary>
        /// Size of the height map
        /// </summary>
        [DataMember(10)]
        public Int2 Resolution { get; set; }

        /// <summary>
        /// Height map data
        /// </summary>
        [DataMember(20)]
        [Display(Browsable = false)]
        public ushort[] Heightmap { get; set; }

        /// <summary>
        /// Terrain layers and their splat map data (if available)
        /// </summary>
        [DataMember(30)]
        [Display(Browsable = false)]
        public List<TerrainLayerData> Layers { get; set; }

        public bool IsValidCoordinate(int x, int y)
            => x >= 0 && x < Resolution.X && y >= 0 && y < Resolution.Y;

        public int GetHeightIndex(int x, int y)
            => y * Resolution.X + x;

        /// <summary>
        /// Get height at a specific point
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns>Height in the range [0, 1]</returns>
        public float GetHeightAt(int x, int y)
        {
            if (!IsValidCoordinate(x, y))
            {
                return 0.0f;
            }

            var index = GetHeightIndex(x, y);
            var heightData = Heightmap[index];

            return ConvertToFloatHeight(ushort.MinValue, ushort.MaxValue, heightData);
        }

        public Vector3 GetNormal(int x, int y, float heightScale)
        {
            var heightL = GetHeightAt(x - 1, y) * heightScale;
            var heightR = GetHeightAt(x + 1, y) * heightScale;
            var heightD = GetHeightAt(x, y - 1) * heightScale;
            var heightU = GetHeightAt(x, y + 1) * heightScale;

            var normal = new Vector3(heightL - heightR, 2.0f, heightD - heightU);
            normal.Normalize();

            return normal;
        }

        public Vector3 GetTangent(int x, int z)
        {
            var flip = 1;
            var here = new Vector3(x, GetHeightAt(x, z), z);
            var left = new Vector3(x - 1, GetHeightAt(x - 1, z), z);
            if (left.X < 0.0f)
            {
                flip *= -1;
                left = new Vector3(x + 1, GetHeightAt(x + 1, z), z);
            }

            left -= here;

            var tangent = left * flip;
            tangent.Normalize();

            return tangent;
        }

        public static float ConvertToFloatHeight(float minValue, float maxValue, float value) => MathUtil.InverseLerp(minValue, maxValue, MathUtil.Clamp(value, minValue, maxValue));
    }

    [DataContract]
    public class TerrainLayerData
    {
        public TerrainLayer Layer { get; set; }
        /// <summary>
        /// Optional splat map data for the terrain layer
        /// this is per terrain data so can't be stored in the layer asset itself
        /// 
        /// Can this be a texture instead?
        /// </summary>
        public byte[] Data { get; set; }
    }
}

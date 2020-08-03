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
        /// Resolution of any attached splat maps
        /// </summary>
        [DataMember(20)]
        public Int2 SplatMapResolution { get; set; }

        /// <summary>
        /// Size / Scale of the terrain in world units
        /// </summary>
        [DataMember(30)]
        public Vector3 Size { get; set; }

        /// <summary>
        /// Height map data
        /// </summary>
        [DataMember(20)]
        [Display(Browsable = false)]
        public float[] Heightmap { get; set; }

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
            return Heightmap[index];
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

        /// <summary>
        /// Convert object space coordinate to a height map index
        /// </summary>
        /// <param name="x"></param>
        /// <param name="z"></param>
        /// <param name="point"></param>
        /// <returns>True if in bounds</returns>
        public bool PositionToHeightMapIndex(float x, float z, out Int2 point)
        {
            // Terrain is centered around the origin so dispalce the coordinates
            var offset = Size / 2.0f;

            x += offset.X;
            z += offset.Z;

            // Scale
            x /= Size.X;
            z /= Size.Z;

            // Check bounds
            if (x < 0.0f || x >= 1.0f || z < 0.0f || z >= 1.0f)
            {
                point = new Int2(0, 0);
                return false;
            }

            int xi = (int)(x * Resolution.X), zi = (int)(z * Resolution.Y);

            point = new Int2(xi, zi);
            return true;
        }

        /// <summary>
        /// Get height at point (x, z) coordinates are assumed to 
        /// in object space, but not adjusted for the terrains  origin
        /// </summary>
        /// <param name="x"></param>
        /// <param name="z"></param>
        /// <returns></returns>
        public float GetHeightAt(float x, float z)
        {
            // We don't use PositionToHeightMapIndex as we need the transformed x,z coordinates
            // Terrain is centered around the origin so dispalce the coordinates
            var offset = Size / 2.0f;

            x += offset.X;
            z += offset.Z;

            // Scale
            x /= Size.X;
            z /= Size.Z;

            // Check bounds
            if (x < 0.0f || x >= 1.0f || z < 0.0f || z >= 1.0f)
            {
                return -1.0f;
            }

            x *= Resolution.X;
            z *= Resolution.Y;

            var xi = (int)x;
            var zi = (int)z;

            var xpct = x - xi;
            var zpct = z - zi;

            if (xi == Resolution.X - 1)
            {
                --xi;
                xpct = 1.0f;
            }
            if (zi == Resolution.Y - 1)
            {
                --zi;
                zpct = 1.0f;
            }

            var heights = new float[]
            {
                GetHeightAt(xi, zi),
                GetHeightAt(xi, zi + 1),
                GetHeightAt(xi + 1, zi),
                GetHeightAt(xi + 1, zi + 1)
            };

            var w = new float[]
            {
                (1.0f - xpct) * (1.0f - zpct),
                (1.0f - xpct) * zpct,
                xpct * (1.0f - zpct),
                xpct * zpct
            };

            var height = w[0] * heights[0] + w[1] * heights[1] + w[2] * heights[2] + w[3] * heights[3];

            return height * Size.Y;
        }

        /// <summary>
        /// Get the intersection point of a ray with the terrain
        /// coordinates are assumed to in object space, but not adjusted for the terrains  origin
        /// </summary>
        /// <param name="ray"></param>
        /// <param name="point"></param>
        /// <returns></returns>
        public bool Intersects(Ray ray, out Vector3 point)
        {
            var bounds = new BoundingBox(-Size * new Vector3(0.5f, 0, 0.5f), Size * new Vector3(0.5f, 1.0f, 0.5f));

            point = ray.Position;

            // Check if we intersect at all
            if (bounds.Contains(ref point) == ContainmentType.Disjoint)
            {
                if (ray.Intersects(ref bounds, out float distance))
                {
                    point += ray.Direction * distance;
                }
                else
                {
                    return false;
                }
            }

            // Trace along the ray until we leave bounds or intersect the terrain
            while (true)
            {
                var height = GetHeightAt(point.X, point.Z);
                if (point.Y <= height)
                {
                    point.Y = height;
                    return true;
                }

                point += ray.Direction;

                if (point.X < bounds.Minimum.X || point.X > bounds.Maximum.X || point.Z < bounds.Minimum.Z || point.Z > bounds.Maximum.Z)
                {
                    return false;
                }
            }
        }
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

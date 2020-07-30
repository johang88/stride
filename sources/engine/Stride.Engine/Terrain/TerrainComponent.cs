using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Engine.Design;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Terrain
{
    [DataContract]
    [DefaultEntityComponentRenderer(typeof(TerrainProcessor))]
    public class TerrainComponent : EntityComponent
    {
        private TerrainData _terrain;
        /// <summary>
        /// Terrain asset
        /// </summary>
        [DataMember(10)]
        public TerrainData Terrain
        {
            get { return _terrain; }
            set
            {
                _terrain = value;

                // Force mesh to be recreated
                Invalidate(true, true);
            }
        }

        /// <summary>
        /// Size of the terrain in world units
        /// </summary>
        [DataMember(20)]
        public Vector3 Size { get; set; }

        [DataMember(30)]
        public bool CastShadows { get; set; }

        [DataMember(40)]
        public Material Material { get; set; }

        [DataMemberIgnore]
        internal bool VerticesInvalidated { get; set; }
        [DataMemberIgnore]
        internal bool NormalsInvalidated { get; set; }

        /// <summary>
        /// Force recaluclation of heights / and normals
        /// </summary>
        public void Invalidate(bool vertices, bool normals)
        {
            VerticesInvalidated = vertices;
            NormalsInvalidated = normals;
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

            int xi = (int)(x * Terrain.Size.X), zi = (int)(z * Terrain.Size.Y);

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

            x *= Terrain.Size.X;
            z *= Terrain.Size.Y;

            var xi = (int)x;
            var zi = (int)z;

            var xpct = x - xi;
            var zpct = z - zi;

            if (xi == Terrain.Size.X - 1)
            {
                --xi;
                xpct = 1.0f;
            }
            if (zi == Terrain.Size.Y - 1)
            {
                --zi;
                zpct = 1.0f;
            }

            var heights = new float[]
            {
                Terrain.GetHeightAt(xi, zi),
                Terrain.GetHeightAt(xi, zi + 1),
                Terrain.GetHeightAt(xi + 1, zi),
                Terrain.GetHeightAt(xi + 1, zi + 1)
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
}

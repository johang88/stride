using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Terrain;

namespace Stride.Physics
{
    [DataContract]
    [Display("Terrain")]
    public class TerrainDataHeightStickArraySource : IHeightStickArraySource
    {
        [DataMember(10)]
        public TerrainData Terrain { get; set; }

        public Int2 HeightStickSize => Terrain?.Resolution ?? default;

        public HeightfieldTypes HeightType => HeightfieldTypes.Float;

        public Vector2 HeightRange => new Vector2(0.0f, Terrain?.Size.Y ?? 1.0f);

        public float HeightScale => Terrain?.Size.Y ?? 1.0f;

        public void CopyTo<T>(UnmanagedArray<T> heightStickArray, int index) where T : struct
        {
            if (Terrain == null) throw new InvalidOperationException($"{ nameof(Terrain) } is a null");
            if (heightStickArray == null) throw new ArgumentNullException(nameof(heightStickArray));

            var heightStickArrayLength = heightStickArray.Length - index;
            if (heightStickArrayLength <= 0) throw new IndexOutOfRangeException(nameof(index));

            if (typeof(T) != typeof(float)) throw new InvalidOperationException("T must be float");

            var heightData = (float[])Terrain.Heightmap.Clone();
            for (var i = 0; i < heightData.Length; i++)
            {
                heightData[i] *= Terrain.Size.Y;
            }
            
            var heights = (T[])(object)heightData;

            var heightsLength = heights.Length;
            if (heightStickArrayLength < heightsLength) throw new ArgumentException($"{ nameof(heightStickArray) }.{ nameof(heightStickArray.Length) } is not enough to copy.");

            heightStickArray.Write(heights, index * Utilities.SizeOf<T>(), 0, heightsLength);
        }

        public bool IsValid()
            => Terrain != null && Terrain.Resolution.X > 0 && Terrain.Resolution.Y > 0 && Terrain.Heightmap != null;

        public bool Match(object obj)
        {
            if (!(obj is TerrainDataHeightStickArraySource other))
            {
                return false;
            }

            return other.Terrain == Terrain;
        }
    }
}

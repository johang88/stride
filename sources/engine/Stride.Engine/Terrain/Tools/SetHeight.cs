using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Tools
{
    [DataContract]
    public class SetHeight : Terrain.BaseTool
    {
        public float Height { get; set; }

        public void Apply(TerrainData terrain, TerrainBrush brush, float strength, int size, Int2 point, HashSet<int> modifiedIndices)
        {
            for (var y = -size; y < size; y++)
            {
                for (var x = -size; x < size; x++)
                {
                    var xi = point.X + x;
                    var yi = point.Y + y;

                    if (xi < 0 || yi < 0 || xi >= terrain.Resolution.X || yi >= terrain.Resolution.Y)
                        continue;

                    var distance = (float)Math.Sqrt(y * y + x * x);
                    var factor = size - distance;

                    if (factor <= 0.0f)
                        continue;

                    var index = yi * terrain.Resolution.X + xi;
                    terrain.Heightmap[index] = Height / terrain.Size.Y;

                    modifiedIndices.Add(index);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Tools
{
    [DataContract]
    public class RaiseLower : BaseTool
    {
        protected override void ApplyTool(TerrainData terrain, int x, int y, float strength, HashSet<int> modifiedIndices)
        {
            var index = y * terrain.Resolution.X + x;
            var height = terrain.Heightmap[index];

            terrain.Heightmap[index] = MathUtil.Clamp(height + strength, 0.0f, 1.0f);

            modifiedIndices.Add(index);
        }
    }
}

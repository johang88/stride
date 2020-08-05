using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Tools
{
    [DataContract]
    public class SetHeight : BaseTool
    {
        public float Height { get; set; }

        protected override void ApplyTool(TerrainData terrain, int x, int y, float strength, HashSet<int> modifiedIndices)
        {
            var index = y * terrain.Resolution.X + x;
            terrain.Heightmap[index] = Height / terrain.Size.Y;

            modifiedIndices.Add(index);
        }
    }
}

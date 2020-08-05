using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;

namespace Stride.Terrain
{
    public interface BaseTool
    {
        void Apply(TerrainData terrain, TerrainBrush brush, float strength, int size, Int2 point, HashSet<int> modifiedIndices);
    }
}

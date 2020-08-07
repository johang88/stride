using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;

namespace Stride.Terrain
{
    public interface ITerrainTool
    {
        void Apply(TerrainData terrain, TerrainBrush brush, float intensity, int size, Int2 point, ToolInvalidationData invalidationData);
    }

    public class ToolInvalidationData
    {
        public bool SplatMaps { get; set; }
        public HashSet<int> ModifiedIndices { get; set; }
    }
}

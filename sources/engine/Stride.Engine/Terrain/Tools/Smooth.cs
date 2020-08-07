using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;

namespace Stride.Terrain.Tools
{
    /// <summary>
    /// Simple box filter (3x3 samples) smoothing brush 
    /// </summary>
    [DataContract]
    public class Smooth : BaseTool
    {
        private float GetSample(TerrainData terrain, int x, int y, ref float factor)
        {
            if (x >= 0 && x  < terrain.Resolution.X && y >= 0 && y < terrain.Resolution.Y)
            {
                factor += 1.0f;
                return terrain.Heightmap[y * terrain.Resolution.X + x];
            }
            else
            {
                return 0.0f;
            }
        }

        protected override void ApplyTool(TerrainData terrain, int x, int y, float intensity, ToolInvalidationData invalidationData)
        {
            var px1 = x - 1;
            var px2 = x + 1;
            var py1 = y - 1;
            var py2 = y + 1;

            var factor = 0.0f;
            var samples = GetSample(terrain, x, y, ref factor);

            samples += GetSample(terrain, px1, py1, ref factor);
            samples += GetSample(terrain, px1, py2, ref factor);
            samples += GetSample(terrain, px2, py1, ref factor);
            samples += GetSample(terrain, px2, py2, ref factor);

            samples += GetSample(terrain, px1, y, ref factor);
            samples += GetSample(terrain, px2, y, ref factor);
            samples += GetSample(terrain, x, py1, ref factor);
            samples += GetSample(terrain, x, py2, ref factor);

            samples /= factor;

            var height = terrain.Heightmap[y * terrain.Resolution.X + x];
            var delta = height - samples;

            terrain.Heightmap[y * terrain.Resolution.X + x] = (height - (delta * intensity * 100));

            invalidationData.ModifiedIndices.Add(y * terrain.Resolution.X + x);
        }
    }
}

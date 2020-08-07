using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;

namespace Stride.Terrain.Tools
{
    public abstract class BaseTool : Terrain.ITerrainTool
    {
        public virtual void Apply(TerrainData terrain, TerrainBrush brush, float intensity, int size, Int2 point, ToolInvalidationData invalidationData)
        {
            if (!IsValid(terrain))
                return;

            var brushIndexMultiplierX = (size * 2.0f) / brush.Size.X;
            var brushIndexMultiplierY = (size * 2.0f) / brush.Size.Y;

            // TODO: Should we maybe interpolate the brush?

            for (var y = -size; y < size; y++)
            {
                for (var x = -size; x < size; x++)
                {
                    var xi = point.X + x;
                    var yi = point.Y + y;

                    if (xi < 0 || yi < 0 || xi >= terrain.Resolution.X || yi >= terrain.Resolution.Y)
                        continue;

                    var brushIndexX = (int)Math.Round((x + size) / brushIndexMultiplierX, MidpointRounding.AwayFromZero);
                    var brushIndexY = (int)Math.Round((y + size) / brushIndexMultiplierY, MidpointRounding.AwayFromZero);

                    var factor = brush.Data[brushIndexY * brush.Size.X + brushIndexX];
                    if (factor > 0)
                    {
                        ApplyTool(terrain, xi, yi, intensity * factor, invalidationData);
                    }
                }
            }
        }

        protected virtual bool IsValid(TerrainData terrain) => true;
        protected abstract void ApplyTool(TerrainData terrain, int x, int y, float strength, ToolInvalidationData invalidationData);
    }
}

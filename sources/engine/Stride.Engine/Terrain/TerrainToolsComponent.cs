using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.Terrain
{
    /// <summary>
    /// Provides tools for the terrain
    /// Not the most beutiful solution but it works ... at least I don't have to mess with WPF for now :D
    /// </summary>
    [DataContract]
    public class TerrainToolsComponent : EntityComponent
    {
        [DataMember(10)]
        public TerrainBrush Brush { get; set; }
        [DataMember(11), DataMemberRange(1.0, 500.0, 0.1, 0.1, 1)]
        public float Strength { get; set; } = 1.0f;
        [DataMember(12), DataMemberRange(1, 500, 1, 1, 0)]
        public int Size { get; set; } = 16;

        [DataMember(20)]
        public ITerrainTool Tool { get; set; }

        public void Apply(TerrainProcessor terrainProcessor, TerrainComponent terrainComponent, Int2 point, float strengthModifier, ToolInvalidationData invalidationData)
        {
            if (Tool == null || Brush == null || Size <= 0)
                return;

            var strength = (Strength / 100.0f) * strengthModifier * 0.1f;

            Tool.Apply(terrainComponent.Terrain, Brush, strength, Size, point, invalidationData);

            terrainProcessor.Invalidate(terrainComponent, invalidationData.ModifiedIndices, invalidationData.SplatMaps);
        }
    }
}

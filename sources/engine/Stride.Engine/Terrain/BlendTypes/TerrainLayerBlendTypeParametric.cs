using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core;

namespace Stride.Terrain.BlendTypes
{
    [DataContract]
    public class TerrainLayerBlendTypeParametric : ITerrainLayerBlendType
    {
        [DataMember(10)]
        public float MinHeight { get; set; }
        [DataMember(11)]
        public float MaxHeight { get; set; }
        [DataMember(12)]
        public float HeightStrength { get; set; }

        [DataMember(20)]
        public float MinSlope { get; set; }
        [DataMember(21)]
        public float MaxSlope { get; set; }
        [DataMember(22)]
        public float SlopeStrength { get; set; }
    }
}

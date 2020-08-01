using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [DataContract(nameof(TerrainDataAsset))]
    [AssetDescription(FileExtension)]
    [AssetContentType(typeof(TerrainData))]
    [AssetFormatVersion(StrideConfig.PackageName, CurrentVersion, "3.0.0.0")]
    public class TerrainDataAsset :  Asset
    {
        private const string CurrentVersion = "3.0.0.0";
        public const string FileExtension = ".sdter";

        /// <summary>
        /// Resolution of the height map
        /// </summary>
        [DataMember(10)]
        [Display(Browsable = false)]
        public Int2 Resolution { get; set; }

        /// <summary>
        /// Resolution of any attached splat maps
        /// </summary>
        [DataMember(20)]
        public Int2 SplatMapResolution { get; set; }

        /// <summary>
        /// Size / Scale of the terrain in world units
        /// </summary>
        [DataMember(30)]
        public Vector3 Size { get; set; }

        /// <summary>
        /// Height map data
        /// </summary>
        [DataMember(40)]
        [Display(Browsable = false)]
        [NonIdentifiableCollectionItems]
        public ushort[] Heightmap { get; set; }

        [DataMember(50)]
        public List<TerrainLayerData> Layers { get; set; } = new List<TerrainLayerData>();

        [DataContract(nameof(TerrainLayerData))]
        public class TerrainLayerData
        {
            [DataMember(10)]
            public TerrainLayer Layer { get; set; }

            [DataMember(20)]
            [Display(Browsable = false)]
            [NonIdentifiableCollectionItems]
            public byte[] Data { get; set; }
        }
    }
}

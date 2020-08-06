using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Yaml.Tokens;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [DataContract(nameof(TerrainDataAsset))]
    [AssetDescription(FileExtension)]
    [AssetContentType(typeof(TerrainData))]
    [AssetFormatVersion(StrideConfig.PackageName, CurrentVersion, "3.0.0.0")]
    [Display("Terrain")]
    public class TerrainDataAsset :  Asset
    {
        private const string CurrentVersion = "3.0.0.0";
        public const string FileExtension = ".sdter";

        private Int2 _resolution;

        /// <summary>
        /// Resolution of the height map
        /// </summary>
        [DataMember(10)]
        [Display(Browsable = false)]
        public Int2 Resolution
        {
            get { return _resolution; }
            set
            {
                // Resize if needed
                if (value.X > 0 && value.Y > 0 && (Heightmap == null || Heightmap.Length != value.X * value.Y))
                {
                    Heightmap = new float[value.X * value.Y];
                }

                _resolution = value;
            }
        }

        private Int2 _splatMapResolution;
        /// <summary>
        /// Resolution of any attached splat maps
        /// </summary>
        [DataMember(20)]
        public Int2 SplatMapResolution
        {
            get { return _splatMapResolution; }
            set
            {
                // Resize if needed
                if (Layers != null)
                {
                    var size = value.X * value.Y;
                    if (size > 0)
                    {
                        foreach (var layer in Layers)
                        {
                            if (layer.Data == null || layer.Data.Length != size)
                            {
                                layer.Data = new byte[size];
                            }
                        }
                    }
                }

                _splatMapResolution = value;
            }
        }

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
        public float[] Heightmap { get; set; }

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

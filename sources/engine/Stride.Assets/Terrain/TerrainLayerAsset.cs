using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Rendering;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [DataContract(nameof(TerrainLayerAsset))]
    [AssetDescription(FileExtension)]
    [AssetContentType(typeof(TerrainLayer))]
    [AssetFormatVersion(StrideConfig.PackageName, CurrentVersion, "3.0.0.0")]
    public class TerrainLayerAsset : Asset
    {
        private const string CurrentVersion = "3.0.0.0";
        public const string FileExtension = ".sdtly";

        [DataMember(10)]
        public string Name { get; set; }

        [DataMember(20)]
        public Material Material { get; set; }
    }
}

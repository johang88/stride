using System.Collections.Generic;
using System.ComponentModel;
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
    [Display("Terrain Layer")]
    public class TerrainLayerAsset : Asset
    {
        private const string CurrentVersion = "3.0.0.0";
        public const string FileExtension = ".sdterrainlayer";

        [DataMember(10)]
        public string Name { get; set; }

        [DataMember(20)]
        public Material Material { get; set; }

        [DataMember(30)]
        public Vector2 UVScale { get; set; } = new Vector2(1, 1);

        [DataMember(40), Display(Expand = ExpandRule.Always)]
        public ITerrainLayerBlendType BlendType { get; set; }
    }
}

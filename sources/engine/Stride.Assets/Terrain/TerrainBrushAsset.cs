using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    [DataContract(nameof(TerrainBrushAsset))]
    [AssetDescription(FileExtension)]
    [AssetContentType(typeof(TerrainBrush))]
    [AssetFormatVersion(StrideConfig.PackageName, CurrentVersion, "3.0.0.0")]
    [Display("Terrain Brush")]
    public class TerrainBrushAsset : AssetWithSource
    {
        private const string CurrentVersion = "3.0.0.0";
        public const string FileExtension = ".sdterrainbrush";

        [DataMember(10, "sRGB sampling")]
        public bool IsSRgb { get; set; } = false;
    }
}

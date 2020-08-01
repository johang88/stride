using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Core.Assets;

namespace Stride.Assets.Terrain
{
    public class TerrainBrushAssetFactory : AssetFactory<TerrainBrushAsset>
    {
        public override TerrainBrushAsset New()
            => new TerrainBrushAsset();
    }
}

using Stride.Core.Assets;

namespace Stride.Assets.Terrain
{
    public class TerrainLayerAssetFactory : AssetFactory<TerrainLayerAsset>
    {
        public override TerrainLayerAsset New()
        {
            return new TerrainLayerAsset
            {
                Name = "New layer"
            };
        }
    }
}

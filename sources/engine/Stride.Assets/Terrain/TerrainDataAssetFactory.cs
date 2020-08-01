using Stride.Core.Assets;

namespace Stride.Assets.Terrain
{
    public class TerrainDataAssetFactory : AssetFactory<TerrainDataAsset>
    {
        public override TerrainDataAsset New()
        {
            var asset = new TerrainDataAsset
            {
                Size = new Core.Mathematics.Int2(512, 512),
                Heightmap = new ushort[512 * 512]
            };

            for (var i = 0; i< asset.Heightmap.Length;i++)
            {
                asset.Heightmap[i] = 128;
            }

            return asset;
        }
    }
}

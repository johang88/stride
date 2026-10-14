// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Assets;
using Stride.Core.Mathematics;

namespace Stride.Assets.Terrain
{
    public sealed class TerrainAssetFactory : AssetFactory<TerrainAsset>
    {
        public override TerrainAsset New() => new TerrainAsset();
    }

    public sealed class TerrainMaterialAssetFactory : AssetFactory<TerrainMaterialAsset>
    {
        public override TerrainMaterialAsset New()
        {
            var asset = new TerrainMaterialAsset();
            asset.Layers.Add(new TerrainLayerDescription { Name = "Grass", Tint = new Color3(0.25f, 0.35f, 0.15f) });
            asset.Layers.Add(new TerrainLayerDescription { Name = "Dirt", Tint = new Color3(0.35f, 0.27f, 0.18f) });
            asset.Layers.Add(new TerrainLayerDescription { Name = "Rock", Tint = new Color3(0.4f, 0.4f, 0.4f) });
            return asset;
        }
    }
}

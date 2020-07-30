using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stride.Assets.Presentation.AssetEditors.TerrainEditor.ViewModels;
using Stride.Assets.Terrain;
using Stride.Core.Assets.Editor.ViewModel;

namespace Stride.Assets.Presentation.ViewModel
{
    [AssetViewModel(typeof(TerrainDataAsset))]
    public class TerrainDataViewModel : AssetViewModel<TerrainDataAsset>
    {
        internal new TerrainEditorViewModel Editor => (TerrainEditorViewModel)base.Editor;

        //public BitmapSource ImageData { get; set; }

        public TerrainDataViewModel(AssetViewModelConstructionParameters parameters)
            : base(parameters)
        {
            var pixelFormat = PixelFormats.Gray16;
            var width = Asset.Size.X;
            var height = Asset.Size.Y;

            //ImageData = BitmapSource.Create(width, height, 96, 96, pixelFormat, null, Asset.Heightmap, 2 * width);
            //ImageData.Freeze();
        }
    }
}

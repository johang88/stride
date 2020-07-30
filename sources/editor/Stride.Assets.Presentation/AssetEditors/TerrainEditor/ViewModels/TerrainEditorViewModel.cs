using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.TerrainEditor.Views;
using Stride.Assets.Presentation.ViewModel;
using Stride.Assets.Terrain;
using Stride.Core.Annotations;
using Stride.Core.Assets.Editor.ViewModel;

namespace Stride.Assets.Presentation.AssetEditors.TerrainEditor.ViewModels
{
    [AssetEditorViewModel(typeof(TerrainDataAsset), typeof(TerrainEditorView))]
    public class TerrainEditorViewModel : AssetEditorViewModel
    {
        public ViewportViewModel Viewport { get; }
        public TerrainDataViewModel Terrain { get; }

        public TerrainEditorViewModel([NotNull] TerrainDataViewModel terrainData)
            : base(terrainData)
        {
            Viewport = new ViewportViewModel(ServiceProvider);
            Terrain = terrainData;
        }

        public override Task<bool> Initialize()
        {
            return Task.FromResult(false);
        }
    }
}

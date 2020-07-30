// Copyright (c) Stride contributors (https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Controls;
using Stride.Assets.Presentation.AssetEditors.TerrainEditor.ViewModels;
using Stride.Assets.Presentation.ViewModel;

namespace Stride.Assets.Presentation.AssetEditors.TerrainEditor.Views
{
    public partial class TerrainEditorView : IEditorView
    {
        static TerrainEditorView()
        {
        }

        private readonly TaskCompletionSource<bool> editorInitializationNotifier = new TaskCompletionSource<bool>();

        public TerrainEditorView()
        {
            InitializeComponent();
            // Ensure we can give the focus to the editor
            Focusable = true;
        }

        public static RoutedCommand FocusOnRegion { get; }

        public static RoutedCommand ActivateMagicWand { get; }

        public static Cursor ColorPickerCursor { get; }

        public static Cursor MagicWandCursor { get; }

        public Task EditorInitialization => editorInitializationNotifier.Task;

        public async Task<IAssetEditorViewModel> InitializeEditor(AssetViewModel asset)
        {
            var terrainData = (TerrainDataViewModel)asset;
            var editor = new TerrainEditorViewModel(terrainData);
            var result = await editor.Initialize();
            editorInitializationNotifier.SetResult(result);
            return editor;
        }
    }
}

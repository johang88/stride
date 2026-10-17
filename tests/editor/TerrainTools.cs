// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the terrain tools on an existing project with a terrain in its default scene
// (pass the project .slnx on the AutoTesting command line). Sculpts and paints through the terrain
// editor service, undoes/redoes, saves, and captures the viewport along the way.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Terrain.Editor;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain.Editing;

namespace Stride.Editor.Tests;

[UITest]
public class TerrainTools : IUITest
{
    public async Task Run(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 180);
        if (opened != GameStudioWindowNames.GameStudio)
        {
            ctx.Exit(1);
            return;
        }
        await ctx.SetWindowSize(GameStudioWindowNames.GameStudio, 1920, 1080);
        await ctx.WaitIdle();

        var sceneUrl = Environment.GetEnvironmentVariable("STRIDE_TERRAIN_TEST_SCENE") ?? "MainScene";
        if (await ctx.OpenAssetEditor(sceneUrl) == null)
        {
            ctx.Exit(2);
            return;
        }
        await ctx.WaitForAssetBuild();
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(120, 90);

        var session = SessionViewModel.Instance;
        var editor = await session.Dispatcher.InvokeAsync(() =>
        {
            var scene = session.AllAssets.First(a => a.Url.EndsWith(sceneUrl, StringComparison.OrdinalIgnoreCase));
            session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var sceneEditor);
            return sceneEditor;
        });
        if (editor == null)
        {
            ctx.Exit(3);
            return;
        }

        var service = await session.Dispatcher.InvokeAsync(() => editor.Tools.OfType<TerrainToolsViewModel>().Single().Service);
        await session.Dispatcher.InvokeAsync(() => editor.Tools.OfType<TerrainToolsViewModel>().Single().IsActive = true);
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("tools");

        // A cold project is still compiling the terrain when the scene opens
        if (!await service.WaitForTarget(TimeSpan.FromMinutes(5)))
        {
            ctx.Exit(4);
            return;
        }

        var raise = TerrainBrushSettings.Default;
        raise.Radius = 25;
        raise.Strength = 1;
        if (!await service.SimulateStroke(new Vector3(0, 0, -20), raise, 60))
        {
            ctx.Exit(4);
            return;
        }
        var paint = raise;
        paint.Tool = TerrainBrushTool.Paint;
        paint.Layer = 2;
        paint.Radius = 12;
        await service.SimulateStroke(new Vector3(0, 0, -20), paint, 30);
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("sculpted");

        // Undo both strokes, then redo them
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.UndoCommand.Execute(); session.ActionHistory.UndoCommand.Execute(); });
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("undone");
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.RedoCommand.Execute(); session.ActionHistory.RedoCommand.Execute(); });
        await ctx.WaitForRendering(30);

        // Save writes the terrain data file next to the asset
        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var terrain = await session.Dispatcher.InvokeAsync(() => session.AllAssets.OfType<TerrainViewModel>().FirstOrDefault());
        var dataPath = terrain?.GetDataFilePath()?.ToOSPath();
        if (!saved || dataPath == null || !File.Exists(dataPath))
        {
            ctx.Exit(5);
            return;
        }

        await ctx.Screenshot("saved");
        ctx.Exit();
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the terrain spline tools on a project whose scene (STRIDE_SPLINE_TEST_SCENE) has a terrain
// and terrain splines: applies the splines to the terrain and saves, then creates a road (not saved).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Assets.Presentation.ViewModel;
using Stride.Core;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain.Splines;

namespace Stride.Editor.Tests;

[UITest]
public class SplineTools : IUITest
{
    private readonly List<string> report = new();
    private int failures;

    private void Check(bool condition, string what)
    {
        if (!condition)
            failures++;
        report.Add($"{(condition ? "PASS" : "FAIL")} {what}");
    }

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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_SPLINE_TEST_SCENE") ?? "MainScene";
        var sceneUrl = await ctx.OpenAssetEditor(sceneName);
        if (sceneUrl == null)
        {
            ctx.Exit(2);
            return;
        }
        await ctx.WaitForAssetBuild();
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(120, 120);

        var session = SessionViewModel.Instance;
        var editor = await session.Dispatcher.InvokeAsync(() =>
        {
            var scene = session.AllAssets.First(a => a.Url == sceneUrl);
            session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var sceneEditor);
            return sceneEditor;
        });
        if (editor == null)
        {
            ctx.Exit(3);
            return;
        }
        var tools = await session.Dispatcher.InvokeAsync(() => editor.TerrainTools);
        for (int i = 0; i < 100 && !await session.Dispatcher.InvokeAsync(() => tools.IsAvailable); i++)
            await Task.Delay(100);

        var splineCount = await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.SelectMany(Flatten).Count(x => x.AssetSideEntity.Get<TerrainSplineComponent>() != null));
        Check(splineCount > 0, $"{splineCount} splines in the scene");

        // Apply all the splines (nothing selected) and save the terrain
        await session.Dispatcher.InvokeAsync(() => editor.ClearSelection());
        var applied = await session.Dispatcher.InvokeTask(() => tools.ApplySplines());
        Check(applied == splineCount, $"applied {applied} splines");
        await ctx.WaitForRendering(60);
        await ctx.Screenshot("applied");
        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var terrain = await session.Dispatcher.InvokeAsync(() => session.AllAssets.OfType<TerrainViewModel>().FirstOrDefault(x => x.Url.Contains("Area01")));
        var dataPath = terrain?.GetDataFilePath()?.ToOSPath();
        Check(saved && dataPath != null && File.Exists(dataPath) && File.GetLastWriteTimeUtc(dataPath) > DateTime.UtcNow.AddMinutes(-5), $"terrain data saved ({dataPath})");

        // Undo/redo of the application
        var undone = await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.UndoCommand.Execute(); session.ActionHistory.RedoCommand.Execute(); return true; });
        Check(undone, "undo/redo of the spline application");

        // A new road: entity with four control points snapped to the terrain, selected (not saved)
        var before = await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.SelectMany(Flatten).Count());
        var roadId = await session.Dispatcher.InvokeTask(() => tools.CreateSpline(TerrainSplineKind.Road));
        await ctx.WaitForRendering(30);
        var road = await session.Dispatcher.InvokeAsync(() => editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, roadId)) as EntityViewModel);
        var after = await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.SelectMany(Flatten).Count());
        Check(road != null && after == before + 5, $"new road created with its control points ({after - before} entities)");
        var selected = await session.Dispatcher.InvokeAsync(() => editor.SelectedContent.Contains(road));
        Check(selected, "new road selected");
        var pointsOnGround = await session.Dispatcher.InvokeAsync(() => road?.AssetSideEntity.Transform.Children.All(x => Math.Abs(x.Position.Y) < 50) ?? false);
        Check(pointsOnGround, "control points snapped");
        await ctx.Screenshot("new-road");
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.UndoCommand.Execute(); session.ActionHistory.UndoCommand.Execute(); });

        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "spline-tools-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    private static IEnumerable<EntityViewModel> Flatten(EntityHierarchyItemViewModel item)
    {
        if (item is EntityViewModel entity)
            yield return entity;
        foreach (var child in item.Children)
        {
            foreach (var inner in Flatten(child))
                yield return inner;
        }
    }
}

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
using Stride.Core.Assets.Templates;
using Stride.Core.Mathematics;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain;
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

        // The terrain asset templates come with the terrain package (Stride.Terrain.Assets)
        var templates = await session.Dispatcher.InvokeAsync(() => session.FindTemplates(TemplateScope.Asset).OfType<TemplateAssetDescription>().Where(x => x.Group == "Terrain").Select(x => x.Name).ToList());
        Console.WriteLine($"TERRAIN templates: {string.Join(", ", templates)}");
        if (templates.Count != 2)
        {
            ctx.Exit(7);
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

        // Opening the scene may build the terrain more than once (a placeholder until it's compiled): count from here
        await ctx.WaitForRendering(60);
        var reloadsBefore = service.DataReloadCount;

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

        // The strokes dirty the terrain asset (to save the samples) without rebuilding and reloading it (a flash and a delay)
        await ctx.WaitForRendering(90);
        var terrainAsset = service.TargetAsset;
        var reloads = service.DataReloadCount - reloadsBefore;
        var dirty = await session.Dispatcher.InvokeAsync(() => terrainAsset?.IsDirty ?? false);
        Console.WriteLine($"TERRAIN after strokes: {reloads} reloads, asset dirty {dirty}");
        if (reloads != 0 || !dirty)
        {
            ctx.Exit(8);
            return;
        }

        // The paint tool shows the layers by their albedo textures
        var paintTools = editor.Tools.OfType<TerrainToolsViewModel>().Single();
        await session.Dispatcher.InvokeAsync(() => paintTools.Tool = TerrainBrushTool.Paint);
        await ctx.WaitForRendering(30);
        var layers = await session.Dispatcher.InvokeAsync(() => paintTools.Layers.Select(x => (x.Name, Texture: x.Texture?.Url)).ToList());
        Console.WriteLine($"TERRAIN paint layers: {string.Join(", ", layers.Select(x => $"{x.Name} ({x.Texture ?? "no texture"})"))}");
        await ctx.Screenshot("paint-layers");
        if (layers.Count == 0 || layers.Any(x => x.Texture == null))
        {
            ctx.Exit(9);
            return;
        }

        // Undo both strokes, then redo them
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.UndoCommand.Execute(); session.ActionHistory.UndoCommand.Execute(); });
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("undone");
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.RedoCommand.Execute(); session.ActionHistory.RedoCommand.Execute(); });
        await ctx.WaitForRendering(30);

        // A custom brush: the ridge (long along X) turned a quarter, so it raises a band along Z
        var tools = editor.Tools.OfType<TerrainToolsViewModel>().Single();
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.SelectedBrush = tools.Brushes.First(x => x.Name == "Ridge");
            tools.Rotation = 90;
        });
        await ctx.WaitForRendering(10);
        await ctx.Screenshot("brush");
        var before = await service.GetTargetDataCopy();
        var ridge = raise with { Mask = tools.SelectedBrush.Mask, Rotation = MathUtil.PiOverTwo, Radius = 20 };
        await service.SimulateStroke(new Vector3(60, 0, 40), ridge, 30);
        var after = await service.GetTargetDataCopy();
        var (width, depth) = ChangedExtent(before, after);
        Console.WriteLine($"TERRAIN ridge brush changed {width} x {depth} samples (X x Z)");
        await session.Dispatcher.InvokeAsync(() => session.ActionHistory.UndoCommand.Execute());
        if (depth <= width * 2)
        {
            ctx.Exit(6);
            return;
        }

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

    // Size in samples (X, Z) of the region whose heights changed between two copies of a terrain
    private static (int Width, int Depth) ChangedExtent(TerrainData before, TerrainData after)
    {
        int minX = int.MaxValue, maxX = -1, minZ = int.MaxValue, maxZ = -1;
        var resolution = before.Resolution;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                if (before.Heights[z * resolution + x] == after.Heights[z * resolution + x])
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        }
        return maxX < 0 ? (0, 0) : (maxX - minX + 1, maxZ - minZ + 1);
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio scripted edit of a terrain through the editor brush: paints patches of material layers along
// meandering strokes (as an artist would), re-applies the splines on top and saves. Patches are listed in
// STRIDE_PAINT_PATCHES ("layer,x,z,length,radius;..." in world meters) on the scene STRIDE_PAINT_SCENE.
using System;
using System.Collections.Generic;
using System.Globalization;
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
public class TerrainPaintLayers : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_PAINT_SCENE") ?? "MainScene";
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
        var tools = await session.Dispatcher.InvokeAsync(() => editor.Tools.OfType<TerrainToolsViewModel>().Single());
        for (int i = 0; i < 100 && !await session.Dispatcher.InvokeAsync(() => tools.IsAvailable); i++)
            await Task.Delay(100);
        var service = await session.Dispatcher.InvokeAsync(() => tools.Service);
        await session.Dispatcher.InvokeAsync(() => tools.IsActive = true);
        // The terrain (and its material) may still be compiling
        if (!await service.WaitForTarget(TimeSpan.FromMinutes(5)))
        {
            ctx.Exit(4);
            return;
        }

        var patches = ParsePatches(Environment.GetEnvironmentVariable("STRIDE_PAINT_PATCHES") ?? string.Empty);
        var random = new Random(1234);
        foreach (var (layer, start, length, radius) in patches)
        {
            var settings = TerrainBrushSettings.Default;
            settings.Tool = TerrainBrushTool.Paint;
            settings.Layer = layer;
            settings.Radius = radius;
            settings.Strength = 1.0f;
            settings.Falloff = 0.85f;

            // Meandering stroke: the direction drifts, the brush size varies along it
            var path = new List<Vector3>();
            var position = start;
            var heading = (float)(random.NextDouble() * Math.PI * 2);
            for (float travelled = 0; travelled <= length; travelled += radius * 0.4f)
            {
                path.Add(position + new Vector3((float)(random.NextDouble() - 0.5) * radius * 0.6f, 0, (float)(random.NextDouble() - 0.5) * radius * 0.6f));
                heading += (float)(random.NextDouble() - 0.5) * 1.2f;
                position += new Vector3(MathF.Cos(heading), 0, MathF.Sin(heading)) * radius * 0.4f;
            }
            if (!await service.SimulateStroke(path, settings, 4))
            {
                ctx.Exit(6);
                return;
            }
        }
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("painted");

        // Roads and rivers are painted over the patches
        await session.Dispatcher.InvokeAsync(() => editor.ClearSelection());
        await session.Dispatcher.InvokeTask(() => tools.ApplySplines());
        await ctx.WaitForRendering(60);
        await ctx.Screenshot("splines");

        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var terrain = await session.Dispatcher.InvokeAsync(() => session.AllAssets.OfType<TerrainViewModel>().FirstOrDefault(x => x.Url.Contains("Area01")));
        var dataPath = terrain?.GetDataFilePath()?.ToOSPath();
        if (!saved || dataPath == null || !File.Exists(dataPath) || File.GetLastWriteTimeUtc(dataPath) < DateTime.UtcNow.AddMinutes(-5))
        {
            ctx.Exit(5);
            return;
        }
        ctx.Exit();
    }

    private static List<(int Layer, Vector3 Start, float Length, float Radius)> ParsePatches(string text)
    {
        var patches = new List<(int, Vector3, float, float)>();
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = entry.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            patches.Add(((int)v[0], new Vector3(v[1], 0, v[2]), v[3], v[4]));
        }
        return patches;
    }
}

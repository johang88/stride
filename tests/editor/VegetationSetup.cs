// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the terrain grass and vegetation tools on a scene (STRIDE_VEGETATION_SCENE) whose terrain has grass layers
// and vegetation types: fills grass layers from terrain layers, places trees where a terrain layer is painted (strokes of the
// vegetation tool), checks the grass density, the instances, the levels of detail and the impostors, and saves.
// Layers by name: STRIDE_VEGETATION_FILL "grass layer=terrain layer:density;...", STRIDE_VEGETATION_FOREST "terrain layer"
// (where trees are placed, default "Forest floor").
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain;
using Stride.Terrain.Editing;
using Stride.Terrain.Editor;

namespace Stride.Editor.Tests;

[UITest]
public class VegetationSetup : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_VEGETATION_SCENE") ?? "MainScene";
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
        var tools = await session.Dispatcher.InvokeAsync(() => editor?.Tools.OfType<TerrainToolsViewModel>().Single());
        if (tools == null)
        {
            ctx.Exit(3);
            return;
        }
        await session.Dispatcher.InvokeAsync(() => tools.IsActive = true);
        var service = await session.Dispatcher.InvokeAsync(() => tools.Service);
        if (!await service.WaitForTarget(TimeSpan.FromMinutes(5)))
        {
            ctx.Exit(4);
            return;
        }
        // The panel lists the target's layers, grass layers and vegetation types
        for (int i = 0; i < 100 && await session.Dispatcher.InvokeAsync(() => tools.GrassLayers.Count == 0 || tools.VegetationTypes.Count == 0); i++)
            await Task.Delay(100);
        var terrainLayers = await session.Dispatcher.InvokeAsync(() => tools.Layers.Select(x => x.Name).ToList());
        var grassLayers = await session.Dispatcher.InvokeAsync(() => tools.GrassLayers.Select(x => x.Name).ToList());
        var types = await session.Dispatcher.InvokeAsync(() => tools.VegetationTypes.Select(x => x.Name).ToList());
        Console.WriteLine($"VEG terrain layers: {string.Join(", ", terrainLayers)}; grass: {string.Join(", ", grassLayers)}; trees: {string.Join(", ", types)}");
        if (grassLayers.Count == 0 || types.Count == 0)
        {
            ctx.Exit(5);
            return;
        }

        // Grass from the terrain layers
        var fill = Environment.GetEnvironmentVariable("STRIDE_VEGETATION_FILL")
            ?? "Grass=Grass:0.85;Clover=Grass:0.12;Ferns=Forest floor:0.6;Heather=Dirt:0.25;Ground foliage=Forest floor:0.7;Nettle=Mud:0.4";
        foreach (var entry in fill.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (grass, rest) = (entry.Split('=')[0], entry.Split('=')[1]);
            var (layer, density) = (rest.Split(':')[0], float.Parse(rest.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
            var grassIndex = grassLayers.IndexOf(grass);
            var layerIndex = terrainLayers.IndexOf(layer);
            if (grassIndex < 0 || layerIndex < 0)
            {
                Console.WriteLine($"VEG skipped fill {entry}");
                continue;
            }
            await service.FillGrassFromLayer(grassIndex, layerIndex, density);
        }

        // Trees where the forest layer is painted: one stroke per 30 m cell, a type picked at random
        var data = await service.GetTargetDataCopy();
        var origin = await service.GetTargetOrigin();
        var forest = terrainLayers.IndexOf(Environment.GetEnvironmentVariable("STRIDE_VEGETATION_FOREST") ?? "Forest floor");
        var random = new Random(1234);
        var strokes = 0;
        const float cell = 30.0f;
        for (float z = cell / 2; z < data.Size; z += cell)
        {
            for (float x = cell / 2; x < data.Size; x += cell)
            {
                var sx = Math.Clamp((int)(x / data.CellSize), 0, data.Resolution - 1);
                var sz = Math.Clamp((int)(z / data.CellSize), 0, data.Resolution - 1);
                if (forest < 0 || TerrainSplat.GetWeight(data.Splat[sz * data.Resolution + sx], forest) < 0.5f)
                    continue;
                var settings = TerrainBrushSettings.Default with
                {
                    Tool = TerrainBrushTool.Vegetation,
                    Radius = cell * 0.6f,
                    Strength = 1,
                    VegetationType = random.Next(types.Count),
                    VegetationDensity = 2.5f,
                    VegetationSpacing = 5,
                    VegetationMinScale = 0.8f,
                    VegetationMaxScale = 1.3f,
                };
                await service.SimulateStroke(origin + new Vector3(x, 0, z), settings, 20);
                strokes++;
            }
        }

        var result = await service.GetTargetDataCopy();
        var grassCounts = Enumerable.Range(0, grassLayers.Count).Select(i => result.GetGrassDensity(i)?.Count(v => v > 0) ?? 0).ToList();
        Console.WriteLine($"VEG grass samples per layer: {string.Join(", ", grassCounts)}");
        Console.WriteLine($"VEG {strokes} tree strokes, {result.Vegetation.Count} trees: {string.Join(", ", result.Vegetation.GroupBy(x => x.Type).OrderBy(x => x.Key).Select(x => $"{types[x.Key]} {x.Count()}"))}");

        // Look at a forest edge from above, with the impostors baked
        await session.Dispatcher.InvokeAsync(() => tools.Tool = TerrainBrushTool.Vegetation);
        await ctx.WaitForRendering(120, 60);
        var infos = await service.GetVegetationInfo();
        foreach (var info in infos)
            Console.WriteLine($"VEG type {info.Name}: {info.Instances} placed, LOD triangles {string.Join("/", info.LodTriangles)}, drawn {string.Join("/", info.LodInstances)}, impostor {(info.HasImpostor ? $"{info.ImpostorViews}x{info.ImpostorViews}x{info.ImpostorResolution}" : "none")}");
        await ctx.Screenshot("vegetation");
        await session.Dispatcher.InvokeAsync(() => tools.ImpostorPreview = true);
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("vegetation-impostors");
        await session.Dispatcher.InvokeAsync(() => tools.ImpostorPreview = false);

        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var ok = saved && grassCounts.Any(x => x > 0) && result.Vegetation.Count > 0 && infos.Count == types.Count && infos.All(x => x.LodTriangles.Length > 0);
        Console.WriteLine($"VEG {(ok ? "PASS" : "FAIL")} saved {saved}");
        ctx.Exit(ok ? 0 : 6);
    }
}

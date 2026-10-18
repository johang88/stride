// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio scripted edit of the fog painted on a terrain, through the terrain tools: adds the fog layers listed in
// STRIDE_FOG_LAYERS ("name,density,height,softness,noise;..." above the ground; missing ones only), paints them with the fog
// brush along meandering strokes listed in STRIDE_FOG_PATCHES ("layer,x,z,length,radius;..." in world meters), checks that a
// stroke is undone and redone, and saves. Scene: STRIDE_FOG_SCENE (default Area01_Hinterlands).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using Stride.Terrain.Fog;

namespace Stride.Editor.Tests;

[UITest]
public class TerrainFogPaint : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_FOG_SCENE") ?? "Area01_Hinterlands";
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
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.IsActive = true;
            tools.Tool = TerrainBrushTool.Fog;
        });
        if (!await service.WaitForTarget(TimeSpan.FromMinutes(5)))
        {
            ctx.Exit(4);
            return;
        }
        for (int i = 0; i < 100 && !await session.Dispatcher.InvokeAsync(() => tools.CanEditDefinitions); i++)
            await Task.Delay(100);

        // The fog layers, added through the tools panel (an undoable change of the scene)
        foreach (var layer in ParseLayers(Environment.GetEnvironmentVariable("STRIDE_FOG_LAYERS") ?? "Ground mist,0.08,3,2.5,0.6"))
        {
            var exists = await session.Dispatcher.InvokeAsync(() => tools.FogLayers.Any(x => x.Name == layer.Name));
            if (!exists && !await session.Dispatcher.InvokeAsync(() => tools.AddFogLayer(layer)))
            {
                Console.WriteLine($"FOGPAINT could not add the fog layer {layer.Name}");
                ctx.Exit(7);
                return;
            }
        }
        var layerCount = await session.Dispatcher.InvokeAsync(() => tools.FogLayers.Count);
        Console.WriteLine($"FOGPAINT fog layers: {layerCount}");

        var patches = ParsePatches(Environment.GetEnvironmentVariable("STRIDE_FOG_PATCHES") ?? "0,-30,30,30,10");
        var random = new Random(4321);
        foreach (var (layer, start, length, radius) in patches)
        {
            if (layer >= layerCount)
                continue;
            var settings = TerrainBrushSettings.Default with
            {
                Tool = TerrainBrushTool.Fog,
                FogLayer = layer,
                FogDensity = 1.0f,
                Radius = radius,
                Strength = 1.0f,
                Falloff = 0.9f,
            };

            var path = new List<Vector3>();
            var position = start;
            var heading = (float)(random.NextDouble() * Math.PI * 2);
            for (float travelled = 0; travelled <= length; travelled += radius * 0.4f)
            {
                path.Add(position);
                heading += (float)(random.NextDouble() - 0.5) * 0.9f;
                position += new Vector3(MathF.Cos(heading), 0, MathF.Sin(heading)) * radius * 0.4f;
            }
            if (!await service.SimulateStroke(path, settings, 12))
            {
                ctx.Exit(6);
                return;
            }
        }

        // The last stroke is undone and redone
        static float PaintedSum(TerrainData data, int count) => Enumerable.Range(0, count).Sum(i => data.GetFogDensity(i)?.Sum(x => (float)x) ?? 0);
        await ctx.WaitForRendering(10);
        var painted = PaintedSum(await service.GetTargetDataCopy(), layerCount);
        await session.Dispatcher.InvokeAsync(() => session.ActionHistory.UndoCommand.Execute());
        await ctx.WaitForRendering(10);
        var undone = PaintedSum(await service.GetTargetDataCopy(), layerCount);
        await session.Dispatcher.InvokeAsync(() => session.ActionHistory.RedoCommand.Execute());
        await ctx.WaitForRendering(10);
        var redone = PaintedSum(await service.GetTargetDataCopy(), layerCount);
        Console.WriteLine($"FOGPAINT painted {painted}, undone {undone}, redone {redone}");
        if (painted <= 0 || undone >= painted || Math.Abs(redone - painted) > 0.5f)
        {
            ctx.Exit(8);
            return;
        }

        await ctx.WaitForRendering(60);
        await ctx.Screenshot("fog_painted");

        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var terrainAsset = await session.Dispatcher.InvokeAsync(() => session.AllAssets.OfType<TerrainViewModel>().FirstOrDefault(x => x.Url.Contains(sceneName.Split('_')[0])));
        var dataPath = terrainAsset?.GetDataFilePath()?.ToOSPath();
        if (!saved || dataPath == null || !File.Exists(dataPath))
        {
            ctx.Exit(5);
            return;
        }
        var savedData = TerrainDataFile.Read(dataPath);
        var savedLayers = Enumerable.Range(0, TerrainData.MaxFogLayers).Count(i => savedData.GetFogDensity(i)?.Any(x => x != 0) == true);
        Console.WriteLine($"FOGPAINT saved {dataPath}: {savedLayers} painted fog layers");
        ctx.Exit(savedLayers > 0 ? 0 : 9);
    }

    private static List<TerrainFogLayer> ParseLayers(string text)
    {
        var layers = new List<TerrainFogLayer>();
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = entry.Split(',');
            float F(int i) => float.Parse(v[i], CultureInfo.InvariantCulture);
            layers.Add(new TerrainFogLayer { Name = v[0], Density = F(1), Height = F(2), Softness = F(3), NoiseStrength = F(4) });
        }
        return layers;
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

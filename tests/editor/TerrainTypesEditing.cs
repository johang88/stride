// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the grass layer and vegetation type editing of the terrain tools panel, on a scene (STRIDE_VEGETATION_SCENE)
// whose terrain has grass layers (one painted) and vegetation types (one placed) and a model asset named STRIDE_VEGETATION_MODEL
// (default Fir_02_Small): changes a setting and undoes it, adds a type through the asset picker, removes a placed type and a
// painted layer with their data (the later ones move down) and undoes that, places a mix of two selected types with a stroke
// and undoes it. Doesn't save. Logs "TYPES ..." lines.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Services;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain;
using Stride.Terrain.Editor;
using Stride.Terrain.Editing;
using Stride.Terrain.Vegetation;

namespace Stride.Editor.Tests;

[UITest]
public class TerrainTypesEditing : IUITest
{
    private readonly List<string> failures = new();

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

        var sceneUrl = await ctx.OpenAssetEditor(Environment.GetEnvironmentVariable("STRIDE_VEGETATION_SCENE") ?? "MainScene");
        if (sceneUrl == null)
        {
            ctx.Exit(2);
            return;
        }
        await ctx.WaitForAssetBuild();
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(120, 120);

        var session = SessionViewModel.Instance;
        var tools = await session.Dispatcher.InvokeAsync(() =>
        {
            var scene = session.AllAssets.First(a => a.Url == sceneUrl);
            session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var editor);
            return editor?.Tools.OfType<TerrainToolsViewModel>().Single();
        });
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
        for (int i = 0; i < 100 && await session.Dispatcher.InvokeAsync(() => tools.VegetationTypes.Count == 0 || tools.GrassLayers.Count == 0); i++)
            await Task.Delay(100);
        var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();

        var (grassCount, typeCount, canEdit) = await session.Dispatcher.InvokeAsync(() => (tools.GrassLayers.Count, tools.VegetationTypes.Count, tools.CanEditDefinitions));
        Console.WriteLine($"TYPES {grassCount} grass layers, {typeCount} vegetation types, editable {canEdit}");
        Check(canEdit && grassCount > 1 && typeCount > 1, "a terrain with editable grass layers and vegetation types");
        if (!canEdit)
        {
            Finish(ctx);
            return;
        }
        var (usage, painted, _) = await service.GetDefinitionUsage();
        Console.WriteLine($"TYPES instances per type {string.Join("/", usage)}, painted grass layers {string.Join("/", painted.Select(x => x ? 1 : 0))}");

        // A setting, undone
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.Tool = TerrainBrushTool.Vegetation;
            tools.SelectedVegetationType = tools.VegetationTypes[0];
        });
        var distance = await session.Dispatcher.InvokeAsync(() => tools.SelectedVegetationType.ImpostorDistance);
        await session.Dispatcher.InvokeAsync(() => tools.SelectedVegetationType.ImpostorDistance = distance + 33);
        var changed = await session.Dispatcher.InvokeAsync(() => ((TerrainVegetationType)tools.VegetationTypes[0].Definition).ImpostorDistance);
        await session.Dispatcher.InvokeAsync(() => undoRedo.Undo());
        await ctx.WaitDispatcherIdle();
        var undone = await session.Dispatcher.InvokeAsync(() => tools.VegetationTypes[0].ImpostorDistance);
        Console.WriteLine($"TYPES impostor distance {distance} -> {changed}, undone {undone}");
        Check(changed == distance + 33 && undone == distance, "a setting changes the asset and undoes");

        // A type added through the picker: the editor game draws it
        var modelName = Environment.GetEnvironmentVariable("STRIDE_VEGETATION_MODEL") ?? "Fir_02_Small";
        var pickerResponse = ctx.QueueAssetPickerResponse(modelName);
        await session.Dispatcher.InvokeAsync(() => tools.AddVegetationTypesCommand.Execute(null));
        await pickerResponse;
        await ctx.WaitDispatcherIdle();
        var added = await session.Dispatcher.InvokeAsync(() => tools.VegetationTypes.Count);
        var addedName = await session.Dispatcher.InvokeAsync(() => tools.SelectedVegetationType?.Name);
        var gameTypes = 0;
        for (int i = 0; i < 100 && gameTypes != added; i++)
        {
            await Task.Delay(200);
            gameTypes = (await service.GetVegetationInfo()).Count;
        }
        Console.WriteLine($"TYPES added {addedName}: {added} types, {gameTypes} drawn by the editor game");
        Check(added == typeCount + 1 && gameTypes == added, "a type added through the picker reaches the editor game");

        // The first placed type removed with its instances (the later ones renumbered), then restored
        var removedType = Array.FindIndex(usage, x => x > 0);
        var lastType = usage.Length - 1;
        await session.Dispatcher.InvokeAsync(() => tools.SelectedVegetationType = tools.VegetationTypes[removedType]);
        var removed = await session.Dispatcher.InvokeTask(() => tools.RemoveDefinition(TerrainDefinitionKind.Vegetation, confirm: false));
        var afterRemove = await WaitForUsage(service, x => x.Instances.Length == typeCount && x.Instances.Sum() == usage.Sum() - usage[removedType]);
        Console.WriteLine($"TYPES removed type {removedType}: {removed}, instances per type {string.Join("/", afterRemove.Instances)}");
        Check(removed && afterRemove.Instances.Sum() == usage.Sum() - usage[removedType]
              && (removedType == lastType || afterRemove.Instances[lastType - 1] == usage[lastType]), "removing a type removes its instances and renumbers the later ones");
        await session.Dispatcher.InvokeAsync(() => undoRedo.Undo());
        var afterUndo = await WaitForUsage(service, x => x.Instances.Length == typeCount + 1);
        Console.WriteLine($"TYPES undone: instances per type {string.Join("/", afterUndo.Instances)}");
        Check(afterUndo.Instances.Take(usage.Length).SequenceEqual(usage), "undoing the removal restores the type and its instances");
        await session.Dispatcher.InvokeAsync(() => undoRedo.Undo());
        await ctx.WaitDispatcherIdle();
        var back = await session.Dispatcher.InvokeAsync(() => tools.VegetationTypes.Count);
        Check(back == typeCount, "undoing the addition removes the type");

        // Two types selected in the list: a stroke places a mix of them
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.SelectedVegetationTypeItems.Clear();
            tools.SelectedVegetationTypeItems.Add(tools.VegetationTypes[0]);
            tools.SelectedVegetationTypeItems.Add(tools.VegetationTypes[1]);
        });
        await ctx.WaitDispatcherIdle();
        var brush = service.Brush;
        Console.WriteLine($"TYPES brush types {string.Join(",", brush.VegetationTypes ?? [])}");
        Check(brush.VegetationTypes is [0, 1], "selecting two types makes the brush place both");
        var data = await service.GetTargetDataCopy();
        var origin = await service.GetTargetOrigin();
        var stroke = brush with { Radius = 20, Strength = 1, Falloff = 0.3f, VegetationDensity = 3, VegetationSpacing = 3, VegetationMaxSlope = 60, Mask = null };
        await service.SimulateStroke(origin + new Stride.Core.Mathematics.Vector3(data.Size * 0.5f, 0, data.Size * 0.5f), stroke, 20);
        var mixed = await WaitForUsage(service, x => x.Instances[0] > usage[0] && x.Instances[1] > usage[1]);
        Console.WriteLine($"TYPES mix stroke: instances per type {string.Join("/", mixed.Instances)}");
        Check(mixed.Instances[0] > usage[0] && mixed.Instances[1] > usage[1] && mixed.Instances.Skip(2).SequenceEqual(usage.Skip(2)), "a stroke places a mix of the selected types only");
        await ctx.WaitDispatcherIdle();
        await session.Dispatcher.InvokeAsync(() => undoRedo.Undo());
        var mixUndone = await WaitForUsage(service, x => x.Instances.SequenceEqual(usage));
        Check(mixUndone.Instances.SequenceEqual(usage), "undoing the stroke removes the mix");
        await session.Dispatcher.InvokeAsync(() => tools.SelectedVegetationTypeItems.Clear());

        // A painted grass layer removed with its density (the later ones move down), then restored
        var removedLayer = Array.FindIndex(painted, x => x);
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.Tool = TerrainBrushTool.Grass;
            tools.SelectedGrassLayer = tools.GrassLayers[removedLayer];
        });
        var removedGrass = await session.Dispatcher.InvokeTask(() => tools.RemoveDefinition(TerrainDefinitionKind.Grass, confirm: false));
        var expected = painted.Where((_, i) => i != removedLayer).Append(false).ToArray();
        var grassAfter = await WaitForUsage(service, x => x.GrassPainted.SequenceEqual(expected));
        Console.WriteLine($"TYPES removed grass layer {removedLayer}: {removedGrass}, painted {string.Join("/", grassAfter.GrassPainted.Select(x => x ? 1 : 0))}");
        Check(removedGrass && grassAfter.GrassPainted.SequenceEqual(expected), "removing a grass layer moves the later layers' density down");
        await ctx.Screenshot("terrain-types-grass");
        await session.Dispatcher.InvokeAsync(() => undoRedo.Undo());
        var grassUndone = await WaitForUsage(service, x => x.GrassPainted.SequenceEqual(painted));
        var layersBack = await session.Dispatcher.InvokeAsync(() => tools.GrassLayers.Count);
        Check(grassUndone.GrassPainted.SequenceEqual(painted) && layersBack == grassCount, "undoing the grass layer removal restores it and its density");

        await session.Dispatcher.InvokeAsync(() => tools.Tool = TerrainBrushTool.Vegetation);
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("terrain-types-vegetation");
        Finish(ctx);
    }

    private static async Task<(int[] Instances, bool[] GrassPainted, bool[] FogPainted)> WaitForUsage(EditorGameTerrainService service, Func<(int[] Instances, bool[] GrassPainted, bool[] FogPainted), bool> condition)
    {
        var usage = await service.GetDefinitionUsage();
        for (int i = 0; i < 100 && !condition(usage); i++)
        {
            await Task.Delay(100);
            usage = await service.GetDefinitionUsage();
        }
        return usage;
    }

    private void Check(bool ok, string what)
    {
        if (!ok)
            failures.Add(what);
        Console.WriteLine($"TYPES {(ok ? "ok" : "FAILED")}: {what}");
    }

    private void Finish(IUITestContext ctx)
    {
        Console.WriteLine(failures.Count == 0 ? "TYPES PASS" : $"TYPES FAIL ({failures.Count})");
        ctx.Exit(failures.Count == 0 ? 0 : 6);
    }
}

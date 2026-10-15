// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the terrain decals on a scene (STRIDE_DECAL_TEST_SCENE) with a terrain, a decal entity named
// "Decal mud puddle" and a spline baking a decal: the decal component is offered under Terrain, the decals reach the editor game,
// and moving, editing and adding decals (undoable changes, reverted) re-render them. Nothing is saved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.TerrainEditor;
using Stride.Core;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Services;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain.Decals;
using Stride.Terrain.Splines;

namespace Stride.Editor.Tests;

[UITest]
public class TerrainDecals : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_DECAL_TEST_SCENE") ?? "MainScene";
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

        // Offered in the add component menu, under Terrain
        var offered = typeof(EntityComponent).GetInheritedInstantiableTypes().Contains(typeof(TerrainDecalComponent));
        Check(offered && ComponentCategoryAttribute.GetCategory(typeof(TerrainDecalComponent)) == "Terrain", "decal component offered under Terrain");

        var entities = await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.SelectMany(Flatten).ToList());
        var decalEntities = entities.Count(x => x.AssetSideEntity.Get<TerrainDecalComponent>() != null);
        var bakingSplines = entities.Count(x => x.AssetSideEntity.Get<TerrainSplineComponent>()?.BakeDecal == true);
        Check(decalEntities > 0 && bakingSplines > 0, $"{decalEntities} decal entities, {bakingSplines} splines baking a decal");

        // The editor game has the decals of the scene
        var tools = await session.Dispatcher.InvokeAsync(() => editor.TerrainTools);
        for (int i = 0; i < 100 && !await session.Dispatcher.InvokeAsync(() => tools.IsAvailable); i++)
            await Task.Delay(100);
        var service = await session.Dispatcher.InvokeAsync(() => tools.Service);
        var expected = decalEntities + bakingSplines;
        var gameDecals = await WaitForDecalCount(service, expected);
        Check(gameDecals == expected, $"editor game decals: {gameDecals} (expected {expected})");

        var puddle = entities.FirstOrDefault(x => x.Name == "Decal mud puddle");
        if (puddle == null)
        {
            Check(false, "decal entity 'Decal mud puddle' found");
            Finish(ctx);
            return;
        }
        await session.Dispatcher.InvokeAsync(() =>
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(puddle);
            puddle.FocusOnEntityCommand.Execute();
        });
        await ctx.WaitForRendering(90);
        await ctx.Screenshot("selected");

        // Move it (undoable): the game side decal follows
        var boundsBefore = (await service.GetDecals()).Select(x => x.Min).ToList();
        await session.Dispatcher.InvokeAsync(() =>
        {
            var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();
            using var transaction = undoRedo.CreateTransaction();
            var node = session.AssetNodeContainer.GetNode(puddle.AssetSideEntity.Transform)[nameof(TransformComponent.Position)];
            node.Update((Vector3)node.Retrieve() + new Vector3(3, 0, 0));
            undoRedo.SetName(transaction, "Move decal");
        });
        var moved = false;
        for (int i = 0; i < 50 && !moved; i++)
        {
            await Task.Delay(100);
            moved = (await service.GetDecals()).Any(x => boundsBefore.All(b => Vector2.Distance(b, x.Min) > 1.0f));
        }
        Check(moved, "moved decal updated in the editor game");
        await ctx.WaitForRendering(60);
        await ctx.Screenshot("moved");

        // Edit the surface through the property graph
        await session.Dispatcher.InvokeAsync(() =>
        {
            var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();
            using var transaction = undoRedo.CreateTransaction();
            var surface = puddle.AssetSideEntity.Get<TerrainDecalComponent>().Surface;
            session.AssetNodeContainer.GetNode(surface)[nameof(TerrainDecalSurface.Tint)].Update(new Color4(1, 0.2f, 0.2f, 1));
            undoRedo.SetName(transaction, "Tint decal");
        });
        var tinted = false;
        for (int i = 0; i < 50 && !tinted; i++)
        {
            await Task.Delay(100);
            tinted = (await service.GetDecals()).Any(x => x.Tint.G < 0.5f);
        }
        Check(tinted, "decal surface edit reached the editor game");
        await ctx.WaitForRendering(60);
        await ctx.Screenshot("tinted");

        // Add a decal component to another entity
        var host = entities.FirstOrDefault(x => x.AssetSideEntity.Get<TerrainDecalComponent>() == null && x.AssetSideEntity.Components.Count == 1 && x.AssetSideEntity.Transform.Children.Count == 0 && x != puddle);
        if (host != null)
        {
            await session.Dispatcher.InvokeAsync(() =>
            {
                var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();
                using var transaction = undoRedo.CreateTransaction();
                var components = session.AssetNodeContainer.GetNode(host.AssetSideEntity)[nameof(Entity.Components)].Target;
                components.Add(new TerrainDecalComponent { Size = new Vector2(3, 3) });
                undoRedo.SetName(transaction, "Add decal");
            });
            var added = await WaitForDecalCount(service, expected + 1);
            Check(added == expected + 1, $"added decal component reached the editor game ({added} decals)");
            await session.Dispatcher.InvokeAsync(() =>
            {
                editor.ClearSelection();
                editor.SelectedContent.Add(host);
                host.FocusOnEntityCommand.Execute();
            });
            await ctx.WaitForRendering(90);
            await ctx.Screenshot("added");
        }
        else
        {
            Check(false, "entity to add a decal to");
        }

        // Revert everything
        await session.Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < (host != null ? 3 : 2); i++)
                session.ActionHistory.UndoCommand.Execute();
        });
        var reverted = await WaitForDecalCount(service, expected);
        Check(reverted == expected, $"undo restored the decals ({reverted})");
        Finish(ctx);
    }

    private void Finish(IUITestContext ctx)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "terrain-decals-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    private static async Task<int> WaitForDecalCount(EditorGameTerrainService service, int expected)
    {
        if (service == null)
            return -1;
        var count = -1;
        for (int i = 0; i < 100 && count != expected; i++)
        {
            if (i > 0)
                await Task.Delay(100);
            count = (await service.GetDecals()).Count;
        }
        return count;
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

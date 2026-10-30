// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the projected decals (Stride.Decals) on a scene (STRIDE_DECAL_EDIT_SCENE): the decal component is offered
// under Rendering, a decal added to an entity reaches the editor game and its decal renderer, edits of its surface follow,
// the selected decal shows its gizmo (screenshots), and undo removes it. Nothing is saved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Extensions;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Services;
using Stride.Decals;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class DecalEditing : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_DECAL_EDIT_SCENE") ?? "MainScene";
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
        // (protected internal: the editor game's dispatcher and its parts)
        var controller = (IEditorGameController)typeof(SceneEditorViewModel).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(x => x.Name == "Controller" && x.DeclaringType == typeof(SceneEditorViewModel)).GetValue(editor);

        // Offered in the add component menu, under Rendering
        var offered = typeof(EntityComponent).GetInheritedInstantiableTypes().Contains(typeof(DecalComponent));
        Check(offered && ComponentCategoryAttribute.GetCategory(typeof(DecalComponent)) == "Rendering", "decal component offered under Rendering");

        // A prop (a model, no children) gets a decal: it projects onto the prop and the ground around it
        var entities = await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.SelectMany(Flatten).ToList());
        var host = entities.FirstOrDefault(x => x.AssetSideEntity.Get<ModelComponent>() != null && x.AssetSideEntity.Get<DecalComponent>() == null
            && x.AssetSideEntity.Transform.Children.Count == 0);
        if (host == null)
        {
            Check(false, "entity to add a decal to");
            Finish(ctx);
            return;
        }
        var before = await ActiveDecals(controller, host);
        await session.Dispatcher.InvokeAsync(() =>
        {
            var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();
            using var transaction = undoRedo.CreateTransaction();
            var components = session.AssetNodeContainer.GetNode(host.AssetSideEntity)[nameof(Entity.Components)].Target;
            components.Add(new DecalComponent { Size = new Vector3(6, 6, 6), Surface = { Color = new Color4(1.0f, 0.2f, 0.9f, 1.0f), EmissiveIntensity = 2.0f } });
            undoRedo.SetName(transaction, "Add decal");
        });
        var added = await WaitForActiveDecals(controller, host, before + 1);
        Check(added == before + 1, $"added decal reached the editor game ({before} -> {added} active decals)");

        await session.Dispatcher.InvokeAsync(() =>
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(host);
            host.FocusOnEntityCommand.Execute();
        });
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(120);
        await ctx.Screenshot("decal-selected");

        // Edit the surface through the property graph: the game side follows
        await session.Dispatcher.InvokeAsync(() =>
        {
            var undoRedo = session.ServiceProvider.Get<IUndoRedoService>();
            using var transaction = undoRedo.CreateTransaction();
            var surface = host.AssetSideEntity.Get<DecalComponent>().Surface;
            session.AssetNodeContainer.GetNode(surface)[nameof(DecalSurface.Color)].Update(new Color4(0.1f, 0.9f, 1.0f, 1.0f));
            undoRedo.SetName(transaction, "Tint decal");
        });
        var tinted = false;
        for (int i = 0; i < 50 && !tinted; i++)
        {
            await Task.Delay(100);
            tinted = await controller.InvokeAsync(() => (controller.FindGameSidePart(host.Id) as Entity)?.Get<DecalComponent>()?.Surface.Color.R < 0.5f);
        }
        Check(tinted, "decal surface edit reached the editor game");
        await ctx.WaitForRendering(60);
        await ctx.Screenshot("decal-tinted");

        // Undo both: the decal is gone
        await session.Dispatcher.InvokeAsync(() =>
        {
            session.ActionHistory.UndoCommand.Execute();
            session.ActionHistory.UndoCommand.Execute();
        });
        var reverted = await WaitForActiveDecals(controller, host, before);
        Check(reverted == before, $"undo removed the decal ({reverted} active decals)");
        Finish(ctx);
    }

    private void Finish(IUITestContext ctx)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "decal-editing-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    // The decals the editor game's scene hands to its decal renderer
    private static Task<int> ActiveDecals(IEditorGameController controller, EntityViewModel anyEntity)
    {
        return controller.InvokeAsync(() =>
        {
            var gameEntity = controller.FindGameSidePart(anyEntity.Id) as Entity;
            if (gameEntity?.EntityManager is not SceneInstance sceneInstance)
                return -1;
            return sceneInstance.VisibilityGroups.Sum(x => x.Tags.Get(DecalRenderer.CurrentDecals)?.Count ?? 0);
        });
    }

    private static async Task<int> WaitForActiveDecals(IEditorGameController controller, EntityViewModel anyEntity, int expected)
    {
        var count = -1;
        for (int i = 0; i < 100 && count != expected; i++)
        {
            if (i > 0)
                await Task.Delay(100);
            count = await ActiveDecals(controller, anyEntity);
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

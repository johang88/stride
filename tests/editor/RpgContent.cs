// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of a project defining its own asset types and components (pass the project .slnx on
// the AutoTesting command line; written against the MorningWood RPG sample): the game assembly's asset
// types load, its templates are listed and create assets, an area scene with prefab instances opens and
// renders, and selecting entities/assets shows their custom properties. Nothing is saved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Assets.Templates;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class RpgContent : IUITest
{
    private static readonly string[] GameDataTypes = ["ItemAsset", "AbilityAsset", "StatusEffectAsset", "CharacterAsset", "QuestAsset", "DialogueAsset"];

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
        var session = SessionViewModel.Instance;

        // Asset types and templates from the game assembly
        var counts = await session.Dispatcher.InvokeAsync(() => GameDataTypes.ToDictionary(x => x, x => session.AllAssets.Count(a => a.Asset.GetType().Name == x)));
        foreach (var (type, count) in counts)
            Check(count > 0, $"{count} {type} assets loaded");
        var templates = await session.Dispatcher.InvokeAsync(() => session.FindTemplates(TemplateScope.Asset).OfType<TemplateAssetDescription>().Where(x => x.Group == "RPG").ToList());
        Check(templates.Count == GameDataTypes.Length, $"RPG templates listed: {string.Join(", ", templates.Select(x => x.Name))}");

        // Terrain content made with the fork's terrain module
        var blendLayer = await session.Dispatcher.InvokeAsync(() =>
        {
            var rock = session.AllAssets.FirstOrDefault(x => x.Url.EndsWith("Materials/Props/RockTerrainBlend", StringComparison.OrdinalIgnoreCase))?.Asset as Stride.Assets.Materials.MaterialAsset;
            return rock?.Layers.FirstOrDefault()?.BlendMap?.GetType().Name;
        });
        Check(blendLayer == "ComputeTerrainBlendMask", $"terrain blend material layer loaded ({blendLayer})");
        var cliff = await session.Dispatcher.InvokeAsync(() => (session.AllAssets.FirstOrDefault(x => x.Asset is Stride.Assets.Terrain.TerrainMaterialAsset)?.Asset as Stride.Assets.Terrain.TerrainMaterialAsset)?.CliffLayer);
        Check(cliff == 2, $"terrain material cliff layer ({cliff})");

        // Create an item and a dialogue from their templates (not saved)
        foreach (var name in new[] { "Item", "Dialogue" })
        {
            var template = templates.FirstOrDefault(x => x.Name == name);
            var id = template != null ? await ctx.AddAssetFromTemplate(template.Id, template.Name) : Guid.Empty;
            var created = id != Guid.Empty ? await session.Dispatcher.InvokeAsync(() => session.GetAssetById(new Stride.Core.Assets.AssetId(id))) : null;
            Check(created != null, $"created a {name} from its template ({created?.Url})");
        }

        // Character asset in the property grid
        await session.Dispatcher.InvokeAsync(() =>
        {
            var character = session.AllAssets.FirstOrDefault(x => x.Url.EndsWith("GameData/Characters/Evelyn", StringComparison.OrdinalIgnoreCase));
            if (character != null)
            {
                session.ActiveAssetView.SelectAssets(new[] { character });
            }
        });
        await ctx.WaitForRendering(20);
        await ctx.CapturePanel("PropertyGrid", "character-properties", 700, 1000);
        var dialogue = await session.Dispatcher.InvokeAsync(() => session.AllAssets.FirstOrDefault(x => x.Url.EndsWith("GameData/Dialogues/CaptainHale", StringComparison.OrdinalIgnoreCase)));
        await session.Dispatcher.InvokeAsync(() => session.ActiveAssetView.SelectAssets(new[] { dialogue }));
        await ctx.WaitForRendering(20);
        await ctx.CapturePanel("PropertyGrid", "dialogue-properties", 700, 1000);

        // The area scene: prefab instances, custom components, terrain
        var sceneUrl = await ctx.OpenAssetEditor("Areas/Area01_Hinterlands");
        Check(sceneUrl != null, "area scene opened");
        if (sceneUrl != null)
        {
            await ctx.WaitForAssetBuild();
            await ctx.WaitForShaders();
            await ctx.WaitForRendering(120, 120);
            var selected = await session.Dispatcher.InvokeAsync(() =>
            {
                var scene = session.AllAssets.First(a => a.Url == sceneUrl);
                if (!session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var editor))
                    return null;
                var entities = editor.HierarchyRoot.Children.SelectMany(Flatten).ToList();
                report.Add($"INFO scene entities: {entities.Count}");
                var hale = entities.FirstOrDefault(x => x.Name == "Captain Hale");
                if (hale != null)
                {
                    editor.SelectedContent.Clear();
                    editor.SelectedContent.Add(hale);
                }
                return hale;
            });
            Check(selected != null, "Captain Hale entity found in the scene");
            var splines = await session.Dispatcher.InvokeAsync(() =>
            {
                var scene = session.AllAssets.First(a => a.Url == sceneUrl);
                session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var editor);
                return editor?.HierarchyRoot.Children.SelectMany(Flatten).Count(x => x.AssetSideEntity.Get<Stride.Terrain.Splines.TerrainSplineComponent>() != null) ?? 0;
            });
            Check(splines == 2, $"terrain splines in the scene ({splines})");
            await ctx.WaitForRendering(30);
            await ctx.CapturePanel(sceneUrl, "area-scene", 1400, 900);
            await ctx.CapturePanel("PropertyGrid", "actor-properties", 700, 1000);
        }

        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "rpg-content-report.txt"), report);
        await ctx.Screenshot("final");
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

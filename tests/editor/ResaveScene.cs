// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio round trip of a scene (STRIDE_RESAVE_SCENE): loads it, makes it dirty with an edit that is reverted
// (renames its first root entity and back) and saves it, to check what the editor writes back (override markers,
// prefab instance reconciliation).
using System;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class ResaveScene : IUITest
{
    public async Task Run(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 180);
        if (opened != GameStudioWindowNames.GameStudio)
        {
            ctx.Exit(1);
            return;
        }
        await ctx.WaitIdle();

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_RESAVE_SCENE") ?? "MainScene";
        var sceneUrl = await ctx.OpenAssetEditor(sceneName);
        if (sceneUrl == null)
        {
            ctx.Exit(2);
            return;
        }
        await ctx.WaitForAssetBuild();

        var session = SessionViewModel.Instance;
        var editor = await session.Dispatcher.InvokeAsync(() =>
        {
            var scene = session.AllAssets.First(a => a.Url == sceneUrl);
            session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var sceneEditor);
            return sceneEditor;
        });
        var entity = editor != null ? await session.Dispatcher.InvokeAsync(() => editor.HierarchyRoot.Children.OfType<EntityViewModel>().FirstOrDefault()) : null;
        if (entity == null)
        {
            ctx.Exit(3);
            return;
        }

        await session.Dispatcher.InvokeAsync(() =>
        {
            var name = entity.Name;
            entity.Name = name + " (renamed)";
            entity.Name = name;
        });
        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        ctx.Exit(saved ? 0 : 4);
    }
}

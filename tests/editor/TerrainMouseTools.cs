// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the terrain tools driven by the real mouse (needs an unlocked desktop): activates the tools
// on the scene STRIDE_TERRAIN_TEST_SCENE, drags a raise stroke then a paint stroke across the viewport with OS mouse
// input and checks that the terrain samples changed. Nothing is saved.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Controls;
using Stride.GameStudio.AutoTesting;
using Stride.Terrain.Editing;

namespace Stride.Editor.Tests;

[UITest]
public class TerrainMouseTools : IUITest
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

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_TERRAIN_TEST_SCENE") ?? "MainScene";
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
        var service = await session.Dispatcher.InvokeAsync(() => tools.Service);
        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.IsActive = true;
            tools.Tool = TerrainBrushTool.Raise;
            tools.Radius = 12;
            tools.Strength = 1;
        });
        if (!await service.WaitForTarget(TimeSpan.FromMinutes(5)))
        {
            ctx.Exit(4);
            return;
        }
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("before");

        // The viewport in screen pixels
        var rect = await session.Dispatcher.InvokeAsync(() =>
        {
            var window = Application.Current.MainWindow;
            var host = FindVisual<GameEngineHost>(window);
            if (host == null || host.ActualWidth < 10)
                return (Rect?)null;
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            var topLeft = host.PointToScreen(new Point(0, 0));
            var bottomRight = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));
            return new Rect(topLeft, bottomRight);
        });
        if (rect == null)
        {
            ctx.Exit(5);
            return;
        }
        Console.WriteLine($"Viewport {rect}");

        var start = await service.GetTargetDataCopy();
        // Raise: drag across the lower middle of the view, where the terrain is
        await Drag(rect.Value, 0.35, 0.65, 0.65, 0.7);
        await ctx.WaitForRendering(10);
        var raised = await service.GetTargetDataCopy();
        var heightChanges = start.Heights.Zip(raised.Heights).Count(x => x.First != x.Second);

        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.Tool = TerrainBrushTool.Paint;
            tools.SelectedLayer = tools.Layers.FirstOrDefault(x => x.Index == 7) ?? tools.Layers.Last();
        });
        await Task.Delay(300);
        await Drag(rect.Value, 0.3, 0.6, 0.7, 0.6);
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("after");
        var painted = await service.GetTargetDataCopy();
        var splatChanges = raised.Splat.Zip(painted.Splat).Count(x => !x.First.Equals(x.Second));

        Console.WriteLine($"TERRAIN MOUSE heights changed: {heightChanges}, splat changed: {splatChanges}");
        ctx.Exit(heightChanges > 0 && splatChanges > 0 ? 0 : 6);
    }

    private static async Task Drag(Rect rect, double x0, double y0, double x1, double y1)
    {
        static (int X, int Y) At(Rect r, double x, double y) => ((int)(r.Left + r.Width * x), (int)(r.Top + r.Height * y));
        var (sx, sy) = At(rect, x0, y0);
        SetCursorPos(sx, sy);
        await Task.Delay(300);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        const int steps = 60;
        for (int i = 1; i <= steps; i++)
        {
            var (x, y) = At(rect, x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
            SetCursorPos(x, y);
            await Task.Delay(25);
        }
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        await Task.Delay(300);
    }

    private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (child is not FrameworkElement element || element.IsVisible))
                return found;
            var result = FindVisual<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}

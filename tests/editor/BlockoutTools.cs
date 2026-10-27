// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the level blockout tools on the scene STRIDE_BLOCKOUT_TEST_SCENE (run it on a scratch copy of the
// project: it saves). Draws a box with mouse messages posted to the viewport (the user's cursor doesn't move), resizes it by
// dragging a handle, draws more shapes through the service (on top of the box: its grid), applies presets, patterns and
// colors, edits a preset, checks undo/redo and the saved scene. Report: blockout-tools-report.txt beside the test runner.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.SceneEditor.ViewModels;
using Stride.BepuPhysics;
using Stride.Blockout;
using Stride.Blockout.Editor;
using Stride.Blockout.Physics;
using Stride.Blockout.Rendering;
using Stride.Blockout.Shapes;
using Stride.Core;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Controls;
using Stride.GameStudio.AutoTesting;
using Point = System.Windows.Point;

namespace Stride.Editor.Tests;

[UITest]
public class BlockoutTools : IUITest
{
    private readonly List<string> report = new();
    private int failures;

    private void Check(bool condition, string what)
    {
        if (!condition)
            failures++;
        report.Add($"{(condition ? "PASS" : "FAIL")} {what}");
        Console.WriteLine($"BLOCKOUT {(condition ? "PASS" : "FAIL")} {what}");
    }

    public async Task Run(IUITestContext ctx)
    {
        try
        {
            await RunChecks(ctx);
        }
        catch (Exception e)
        {
            Check(false, $"exception {e}");
        }
        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "blockout-tools-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    private async Task RunChecks(IUITestContext ctx)
    {
        var opened = await ctx.WaitForAnyWindow(new[] { GameStudioWindowNames.GameStudio, GameStudioWindowNames.ProjectSelection }, timeoutSeconds: 180);
        Check(opened == GameStudioWindowNames.GameStudio, "Game Studio opened");
        if (opened != GameStudioWindowNames.GameStudio)
            return;
        await ctx.SetWindowSize(GameStudioWindowNames.GameStudio, 1920, 1080);
        await ctx.WaitIdle();

        // Only the tested scene renders (Game Studio reopens the editors of the last session)
        var session = SessionViewModel.Instance;
        await session.Dispatcher.InvokeAsync(() => session.ServiceProvider.Get<IAssetEditorsManager>().CloseAllEditorWindows(false));

        var sceneName = Environment.GetEnvironmentVariable("STRIDE_BLOCKOUT_TEST_SCENE") ?? "MainScene";
        var sceneUrl = await ctx.OpenAssetEditor(sceneName);
        Check(sceneUrl != null, $"scene {sceneName} opened");
        if (sceneUrl == null)
            return;
        await ctx.WaitForAssetBuild();
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(120, 120);

        var editor = await session.Dispatcher.InvokeAsync(() =>
        {
            var scene = session.AllAssets.First(a => a.Url == sceneUrl);
            session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<SceneEditorViewModel>(scene, out var sceneEditor);
            return sceneEditor;
        });
        var tools = await session.Dispatcher.InvokeAsync(() => editor?.Tools.OfType<BlockoutToolsViewModel>().SingleOrDefault());
        Check(tools != null, "blockout tool in the scene editor");
        if (tools == null)
            return;
        for (int i = 0; i < 100 && !await session.Dispatcher.InvokeAsync(() => tools.IsAvailable); i++)
            await Task.Delay(100);
        var service = await session.Dispatcher.InvokeAsync(() => tools.Service);
        Check(service != null, "blockout editor game service");
        if (service == null)
            return;

        await session.Dispatcher.InvokeAsync(() =>
        {
            tools.IsActive = true;
            tools.GridSize = 0.5f;
            tools.Snap = true;
            tools.SelectedShape = tools.Shapes.First(x => x.Name == "Box");
            tools.ApplyPreset(tools.Presets.First(x => x.Name == "Wall"));
            editor.ClearSelection();
        });
        await ctx.WaitForRendering(30);
        await ctx.Screenshot("panel");
        Check(await session.Dispatcher.InvokeAsync(() => tools.Presets.Count) >= 10, "default presets loaded");

        int ShapeCount() => Flatten(editor.HierarchyRoot).Count(x => x.AssetSideEntity.Get<BlockoutComponent>() != null);
        var before = await session.Dispatcher.InvokeAsync(ShapeCount);

        // Draw a box with the mouse: drag the footprint, release, move up for the height, click
        var view = await session.Dispatcher.InvokeAsync(() =>
        {
            var host = FindVisual<GameEngineHost>(Application.Current.MainWindow);
            return host == null ? IntPtr.Zero : host.Handle;
        });
        Check(view != IntPtr.Zero, "viewport window");
        GetClientRect(view, out var client);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        Move(view, width, height, 0.45, 0.62);
        await Frames(service, 3);
        await ctx.Screenshot("hover-grid");
        Post(view, WM_LBUTTONDOWN, MK_LBUTTON, width, height, 0.45, 0.62);
        await Frames(service, 2);
        for (int i = 1; i <= 8; i++)
        {
            Post(view, WM_MOUSEMOVE, MK_LBUTTON, width, height, 0.45 + 0.1 * i / 8, 0.62 + 0.06 * i / 8);
            await Frames(service, 1);
        }
        await Frames(service, 2);
        await ctx.Screenshot("drawing-footprint");
        Post(view, WM_LBUTTONUP, 0, width, height, 0.55, 0.68);
        await Frames(service, 2);
        for (int i = 1; i <= 6; i++)
        {
            Move(view, width, height, 0.55, 0.68 - 0.08 * i / 6);
            await Frames(service, 1);
        }
        await Frames(service, 2);
        await ctx.Screenshot("drawing-height");
        Post(view, WM_LBUTTONDOWN, MK_LBUTTON, width, height, 0.55, 0.6);
        await Frames(service, 2);
        Post(view, WM_LBUTTONUP, 0, width, height, 0.55, 0.6);
        await Frames(service, 3);
        await ctx.WaitIdle();

        var afterDraw = await session.Dispatcher.InvokeAsync(ShapeCount);
        Check(afterDraw == before + 1, $"a box drawn with the mouse ({afterDraw - before} new shapes)");
        var box = await session.Dispatcher.InvokeAsync(() => editor.SelectedContent.OfType<EntityViewModel>().LastOrDefault(x => x.AssetSideEntity.Get<BlockoutComponent>() != null));
        Check(box != null, "the new box is selected");
        if (box == null)
            return;
        var boxInfo = await session.Dispatcher.InvokeAsync(() =>
        {
            var entity = box.AssetSideEntity;
            var component = entity.Get<BlockoutComponent>();
            return (component.Shape, component.Size, component.Style.Preset, Collider: entity.Get<StaticComponent>()?.Collider, entity.Transform.Position, Folder: editor.Asset.Asset.Hierarchy.Parts[entity.Id].Folder);
        });
        Check(boxInfo.Shape is BlockoutBox && boxInfo.Collider is BlockoutCollider && boxInfo.Folder == BlockoutToolsViewModel.FolderName && boxInfo.Preset == "Wall",
            $"box with a blockout collider in the Blockout folder, style {boxInfo.Preset} (size {boxInfo.Size}, at {boxInfo.Position})");
        var onGrid = IsOnGrid(boxInfo.Size.X, 0.5f) && IsOnGrid(boxInfo.Size.Y, 0.5f) && IsOnGrid(boxInfo.Size.Z, 0.5f);
        Check(onGrid && boxInfo.Size.X >= 0.5f && boxInfo.Size.Y >= 0.5f, $"box size snapped to the 0.5 m grid ({boxInfo.Size})");
        await ctx.Screenshot("drawn-box");

        // Resize with the right handle: drag it outward (away from the left handle on screen) by 1/10th of the view
        var handle = await service.GetHandleViewportPosition(BlockoutHandle.Right);
        var opposite = await service.GetHandleViewportPosition(BlockoutHandle.Left);
        Check(handle.HasValue && opposite.HasValue, $"right handle at {handle}");
        if (handle.HasValue && opposite.HasValue)
        {
            var (hx, hy) = (handle.Value.X, handle.Value.Y);
            var outward = Vector2.Normalize(handle.Value - opposite.Value) * 0.1f;
            Move(view, width, height, hx, hy);
            await Frames(service, 3);
            Post(view, WM_LBUTTONDOWN, MK_LBUTTON, width, height, hx, hy);
            await Frames(service, 2);
            for (int i = 1; i <= 8; i++)
            {
                Post(view, WM_MOUSEMOVE, MK_LBUTTON, width, height, hx + outward.X * i / 8, hy + outward.Y * i / 8);
                await Frames(service, 1);
            }
            await Frames(service, 2);
            Post(view, WM_LBUTTONUP, 0, width, height, hx + outward.X, hy + outward.Y);
            await Frames(service, 3);
            await ctx.WaitIdle();
            var resized = await session.Dispatcher.InvokeAsync(() => (box.AssetSideEntity.Get<BlockoutComponent>().Size, box.AssetSideEntity.Transform.Position));
            var grown = resized.Size.X - boxInfo.Size.X;
            var moved = Vector3.Distance(resized.Position, boxInfo.Position);
            Check(grown > 0.4f && IsOnGrid(grown, 0.5f) && MathF.Abs(moved - grown * 0.5f) < 0.01f && resized.Size.Y == boxInfo.Size.Y,
                $"handle drag grew the box by {grown} m, its center moved {moved} m (left face kept)");
            var stillSelected = await session.Dispatcher.InvokeAsync(() => editor.SelectedContent.Contains(box));
            Check(stillSelected, "the handle drag kept the selection");
            await ctx.Screenshot("resized");
        }

        // Scripted resize: top handle up 1 m, symmetric right
        var sizeBefore = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Size);
        await service.SimulateResize(box.Id.ObjectId, BlockoutHandle.Top, 1.0f);
        var sizeAfter = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Size);
        Check(MathF.Abs(sizeAfter.Y - sizeBefore.Y - 1) < 1e-3f, $"top handle: height {sizeBefore.Y} -> {sizeAfter.Y}");

        // Back and front handles: 1 m deeper on each side (room for the stairs drawn on top)
        await service.SimulateResize(box.Id.ObjectId, BlockoutHandle.Back, 1.0f);
        await service.SimulateResize(box.Id.ObjectId, BlockoutHandle.Front, 1.0f);
        var deeper = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Size);
        Check(MathF.Abs(deeper.Z - sizeAfter.Z - 2) < 1e-3f, $"back and front handles: depth {sizeAfter.Z} -> {deeper.Z}");

        // More shapes on top of the box (drawn in its grid), and a wall with a door beside it
        var position = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Transform.Position);
        var top = position + new Vector3(0, sizeAfter.Y, 0);
        var created = new List<Guid>();
        foreach (var (name, start, end, h) in new (string, Vector3, Vector3, float)[]
        {
            ("Stairs", top + new Vector3(-0.5f, 0, 0.5f), top + new Vector3(0.5f, 0, -1.0f), 1.0f),
            ("Doorway", position + new Vector3(-6, 0, 3), position + new Vector3(-1, 0, 3), 3.0f),
            ("Tower", position + new Vector3(-9, 0, -4), position + new Vector3(-5, 0, 0), 5.0f),
            ("Arch", position + new Vector3(4, 0, 4), position + new Vector3(4, 0, 8), 3.5f),
        })
        {
            var item = await session.Dispatcher.InvokeAsync(() => tools.Shapes.First(x => x.Name == name));
            var placement = await service.ComputeDrawing(item, start, end, h);
            Check(placement.HasValue, $"{name} placement {placement}");
            if (placement.HasValue)
                created.Add(await session.Dispatcher.InvokeAsync(() => tools.CreateShape(placement.Value, tools.CurrentStyle.Clone())));
        }
        await ctx.WaitForRendering(30);
        var stairs = await session.Dispatcher.InvokeAsync(() => editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, created[0])) as EntityViewModel);
        var stairsPosition = await session.Dispatcher.InvokeAsync(() => stairs.AssetSideEntity.Transform.Position);
        Check(MathF.Abs(stairsPosition.Y - top.Y) < 1e-3f, $"stairs drawn on top of the box (at {stairsPosition.Y}, top {top.Y})");
        var stairsRotation = await session.Dispatcher.InvokeAsync(() => stairs.AssetSideEntity.Transform.Rotation);
        var climb = Vector3.Transform(-Vector3.UnitZ, stairsRotation);
        Check(climb.Z < -0.99f, $"stairs climb toward the end of the drag ({climb})");
        var doorway = await session.Dispatcher.InvokeAsync(() => editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, created[1])) as EntityViewModel);
        var doorSize = await session.Dispatcher.InvokeAsync(() => doorway.AssetSideEntity.Get<BlockoutComponent>().Size);
        Check(MathF.Abs(doorSize.X - 5) < 1e-3f && MathF.Abs(doorSize.Z - 0.3f) < 1e-3f, $"doorway drawn along a line: {doorSize} (5 m long, default thickness)");
        var arch = await session.Dispatcher.InvokeAsync(() => editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, created[3])) as EntityViewModel);
        var archRotation = await session.Dispatcher.InvokeAsync(() => arch.AssetSideEntity.Transform.Rotation);
        var across = Vector3.Transform(Vector3.UnitX, archRotation);
        Check(MathF.Abs(across.Z) > 0.99f, $"arch drawn along Z spans Z ({across})");
        await ctx.Screenshot("shapes");

        // Styles: preset, pattern, color on the selection
        await session.Dispatcher.InvokeAsync(() =>
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(box);
            tools.ApplyPreset(tools.Presets.First(x => x.Name == "Hazard"));
        });
        var hazard = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Style);
        Check(hazard.Preset == "Hazard" && hazard.Pattern == BlockoutPattern.Stripes, $"Hazard preset applied ({hazard.Preset}, {hazard.Pattern})");
        await session.Dispatcher.InvokeAsync(() => tools.SetPattern(BlockoutPattern.Checker));
        await session.Dispatcher.InvokeAsync(() => tools.SetColor(tools.Colors.First(x => x.Name == "Teal").Color));
        var custom = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Style);
        Check(custom.Preset == null && custom.Pattern == BlockoutPattern.Checker && custom.Color == new Color3(0.15f, 0.55f, 0.55f), $"pattern and color set ({custom.Pattern}, {custom.Color})");
        await ctx.WaitForRendering(20);
        await ctx.Screenshot("styled");

        // Undo the color and pattern: back to Hazard
        await session.Dispatcher.InvokeAsync(() => { session.ActionHistory.UndoCommand.Execute(); session.ActionHistory.UndoCommand.Execute(); });
        var undone = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Style);
        Check(undone.Preset == "Hazard" && undone.Pattern == BlockoutPattern.Stripes, $"undo of the style changes ({undone.Preset}, {undone.Pattern})");

        // Editing a preset restyles the shapes using it and saves the presets
        await session.Dispatcher.InvokeAsync(() =>
        {
            var preset = tools.Presets.First(x => x.Name == "Hazard");
            tools.SelectedPreset = preset;
            preset.Color = new Color3(1, 0.4f, 0);
            preset.Name = "Danger";
        });
        await Task.Delay(1500);
        var renamed = await session.Dispatcher.InvokeAsync(() => box.AssetSideEntity.Get<BlockoutComponent>().Style);
        Check(renamed.Preset == "Danger" && renamed.Color == new Color3(1, 0.4f, 0), $"preset edit restyled the box ({renamed.Preset}, {renamed.Color})");
        var presetsPath = await session.Dispatcher.InvokeAsync(() => tools.PresetsPath);
        Check(presetsPath != null && File.Exists(presetsPath) && File.ReadAllText(presetsPath).Contains("Danger"), $"presets saved to {presetsPath}");

        // Undo everything drawn, then redo
        var countBeforeUndo = await session.Dispatcher.InvokeAsync(ShapeCount);
        Check(countBeforeUndo == before + 5, $"{countBeforeUndo - before} shapes drawn");

        // Save and check the scene file
        await ctx.WaitForShaders();
        await ctx.WaitForRendering(20);
        await ctx.Screenshot("final");
        var saved = await session.Dispatcher.InvokeTask(() => session.SaveSession());
        var scenePath = await session.Dispatcher.InvokeAsync(() => session.AllAssets.First(a => a.Url == sceneUrl).AssetItem.FullPath.ToOSPath());
        var yaml = File.Exists(scenePath) ? File.ReadAllText(scenePath) : "";
        Check(saved && yaml.Contains("!BlockoutComponent") && yaml.Contains("!BlockoutCollider") && yaml.Contains("!BlockoutWall") && yaml.Contains("!BlockoutStairs"),
            $"scene saved with the shapes ({scenePath})");

        // Selected shapes render the same from frame to frame (their collider gizmo lies on their faces: no depth flicker)
        Move(view, width, height, 0.02, 0.02);
        await ctx.WaitForShaders();
        foreach (var (name, selected) in new[] { ("selected", box), ("unselected", (EntityViewModel)null) })
        {
            await session.Dispatcher.InvokeAsync(() =>
            {
                editor.ClearSelection();
                if (selected != null)
                    editor.SelectedContent.Add(selected);
            });
            await Frames(service, 90, 120);
            var changed = new List<int>();
            byte[] previous = null;
            for (int i = 0; i < 4; i++)
            {
                await ctx.ScreenshotHwnd(view, $"{name}-{i}");
                var pixels = LoadScreenshot($"{name}-{i}");
                if (previous != null && pixels != null)
                    changed.Add(CountChangedPixels(previous, pixels));
                previous = pixels;
                await Frames(service, 1);
            }
            Check(changed.Count == 3 && changed.Max() < 1000, $"{name}: steady frames (pixels changed between frames: {string.Join(", ", changed)})");
        }
    }

    private static byte[] LoadScreenshot(string name)
    {
        var directory = Path.GetDirectoryName(typeof(BlockoutTools).Assembly.Location);
        var path = Directory.GetFiles(directory, name + ".png", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path == null)
            return null;
        using var stream = File.OpenRead(path);
        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return pixels;
    }

    private static int CountChangedPixels(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return int.MaxValue;
        var count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > 24 || Math.Abs(a[i + 1] - b[i + 1]) > 24 || Math.Abs(a[i + 2] - b[i + 2]) > 24)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Waits for the blockout tool to update a number of frames (the editor may render slowly: posted input is read per frame).
    /// </summary>
    private static async Task Frames(EditorGameBlockoutService service, int count, double timeoutSeconds = 30)
    {
        var target = service.FrameCount + count;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (service.FrameCount < target && DateTime.UtcNow < deadline)
            await Task.Delay(15);
    }

    private static bool IsOnGrid(float value, float grid) => MathF.Abs(value / grid - MathF.Round(value / grid)) < 1e-3f;

    private static void Move(IntPtr view, int width, int height, double x, double y) => Post(view, WM_MOUSEMOVE, 0, width, height, x, y);

    private static void Post(IntPtr view, uint message, int buttons, int width, int height, double x, double y)
    {
        var px = (int)(width * x);
        var py = (int)(height * y);
        PostMessage(view, message, (IntPtr)buttons, (IntPtr)((py << 16) | (px & 0xFFFF)));
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

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}

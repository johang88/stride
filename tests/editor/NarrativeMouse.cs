// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

// GameStudio check of the mouse handling of the MorningWood dialogue editor's canvas (pass the MorningWood .slnx on the
// AutoTesting command line): dragging a card moves it, dragging from a response's connector to a card links them, releasing
// a link on empty space offers new cards, a rectangle selects, the wheel zooms. The mouse input is posted to the window
// (the real cursor isn't moved). The extension's types are reached with dynamic. Nothing is saved.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Services;
using Stride.GameStudio.AutoTesting;

namespace Stride.Editor.Tests;

[UITest]
public class NarrativeMouse : IUITest
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int MK_LBUTTON = 0x0001;

    private readonly List<string> report = new();
    private int failures;
    private SessionViewModel session;

    private void Check(bool condition, string what)
    {
        if (!condition)
            failures++;
        report.Add($"{(condition ? "PASS" : "FAIL")} {what}");
        Console.WriteLine($"NARRATIVE MOUSE {report[^1]}");
    }

    private Task<T> Ui<T>(Func<T> action) => session.Dispatcher.InvokeAsync(action);

    private Task Do(Action action) => session.Dispatcher.InvokeAsync(action);

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
        session = SessionViewModel.Instance;
        var undo = session.ServiceProvider.Get<IUndoRedoService>();

        var url = await ctx.OpenAssetEditor("GameData/Dialogues/CaptainHale");
        await ctx.WaitForRendering(20);
        dynamic editor = await Ui(() =>
        {
            var asset = session.AllAssets.First(a => a.Url == url);
            return session.ServiceProvider.Get<IAssetEditorsManager>().TryGetAssetEditor<AssetEditorViewModel>(asset, out var found) ? (object)found : null;
        });
        var graph = await Ui(() => FindGraph());
        Check(graph != null, "canvas found");
        if (graph == null || editor == null)
        {
            Finish(ctx);
            return;
        }
        await Do(() => ((dynamic)graph).FrameAll());
        await ctx.WaitForRendering(10);

        // Drag a card by its header
        var before = await Ui(() => Position(editor, "refuse"));
        var header = await Ui(() => CardPoint(graph, editor.CardOf("refuse"), 0.5, 10));
        await Drag(graph, header, new Vector(120, 60));
        await ctx.WaitForRendering(5);
        var after = await Ui(() => Position(editor, "refuse"));
        var zoom = await Ui(() => (double)((dynamic)graph).Zoom);
        var moved = after - before;
        Check(Math.Abs(moved.X - 120 / zoom) < 15 && Math.Abs(moved.Y - 60 / zoom) < 15, $"dragging a card moves it ({moved.X:0},{moved.Y:0} for {120 / zoom:0},{60 / zoom:0} at zoom {zoom:0.00})");
        await Do(() => undo.Undo());
        await ctx.WaitForRendering(5);
        var back = await Ui(() => Position(editor, "refuse"));
        Check((back - before).Length < 0.5, "undo puts the card back");

        // Drag from a response's connector to a card
        var port = await Ui(() => PortPoint(graph, editor.CardOf("active").Responses[0].Port));
        var target = await Ui(() => CardPoint(graph, editor.CardOf("refuse"), 0.5, 40));
        await Drag(graph, port, target - port);
        await ctx.WaitForRendering(5);
        var next = await Ui(() => (string)editor.CardOf("active").Node.Responses[0].Next);
        Check(next == "refuse", $"dragging from a response to a card links them ({next})");
        await Do(() => undo.Undo());

        // Release a link on empty space: the menu offers new cards
        port = await Ui(() => PortPoint(graph, editor.CardOf("active").Responses[0].Port));
        var empty = await Ui(() => EmptyPoint(graph, editor));
        await Drag(graph, port, empty - port);
        await ctx.WaitForRendering(5);
        var hub = await Ui(() => InvokeMenuItem("New hub"));
        Check(hub, "releasing a link on empty space offers new cards");
        await ctx.WaitForRendering(10);
        var created = await Ui(() => (string)editor.CardOf("active").Node.Responses[0].Next);
        Check(created == "hub1" && await Ui(() => editor.CardOf("hub1") != null), $"the new hub is linked to the response ({created})");
        await Do(() => undo.Undo());
        await ctx.WaitForRendering(5);

        // A rectangle selects the cards it touches
        // (starting on the background clears the selection first)
        var start = await Ui(() => EmptyPoint(graph, editor));
        var corner = await Ui(() => CardPoint(graph, editor.CardOf("refuse"), 0.5, 30));
        await Drag(graph, start, corner - start);
        await ctx.WaitForRendering(5);
        var selected = await Ui(() => ((IEnumerable)editor.Nodes).Cast<dynamic>().Count(x => x.IsSelected));
        Check(selected >= 1, $"a rectangle selects cards ({selected})");

        // The wheel zooms
        var zoomBefore = await Ui(() => (double)((dynamic)graph).Zoom);
        await Wheel(graph, empty, 120);
        await ctx.WaitForRendering(5);
        var zoomAfter = await Ui(() => (double)((dynamic)graph).Zoom);
        Check(zoomAfter > zoomBefore, $"the wheel zooms ({zoomBefore:0.00} -> {zoomAfter:0.00})");

        Finish(ctx);
    }

    private void Finish(IUITestContext ctx)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "ui-test-out-dpi100");
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, "narrative-mouse-report.txt"), report);
        ctx.Exit(failures == 0 ? 0 : 10);
    }

    private static FrameworkElement FindGraph()
    {
        foreach (var window in Application.Current.Windows.Cast<Window>())
        {
            if (Find(window, x => x.GetType().Name == "GraphView") is FrameworkElement graph && graph.IsVisible)
                return graph;
        }
        return null;
    }

    private static DependencyObject Find(DependencyObject root, Func<DependencyObject, bool> match)
    {
        if (match(root))
            return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found)
                return found;
        }
        return null;
    }

    private static Vector Position(dynamic editor, string id)
    {
        var card = editor.CardOf(id);
        return new Vector((double)card.X, (double)card.Y);
    }

    /// <summary>
    /// A point of a card (fraction of its width, pixels from its top), in the canvas's coordinates.
    /// </summary>
    private static Point CardPoint(FrameworkElement graph, object card, double x, double y)
    {
        var container = (FrameworkElement)Find(graph, e => e is ContentPresenter presenter && presenter.Content == card);
        return container.TranslatePoint(new Point(container.ActualWidth * x, y), graph);
    }

    private static Point PortPoint(FrameworkElement graph, object port)
    {
        var portProperty = (DependencyProperty)graph.GetType().GetField("PortProperty").GetValue(null);
        var element = (FrameworkElement)Find(graph, e => e is FrameworkElement f && f.GetValue(portProperty) == port);
        return element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), graph);
    }

    /// <summary>
    /// A point of the canvas away from every card.
    /// </summary>
    private static Point EmptyPoint(FrameworkElement graph, dynamic editor)
    {
        var cards = new List<Rect>();
        foreach (var node in (IEnumerable)editor.Nodes)
        {
            var container = (FrameworkElement)Find(graph, e => e is ContentPresenter presenter && presenter.Content == node);
            if (container != null)
                cards.Add(new Rect(container.TranslatePoint(new Point(-40, -40), graph), new Size(container.ActualWidth + 80, container.ActualHeight + 80)));
        }
        for (double y = graph.ActualHeight - 30; y > 30; y -= 25)
        {
            for (double x = graph.ActualWidth - 30; x > 30; x -= 25)
            {
                if (!cards.Any(r => r.Contains(new Point(x, y))))
                    return new Point(x, y);
            }
        }
        return new Point(graph.ActualWidth - 20, graph.ActualHeight - 20);
    }

    private async Task Drag(FrameworkElement graph, Point start, Vector delta)
    {
        var (hwnd, client) = await Ui(() => Client(graph, start));
        Post(hwnd, WM_MOUSEMOVE, 0, client);
        await Task.Delay(60);
        Post(hwnd, WM_LBUTTONDOWN, MK_LBUTTON, client);
        await Task.Delay(60);
        for (int i = 1; i <= 12; i++)
        {
            var (_, point) = await Ui(() => Client(graph, start + delta * (i / 12.0)));
            Post(hwnd, WM_MOUSEMOVE, MK_LBUTTON, point);
            await Task.Delay(25);
        }
        var (_, end) = await Ui(() => Client(graph, start + delta));
        Post(hwnd, WM_LBUTTONUP, 0, end);
        await Task.Delay(150);
    }

    private async Task Wheel(FrameworkElement graph, Point at, int delta)
    {
        var (hwnd, screen) = await Ui(() =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(graph);
            var point = graph.PointToScreen(at);
            return (source.Handle, new POINT { X = (int)point.X, Y = (int)point.Y });
        });
        var (_, client) = await Ui(() => Client(graph, at));
        Post(hwnd, WM_MOUSEMOVE, 0, client);
        await Task.Delay(60);
        PostMessage(hwnd, WM_MOUSEWHEEL, (IntPtr)(delta << 16), (IntPtr)((screen.Y << 16) | (screen.X & 0xFFFF)));
        await Task.Delay(150);
    }

    private static (IntPtr Hwnd, POINT Client) Client(FrameworkElement graph, Point point)
    {
        var source = (HwndSource)PresentationSource.FromVisual(graph);
        var screen = graph.PointToScreen(point);
        var client = new POINT { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
        ScreenToClient(source.Handle, ref client);
        return (source.Handle, client);
    }

    private static void Post(IntPtr hwnd, int message, int keys, POINT client)
        => PostMessage(hwnd, message, (IntPtr)keys, (IntPtr)((client.Y << 16) | (client.X & 0xFFFF)));

    /// <summary>
    /// Clicks an entry of the open context menu (through its automation peer).
    /// </summary>
    private static bool InvokeMenuItem(string header)
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source.RootVisual is not Visual root || Find(root, x => x is ContextMenu { IsOpen: true }) is not ContextMenu menu)
                continue;
            var item = menu.Items.OfType<MenuItem>().FirstOrDefault(x => x.Header as string == header);
            if (item == null)
                continue;
            ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
}

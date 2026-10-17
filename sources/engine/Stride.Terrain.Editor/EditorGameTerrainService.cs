// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.ViewModel;
using Stride.Terrain.Assets;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Services;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;
using Stride.Terrain;
using Stride.Terrain.Editing;
using Stride.Terrain.Splines;

namespace Stride.Terrain.Editor;

/// <summary>
/// Editor game service sculpting and painting terrains with the mouse.
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController), Order = 260)]
public sealed class EditorGameTerrainService : EditorGameMouseServiceBase, Stride.Editor.EditorGame.ViewModels.IEditorGameViewModelService
{
    private static readonly Stride.Core.Diagnostics.Logger Log = Stride.Core.Diagnostics.GlobalLogger.GetLogger("TerrainEditor");

    private readonly EntityHierarchyEditorViewModel editor;
    private readonly IEditorGameController controller;
    private readonly object settingsLock = new();
    private EntityHierarchyEditorGame game;
    private TerrainBrushSettings brush = TerrainBrushSettings.Default;
    private bool toolEnabled;

    private TerrainComponent target;
    private TerrainData targetData;
    private TerrainViewModel targetAsset;
    private TerrainEditingSession session;
    private bool resolvingAsset;
    private TerrainProcessor overlayProcessor;
    private TerrainComponent overlayTarget;
    private TerrainBrushMask overlayMask;
    private Texture overlayMaskTexture;

    private TerrainSnapshot strokeBefore;
    private TerrainBrushTool strokeTool;
    private Vector3? lastHit;
    // Whether a left click on the terrain owns the mouse (until the button is released)
    private bool mouseClaimed;

    public EditorGameTerrainService([NotNull] EntityHierarchyEditorViewModel editor, [NotNull] IEditorGameController controller)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public override bool IsControllingMouse { get; protected set; }

    public override IEnumerable<Type> Dependencies { get { yield return typeof(IEditorGameEntitySelectionService); } }

    /// <summary>
    /// The view model notified of target and brush changes made from the viewport.
    /// </summary>
    internal TerrainToolsViewModel ToolsViewModel { get; set; }

    /// <summary>
    /// Sets the brush used by the tool (thread-safe).
    /// </summary>
    public void SetBrush(TerrainBrushSettings settings)
    {
        lock (settingsLock)
            brush = settings;
    }

    /// <summary>
    /// Enables or disables the terrain tool (thread-safe).
    /// </summary>
    public void SetToolEnabled(bool enabled)
    {
        lock (settingsLock)
            toolEnabled = enabled;
    }

    /// <summary>
    /// Replaces the heights of the current target (game thread), recording an undo operation.
    /// </summary>
    public void ImportHeights(Func<TerrainData, float[]> createHeights, string name)
    {
        controller.InvokeAsync(() =>
        {
            if (session == null || targetData == null)
                return;
            var data = targetData;
            var heights = createHeights(data);
            if (heights == null)
                return;

            var before = new TerrainSnapshot();
            TerrainSnapshot after;
            lock (session.SyncRoot)
            {
                before.Capture(data, Int2.Zero, new Int2(data.Resolution - 1));
                for (int i = 0; i < heights.Length; i++)
                    data.Heights[i] = data.Quantize(heights[i]);
                data.NotifyChanged(Int2.Zero, new Int2(data.Resolution - 1), TerrainChangeFlags.Heights);
                session.MarkModified();
                after = TerrainSnapshot.CaptureSameTiles(data, before);
            }
            PushUndo(before, after, name);
        });
    }

    /// <summary>
    /// Applies terrain splines to the current target (rivers first), recording an undo operation.
    /// </summary>
    /// <param name="splineEntityIds">The entities of the splines to apply; all the splines of the scene when empty.</param>
    /// <returns>The number of splines applied, or -1 if there's no editable terrain.</returns>
    public async Task<int> ApplySplines(IReadOnlyCollection<Guid> splineEntityIds)
    {
        await WaitForTarget();
        return await controller.InvokeAsync(() =>
        {
            if (session == null || targetData == null || target == null)
                return -1;
            var splines = GetSplines(splineEntityIds).OrderBy(x => x.Kind == TerrainSplineKind.River ? 0 : 1).ToList();
            var data = targetData;
            var before = new TerrainSnapshot();
            var applied = 0;
            TerrainSnapshot after;
            lock (session.SyncRoot)
            {
                foreach (var spline in splines)
                {
                    spline.Entity.Transform.UpdateWorldMatrix();
                    if (TerrainSplineShaper.Apply(data, target.Origin, spline, spline.Evaluate(), (min, max) => before.Capture(data, min, max)))
                        applied++;
                }
                if (applied == 0)
                    return 0;
                session.MarkModified();
                after = TerrainSnapshot.CaptureSameTiles(data, before);
            }
            PushUndo(before, after, applied == 1 ? "Apply terrain spline" : "Apply terrain splines");
            return applied;
        });
    }

    /// <summary>
    /// Computes the local positions that put the control points of splines on the terrain surface.
    /// </summary>
    /// <param name="splineEntityIds">The entities of the splines; all the splines of the scene when empty.</param>
    public Task<List<(Guid Id, Vector3 Position)>> ComputeSnappedSplinePoints(IReadOnlyCollection<Guid> splineEntityIds)
    {
        return controller.InvokeAsync(() =>
        {
            var result = new List<(Guid Id, Vector3 Position)>();
            var terrains = game.SceneSystem.SceneInstance?.Processors.Get<TerrainProcessor>()?.Terrains.ToList();
            if (terrains == null)
                return result;
            foreach (var spline in GetSplines(splineEntityIds))
            {
                var parent = spline.Entity.Transform;
                parent.UpdateWorldMatrix();
                var inverse = Matrix.Invert(parent.WorldMatrix);
                foreach (var child in parent.Children)
                {
                    child.UpdateWorldMatrix();
                    var world = child.WorldMatrix.TranslationVector;
                    float? height = null;
                    foreach (var terrain in terrains)
                        height ??= terrain.GetHeight(world);
                    if (height == null)
                        continue;
                    world.Y = height.Value;
                    result.Add((child.Entity.Id, Vector3.TransformCoordinate(world, inverse)));
                }
            }
            return result;
        });
    }

    /// <summary>
    /// The point of the terrain at the center of the view (or in front of the camera) and the camera right direction, to place new objects.
    /// </summary>
    public Task<(Vector3 Position, Vector3 Right)> GetViewCenter()
    {
        return controller.InvokeAsync(() =>
        {
            var camera = game.EditorServices.Get<IEditorGameCameraService>();
            if (camera?.Component == null)
                return (Vector3.Zero, Vector3.UnitX);
            var view = Matrix.Invert(camera.ViewMatrix);
            var right = view.Right;
            right.Y = 0;
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            var ray = EditorGameHelper.CalculateRayFromMousePosition(camera.Component, new Vector2(0.5f), view);
            foreach (var terrain in game.SceneSystem.SceneInstance?.Processors.Get<TerrainProcessor>()?.Terrains ?? Enumerable.Empty<TerrainComponent>())
            {
                if (terrain.Raycast(ray, camera.FarPlane, out var point))
                    return (point, right);
            }
            var ahead = ray.Position + ray.Direction * 20;
            return (ahead, right);
        });
    }

    private List<TerrainSplineComponent> GetSplines(IReadOnlyCollection<Guid> ids)
    {
        var processor = game.SceneSystem.SceneInstance?.Processors.Get<TerrainSplineProcessor>();
        if (processor == null)
            return new List<TerrainSplineComponent>();
        return processor.Splines.Where(x => ids == null || ids.Count == 0 || ids.Contains(x.Entity.Id)).ToList();
    }

    private async Task WaitForTarget()
    {
        // The target and its asset are resolved asynchronously after the scene is loaded
        for (int i = 0; i < 300; i++)
        {
            if (await controller.InvokeAsync(() => session != null && targetData != null))
                return;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Waits until an editable terrain is resolved (its asset is compiled and loaded after the scene), up to a timeout.
    /// </summary>
    public async Task<bool> WaitForTarget(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await controller.InvokeAsync(() => session != null && targetData != null))
                return true;
            if (DateTime.UtcNow > end)
                return false;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Applies a stroke at a world position without the mouse (automated tests, scripted edits). Returns false if there's no editable terrain.
    /// </summary>
    public Task<bool> SimulateStroke(Vector3 worldPosition, TerrainBrushSettings settings, int steps = 10)
    {
        return SimulateStroke(new[] { worldPosition }, settings, steps);
    }

    /// <summary>
    /// Applies a stroke along world positions (one undoable stroke), with <paramref name="steps"/> brush applications at each position.
    /// Returns false if there's no editable terrain.
    /// </summary>
    public async Task<bool> SimulateStroke(IReadOnlyList<Vector3> path, TerrainBrushSettings settings, int steps = 10)
    {
        await WaitForTarget(TimeSpan.FromSeconds(15));
        return await controller.InvokeAsync(() =>
        {
            if (session == null || targetData == null || target == null)
                return false;
            strokeBefore = new TerrainSnapshot();
            strokeTool = settings.Tool;
            foreach (var worldPosition in path)
            {
                var local = worldPosition - target.Origin;
                for (int i = 0; i < steps; i++)
                    ApplyBrush(new Vector2(local.X, local.Z), settings, 1 / 30.0f);
            }
            EndStroke();
            return true;
        });
    }

    /// <summary>
    /// Gets a copy of the current target samples (game thread).
    /// </summary>
    public Task<TerrainData> GetTargetDataCopy()
    {
        return controller.InvokeAsync(() =>
        {
            if (targetData == null)
                return null;
            return session != null ? CloneLocked() : targetData.Clone();
        });
    }

    /// <summary>
    /// Gets the decals drawn into the terrain virtual textures of the editor game: world XZ bounds and tint (game thread).
    /// </summary>
    public Task<List<(Vector2 Min, Vector2 Max, Color4 Tint)>> GetDecals()
    {
        return controller.InvokeAsync(() =>
        {
            var registry = game?.Services.GetService<Stride.Terrain.Decals.TerrainDecalRegistry>();
            return registry?.Decals.Select(x => (x.BoundsMin, x.BoundsMax, x.Surface?.Tint ?? Color4.White)).ToList() ?? new List<(Vector2, Vector2, Color4)>();
        });
    }

    private TerrainData CloneLocked()
    {
        lock (session.SyncRoot)
            return targetData.Clone();
    }

    protected override Task<bool> Initialize(EditorServiceGame editorGame)
    {
        game = (EntityHierarchyEditorGame)editorGame;
        game.Script.AddTask(Update);
        return Task.FromResult(true);
    }

    private async Task Update()
    {
        while (!IsDisposed)
        {
            try
            {
                UpdateFrame();
            }
            catch (Exception e)
            {
                Log.Error("Terrain tool failure", e);
                EndStroke();
            }
            await game.Script.NextFrame();
        }
        overlayMaskTexture?.Dispose();
        overlayMaskTexture = null;
    }

    private void UpdateFrame()
    {
        TerrainBrushSettings settings;
        bool enabled;
        lock (settingsLock)
        {
            settings = brush;
            enabled = toolEnabled;
        }

        UpdateTarget();
        if (!enabled || target == null || targetData == null || !IsActive)
        {
            EndStroke();
            HideOverlay();
            mouseClaimed = false;
            IsControllingMouse = false;
            return;
        }

        var input = game.Input;
        var camera = game.EditorServices.Get<IEditorGameCameraService>();
        if (camera?.Component == null)
            return;

        // Shortcuts: [ and ] change the radius, with Ctrl they rotate a custom brush
        if (input.IsKeyPressed(Keys.OemOpenBrackets) || input.IsKeyPressed(Keys.OemCloseBrackets))
        {
            var decrease = input.IsKeyPressed(Keys.OemOpenBrackets);
            if (settings.Mask != null && (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl)))
            {
                var degrees = MathUtil.RadiansToDegrees(settings.Rotation) + (decrease ? -15 : 15);
                degrees = ((MathF.Round(degrees / 15) * 15 % 360) + 360) % 360;
                settings.Rotation = MathUtil.DegreesToRadians(degrees);
                SetBrush(settings);
                editor.Dispatcher.InvokeAsync(() => ToolsViewModel?.OnRotationChangedFromViewport(degrees));
            }
            else
            {
                settings.Radius = Math.Clamp(settings.Radius * (decrease ? 1 / 1.2f : 1.2f), 0.5f, 500.0f);
                SetBrush(settings);
                var radius = settings.Radius;
                editor.Dispatcher.InvokeAsync(() => ToolsViewModel?.OnRadiusChangedFromViewport(radius));
            }
        }
        settings.Invert ^= input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);

        // Mouse ray against the terrain
        var mouse = input.MousePosition;
        var mouseInViewport = mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= 1 && mouse.Y <= 1;
        Vector3? hit = null;
        if (mouseInViewport)
        {
            var ray = EditorGameHelper.CalculateRayFromMousePosition(camera.Component, mouse, Matrix.Invert(camera.ViewMatrix));
            if (target.Raycast(ray, camera.FarPlane, out var point))
                hit = point;
        }
        // While painting, keep the last hit when the ray briefly misses (e.g. over a hole)
        if (hit == null && strokeBefore != null)
            hit = lastHit;
        lastHit = hit;

        var origin = target.Origin;
        if (hit.HasValue)
        {
            var local = hit.Value - origin;
            var color = settings.Tool switch
            {
                TerrainBrushTool.Paint => new Color4(0.2f, 0.9f, 1.0f, 0.9f),
                TerrainBrushTool.Hole => new Color4(1.0f, 0.3f, 0.2f, 0.9f),
                _ => settings.Invert ? new Color4(1.0f, 0.55f, 0.1f, 0.9f) : new Color4(1.0f, 0.95f, 0.3f, 0.9f),
            };
            ShowOverlay(new Vector2(local.X, local.Z), settings, color);
        }
        else
        {
            HideOverlay();
        }

        var leftDown = input.IsMouseButtonDown(MouseButton.Left) && !input.IsMouseButtonDown(MouseButton.Right) && !input.IsMouseButtonDown(MouseButton.Middle);
        if (strokeBefore != null)
        {
            if (!leftDown)
            {
                EndStroke();
            }
            else if (hit.HasValue)
            {
                var local = hit.Value - origin;
                var dt = Math.Clamp((float)game.UpdateTime.Elapsed.TotalSeconds, 0.0f, 0.1f);
                ApplyBrush(new Vector2(local.X, local.Z), settings, dt);
            }
        }
        else if (hit.HasValue && input.IsMouseButtonPressed(MouseButton.Left) && IsMouseAvailable && !input.IsMouseButtonDown(MouseButton.Right)
                 && !input.IsKeyDown(Keys.LeftAlt) && !input.IsKeyDown(Keys.RightAlt))
        {
            // Alt + left drag orbits the camera
            mouseClaimed = true;
            var local = hit.Value - origin;
            if (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl))
            {
                Sample(new Vector2(local.X, local.Z), settings);
            }
            else if (session != null)
            {
                strokeBefore = new TerrainSnapshot();
                strokeTool = settings.Tool;
                // A stroke always applies at least once, even for a click without moving
                ApplyBrush(new Vector2(local.X, local.Z), settings, 1 / 30.0f);
            }
        }

        // Only clicks and strokes take the mouse, so the camera keeps working while hovering the terrain.
        // The release frame is kept so that the click doesn't also change the selection.
        if (mouseClaimed && !input.IsMouseButtonDown(MouseButton.Left) && !input.IsMouseButtonReleased(MouseButton.Left))
            mouseClaimed = false;
        IsControllingMouse = mouseClaimed || strokeBefore != null;
    }

    private void ApplyBrush(Vector2 localCenter, TerrainBrushSettings settings, float dt)
    {
        var data = targetData;
        var before = strokeBefore;
        lock (session.SyncRoot)
        {
            TerrainBrush.Apply(data, localCenter, settings, dt, (min, max) => before.Capture(data, min, max));
            session.MarkModified();
        }
    }

    private void Sample(Vector2 localCenter, TerrainBrushSettings settings)
    {
        var data = targetData;
        if (settings.Tool == TerrainBrushTool.Paint)
        {
            var x = Math.Clamp((int)MathF.Round(localCenter.X / data.CellSize), 0, data.Resolution - 1);
            var z = Math.Clamp((int)MathF.Round(localCenter.Y / data.CellSize), 0, data.Resolution - 1);
            var layer = TerrainSplat.GetDominantLayer(data.Splat[z * data.Resolution + x]);
            editor.Dispatcher.InvokeAsync(() => ToolsViewModel?.OnLayerSampled(layer));
        }
        else
        {
            var height = data.SampleHeight(localCenter.X, localCenter.Y);
            editor.Dispatcher.InvokeAsync(() => ToolsViewModel?.OnHeightSampled(height));
        }
    }

    private void EndStroke()
    {
        var before = strokeBefore;
        strokeBefore = null;
        if (before == null || before.IsEmpty || session == null || targetData == null)
            return;

        TerrainSnapshot after;
        lock (session.SyncRoot)
            after = TerrainSnapshot.CaptureSameTiles(targetData, before);
        PushUndo(before, after, strokeTool switch
        {
            TerrainBrushTool.Raise => "Sculpt terrain",
            TerrainBrushTool.Smooth => "Smooth terrain",
            TerrainBrushTool.Flatten => "Flatten terrain",
            TerrainBrushTool.Noise => "Add terrain noise",
            TerrainBrushTool.Paint => "Paint terrain",
            TerrainBrushTool.Hole => "Edit terrain holes",
            _ => "Edit terrain",
        });
    }

    private void PushUndo(TerrainSnapshot before, TerrainSnapshot after, string name)
    {
        var asset = targetAsset;
        var editSession = session;
        if (asset == null || editSession == null)
            return;

        editor.Dispatcher.InvokeAsync(() =>
        {
            var undoRedo = editor.ServiceProvider.Get<IUndoRedoService>();
            using var transaction = undoRedo.CreateTransaction();
            // The first edit decides where the samples are saved
            if (UPath.IsNullOrEmpty(asset.Asset.DataFile))
                asset.PropertyGraph.RootNode[nameof(TerrainAsset.DataFile)].Update(asset.GetDefaultDataFile());
            undoRedo.PushOperation(new TerrainStrokeOperation(controller, editSession, before, after, asset.Dirtiables));
            undoRedo.SetName(transaction, name);
        });
    }

    private void UpdateTarget()
    {
        var newTarget = FindTarget();
        if (newTarget != target)
        {
            EndStroke();
            HideOverlay();
            target = newTarget;
            targetData = null;
            targetAsset = null;
            session = null;
        }

        var data = target?.Terrain;
        if (data == targetData)
            return;

        var previous = targetData;
        targetData = data;
        if (data == null)
        {
            NotifyTargetChanged();
            return;
        }

        // The asset was recompiled (e.g. a property changed) and reloaded: keep the unsaved edits
        if (session != null && previous != null && session.HasUnsavedChanges)
        {
            lock (session.SyncRoot)
            {
                var source = previous.Resolution == data.Resolution
                    ? previous
                    : TerrainDataFile.Resample(previous, data.Resolution, data.CellSize, data.MinHeight, data.MaxHeight);
                data.CopySamplesFrom(source);
                session.Data = data;
            }
        }
        else if (session != null)
        {
            lock (session.SyncRoot)
                session.Data = data;
        }
        else
        {
            ResolveAsset(data);
        }

        NotifyTargetChanged();
    }

    private void ResolveAsset(TerrainData data)
    {
        if (resolvingAsset)
            return;
        if (!game.Content.TryGetAssetUrl(data, out var url))
            return;

        resolvingAsset = true;
        var component = target;
        editor.Dispatcher.InvokeAsync(() =>
        {
            var asset = editor.Session.AllAssets.OfType<TerrainViewModel>().FirstOrDefault(x => x.Url == url);
            var editingSession = asset != null ? asset.EditingSession ??= new TerrainEditingSession() : null;
            controller.InvokeAsync(() =>
            {
                resolvingAsset = false;
                if (component != target || asset == null)
                    return;
                targetAsset = asset;
                session = editingSession;
                lock (session.SyncRoot)
                {
                    // Another editor may already hold edited samples of this terrain
                    if (session.Data != null && session.Data != targetData && session.HasUnsavedChanges && session.Data.Resolution == targetData.Resolution)
                        targetData.CopySamplesFrom(session.Data);
                    session.Data = targetData;
                }
            });
        });
    }

    private TerrainComponent FindTarget()
    {
        var selection = Services.Get<IEditorGameEntitySelectionService>();
        if (selection != null)
        {
            foreach (var id in selection.GetSelectedIds())
            {
                if (controller.FindGameSidePart(id) is Entity entity && entity.Get<TerrainComponent>() is { } selected)
                    return selected;
            }
        }

        // Keep the current target while it's still in the scene, otherwise use the first terrain found
        if (target?.Entity?.Scene != null)
            return target;
        return game.ContentScene?.Yield().BreadthFirst(x => x.Children).SelectMany(x => x.Entities)
            .BreadthFirst(x => x.Transform.Children.Select(y => y.Entity))
            .Select(x => x.Get<TerrainComponent>()).FirstOrDefault(x => x != null);
    }

    private void NotifyTargetChanged()
    {
        var name = target?.Entity?.Name;
        var material = targetData?.Material;
        var layers = material?.Layers.Select(x => x.Name).ToList() ?? new List<string>();
        editor.Dispatcher.InvokeAsync(() => ToolsViewModel?.OnTargetChanged(name, layers));
    }

    private void ShowOverlay(Vector2 center, in TerrainBrushSettings settings, Color4 color)
    {
        var processor = game.SceneSystem.SceneInstance?.Processors.OfType<TerrainProcessor>().FirstOrDefault();
        if (processor == null)
            return;
        if (overlayTarget != null && overlayTarget != target)
            HideOverlay();
        overlayProcessor = processor;
        overlayTarget = target;
        var inner = settings.Mask != null ? 0 : settings.Radius * (1 - settings.Falloff);
        processor.SetBrushOverlay(target, center, settings.Radius, inner, color, GetMaskTexture(settings.Mask), settings.Rotation);
    }

    /// <summary>
    /// The GPU copy of a brush mask drawn by the overlay (game thread), recreated when the brush changes.
    /// </summary>
    private Texture GetMaskTexture(TerrainBrushMask mask)
    {
        if (mask != overlayMask)
        {
            overlayMaskTexture?.Dispose();
            overlayMaskTexture = null;
            overlayMask = mask;
            if (mask != null)
            {
                var pixels = new byte[mask.Values.Length];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (byte)Math.Clamp(mask.Values[i] * 255 + 0.5f, 0, 255);
                overlayMaskTexture = Texture.New2D(game.GraphicsDevice, mask.Size, mask.Size, PixelFormat.R8_UNorm, pixels);
            }
        }
        return overlayMaskTexture;
    }

    private void HideOverlay()
    {
        if (overlayTarget != null)
            overlayProcessor?.SetBrushOverlay(overlayTarget, Vector2.Zero, 0, 0, Color4.White);
        overlayTarget = null;
    }
}

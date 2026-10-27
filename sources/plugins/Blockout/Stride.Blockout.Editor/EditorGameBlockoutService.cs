// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Game;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.Services;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Game;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Assets.Presentation.AssetEditors.Gizmos;
using Stride.BepuPhysics;
using Stride.Blockout.Rendering;
using Stride.Blockout.Shapes;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Editor.EditorGame.Game;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;

namespace Stride.Blockout.Editor;

/// <summary>
/// The settings of the blockout tool the panel sends to the viewport.
/// </summary>
public struct BlockoutToolSettings
{
    /// <summary>
    /// Whether dragging in the viewport draws new shapes (the tool is active).
    /// </summary>
    public bool DrawEnabled;

    /// <summary>
    /// The palette item new shapes are made from.
    /// </summary>
    public BlockoutShapeItemViewModel Shape;

    /// <summary>
    /// The style of new shapes.
    /// </summary>
    public BlockoutStyle Style;

    /// <summary>
    /// The grid spacing in meters.
    /// </summary>
    public float GridSize;

    /// <summary>
    /// Whether positions and sizes snap to the grid (Shift inverts it).
    /// </summary>
    public bool Snap;
}

/// <summary>
/// The faces of a shape that have a resize handle.
/// </summary>
public enum BlockoutHandle
{
    None = -1,
    Right = 0,
    Left = 1,
    Top = 2,
    Back = 3,
    Front = 4,
}

/// <summary>
/// The placement of a new shape: in the space of its entity's parent (the scene root).
/// </summary>
public readonly record struct BlockoutPlacementResult(BlockoutShapeItemViewModel Item, Vector3 Size, Vector3 Position, Quaternion Rotation);

/// <summary>
/// Editor game service of the blockout tools: draws new shapes by dragging on any surface (terrain, models, other shapes),
/// resizes the selected shape with handles on its faces, and shows the grid under the mouse.
/// </summary>
[EditorGameService(typeof(EntityHierarchyEditorController), Order = 265)]
public sealed class EditorGameBlockoutService : EditorGameMouseServiceBase, Stride.Editor.EditorGame.ViewModels.IEditorGameViewModelService
{
    private static readonly Stride.Core.Diagnostics.Logger Log = Stride.Core.Diagnostics.GlobalLogger.GetLogger("BlockoutEditor");

    private static readonly Vector3[] HandleAxes = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
    private const float DragThresholdPixels = 6;

    private enum DrawState
    {
        Idle,
        // Left button down, not moved enough yet: may still be a click that selects
        Pressed,
        Footprint,
        Height,
        // The click that ended the drawing: owns the mouse until released
        Finishing,
    }

    private readonly EntityHierarchyEditorViewModel editor;
    private readonly IEditorGameController controller;
    private readonly object settingsLock = new();
    private BlockoutToolSettings settings;
    private EntityHierarchyEditorGame game;

    // Overlays
    private BlockoutLineBatch gridLines;
    private BlockoutLineBatch outlineLines;
    private readonly BlockoutHandleGizmo[] handles = new BlockoutHandleGizmo[HandleAxes.Length];
    private Entity preview;
    private BlockoutComponent previewShape;

    // Selection
    private readonly List<Entity> selection = new();

    // Drawing
    private DrawState drawState;
    private Vector2 pressMouse;
    private Frame frame;
    private float baseHeight;
    private Vector2 cornerA;
    private Vector2 cornerB;
    private float height;
    private float heightStart;
    private int rotationSteps;
    private BlockoutShapeItemViewModel drawItem;

    // Resizing
    private BlockoutHandle hoveredHandle = BlockoutHandle.None;
    private BlockoutHandle dragHandle = BlockoutHandle.None;
    private Entity dragEntity;
    private Vector3 dragStartSize;
    private Vector3 dragStartPosition;
    private float dragStartParameter;
    private bool releaseClaimed;
    // The transformation gizmo, disabled while a handle is under the mouse or dragged
    private TransformationGizmo suppressedGizmo;

    private int frameCount;

    /// <summary>
    /// The drawing frame: horizontal axes turned by a yaw around an origin (the shape drawn on, or the world).
    /// </summary>
    private readonly record struct Frame(Vector3 Origin, float Yaw)
    {
        public Quaternion Rotation => Quaternion.RotationY(Yaw);

        public Vector2 ToLocal(Vector3 world)
        {
            var local = Vector3.Transform(world - Origin, Quaternion.Invert(Rotation));
            return new Vector2(local.X, local.Z);
        }

        public Vector3 ToWorld(Vector2 local, float y)
        {
            var world = Vector3.Transform(new Vector3(local.X, 0, local.Y), Rotation) + Origin;
            world.Y = y;
            return world;
        }
    }

    /// <summary>
    /// A point of a surface under a ray.
    /// </summary>
    public readonly record struct SurfaceHit(Vector3 Point, Vector3 Normal, float Distance, Entity Blockout);

    public EditorGameBlockoutService([NotNull] EntityHierarchyEditorViewModel editor, [NotNull] IEditorGameController controller)
    {
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public override bool IsControllingMouse { get; protected set; }

    public override IEnumerable<Type> Dependencies { get { yield return typeof(IEditorGameEntitySelectionService); } }

    /// <summary>
    /// The panel notified of shapes drawn and resized.
    /// </summary>
    internal BlockoutToolsViewModel ToolsViewModel { get; set; }

    /// <summary>
    /// Whether a shape is being drawn or resized (game thread).
    /// </summary>
    public bool IsEditing => drawState is DrawState.Footprint or DrawState.Height || dragHandle != BlockoutHandle.None;

    /// <summary>
    /// The number of frames the tool has updated (to wait for input to be processed).
    /// </summary>
    public int FrameCount => Volatile.Read(ref frameCount);

    public void SetSettings(BlockoutToolSettings value)
    {
        lock (settingsLock)
            settings = value;
    }

    private BlockoutToolSettings GetSettings()
    {
        lock (settingsLock)
            return settings;
    }

    protected override Task<bool> Initialize(EditorServiceGame editorGame)
    {
        game = (EntityHierarchyEditorGame)editorGame;
        var device = game.GraphicsDevice;
        gridLines = new BlockoutLineBatch(device, game.EditorScene, "Blockout grid", new Color(220, 220, 220), 0.6f);
        outlineLines = new BlockoutLineBatch(device, game.EditorScene, "Blockout outline", new Color(255, 190, 60), 1.5f);
        var cube = BlockoutHandleGizmo.CreateCube(device);
        var colors = new[] { new Color(230, 70, 60), new Color(230, 70, 60), new Color(110, 210, 70), new Color(70, 130, 245), new Color(70, 130, 245) };
        for (int i = 0; i < handles.Length; i++)
            handles[i] = new BlockoutHandleGizmo(device, game.EditorScene, cube, colors[i]);

        Services.Get<IEditorGameEntitySelectionService>().SelectionUpdated += OnSelectionUpdated;
        game.Script.AddTask(Update);
        return Task.FromResult(true);
    }

    public override ValueTask DisposeAsync()
    {
        var selectionService = Services.Get<IEditorGameEntitySelectionService>();
        if (selectionService != null)
            selectionService.SelectionUpdated -= OnSelectionUpdated;
        return base.DisposeAsync();
    }

    private void OnSelectionUpdated(object sender, EntitySelectionEventArgs e)
    {
        selection.Clear();
        selection.AddRange(e.NewSelection);
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
                Log.Error("Blockout tool failure", e);
                CancelDrawing();
                CancelResize();
                SuppressGizmo(false);
            }
            Interlocked.Increment(ref frameCount);
            await game.Script.NextFrame();
        }
        SuppressGizmo(false);
        gridLines?.Dispose();
        outlineLines?.Dispose();
    }

    private void UpdateFrame()
    {
        var current = GetSettings();
        gridLines.Clear();
        outlineLines.Clear();

        var camera = game.EditorServices.Get<IEditorGameCameraService>();
        var input = game.Input;
        if (camera?.Component == null || !IsActive)
        {
            HideHandles();
            SuppressGizmo(false);
            Upload();
            IsControllingMouse = false;
            return;
        }

        var mouse = input.MousePosition;
        var inViewport = mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= 1 && mouse.Y <= 1;
        var ray = EditorGameHelper.CalculateRayFromMousePosition(camera.Component, mouse, Matrix.Invert(camera.ViewMatrix));
        var leftDown = input.IsMouseButtonDown(MouseButton.Left);
        var leftPressed = input.IsMouseButtonPressed(MouseButton.Left);
        var otherButtons = input.IsMouseButtonDown(MouseButton.Right) || input.IsMouseButtonDown(MouseButton.Middle);
        var alt = input.IsKeyDown(Keys.LeftAlt) || input.IsKeyDown(Keys.RightAlt);
        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        var ctrl = input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl);
        var snap = current.Snap ^ shift;
        var grid = Math.Max(current.GridSize, 0.01f);
        var escape = input.IsKeyPressed(Keys.Escape);

        if (!current.DrawEnabled && drawState != DrawState.Idle && drawState != DrawState.Finishing)
            CancelDrawing();

        var anchor = selection.LastOrDefault(x => x.Get<BlockoutComponent>() != null && x.Scene != null);

        if (dragHandle != BlockoutHandle.None)
        {
            // Resizing
            if (escape || dragEntity?.Scene == null)
                CancelResize();
            else if (!leftDown)
                EndResize();
            else
                UpdateResize(ray, snap, grid, ctrl);
            releaseClaimed = true;
        }
        else if (drawState != DrawState.Idle)
        {
            UpdateDrawing(current, ray, mouse, leftDown, leftPressed, escape, snap, grid, input.IsKeyPressed(Keys.T), ctrl);
        }
        else
        {
            // The handles have priority over the transformation gizmo, which covers them on small shapes (but not over a gizmo drag)
            var gizmoHovered = game.EditorServices.Get<EditorGameEntityTransformService>() is { IsControllingMouse: true } && (!leftDown || leftPressed);
            var handlesAvailable = IsMouseAvailable || gizmoHovered;
            hoveredHandle = inViewport && anchor != null && handlesAvailable && !otherButtons ? HitHandle(anchor, ray, camera) : BlockoutHandle.None;
            if (hoveredHandle != BlockoutHandle.None && leftPressed && !alt)
            {
                BeginResize(anchor, hoveredHandle, ray);
            }
            else if (current.DrawEnabled && current.Shape != null && inViewport && IsMouseAvailable && !alt && !otherButtons)
            {
                // Under the mouse: the grid of the surface the next shape would be drawn on
                if (RaycastSurface(ray, camera.FarPlane, null, out var hit))
                {
                    var hoverFrame = FrameOf(hit);
                    var point = SnapPoint(hoverFrame.ToLocal(hit.Point), snap, grid);
                    DrawGridPatch(hoverFrame, point, hit.Point.Y, snap ? grid : 1, camera);
                    if (leftPressed)
                    {
                        drawState = DrawState.Pressed;
                        drawItem = current.Shape;
                        pressMouse = mouse;
                        frame = hoverFrame;
                        baseHeight = hit.Point.Y;
                        cornerA = cornerB = point;
                        rotationSteps = 0;
                    }
                }
            }
        }

        if (dragHandle == BlockoutHandle.None && releaseClaimed && !leftDown && !input.IsMouseButtonReleased(MouseButton.Left))
            releaseClaimed = false;

        // Handles of the selected shape
        if (anchor != null && drawState is DrawState.Idle or DrawState.Pressed)
            ShowHandles(anchor, camera);
        else
            HideHandles();

        SuppressGizmo(hoveredHandle != BlockoutHandle.None || dragHandle != BlockoutHandle.None);
        Upload();
        IsControllingMouse = releaseClaimed || hoveredHandle != BlockoutHandle.None || dragHandle != BlockoutHandle.None
            || drawState is DrawState.Footprint or DrawState.Height or DrawState.Finishing;
    }

    /// <summary>
    /// Disables the transformation gizmo of the selection (it then neither highlights nor starts a transformation), or restores it.
    /// </summary>
    private void SuppressGizmo(bool suppress)
    {
        var transform = game.EditorServices.Get<EditorGameEntityTransformService>();
        var gizmo = transform?.ActiveTransformationGizmo;
        if (suppress)
        {
            if (gizmo is { IsEnabled: true })
            {
                gizmo.ClearTransformationAxes();
                gizmo.IsEnabled = false;
                suppressedGizmo = gizmo;
            }
        }
        else if (suppressedGizmo != null)
        {
            if (suppressedGizmo == gizmo && transform.EntityWithGizmo != null)
                gizmo.IsEnabled = true;
            suppressedGizmo = null;
        }
    }

    private void Upload()
    {
        var commandList = game.GraphicsContext.CommandList;
        gridLines.Upload(commandList);
        outlineLines.Upload(commandList);
    }

    #region Drawing

    private void UpdateDrawing(BlockoutToolSettings current, Ray ray, Vector2 mouse, bool leftDown, bool leftPressed, bool escape, bool snap, float grid, bool rotate, bool square)
    {
        if (escape)
        {
            CancelDrawing();
            return;
        }
        if (rotate && drawState is DrawState.Footprint or DrawState.Height)
            rotationSteps = (rotationSteps + 1) % 4;

        switch (drawState)
        {
            case DrawState.Pressed:
            {
                if (!leftDown)
                {
                    // A click: the selection service handles it
                    CancelDrawing();
                    return;
                }
                var screen = new Vector2(game.GraphicsDevice.Presenter.BackBuffer.Width, game.GraphicsDevice.Presenter.BackBuffer.Height);
                if (((mouse - pressMouse) * screen).Length() < DragThresholdPixels)
                    return;
                drawState = DrawState.Footprint;
                height = drawItem.Height;
                goto case DrawState.Footprint;
            }
            case DrawState.Footprint:
            {
                if (IntersectHorizontal(ray, baseHeight, out var point))
                {
                    cornerB = SnapPoint(frame.ToLocal(point), snap, grid);
                    if (square)
                        cornerB = SquareCorner(cornerA, cornerB);
                }
                if (!leftDown)
                {
                    drawState = DrawState.Height;
                    heightStart = VerticalParameter(ray, frame.ToWorld(cornerB, baseHeight)) ?? 0;
                }
                break;
            }
            case DrawState.Height:
            {
                var parameter = VerticalParameter(ray, frame.ToWorld(cornerB, baseHeight));
                if (parameter.HasValue)
                {
                    var raw = drawItem.Height + parameter.Value - heightStart;
                    height = snap ? Math.Max(MathF.Round(raw / grid) * grid, grid) : Math.Max(raw, BlockoutShape.MinSize);
                }
                if (leftPressed)
                {
                    FinishDrawing(current);
                    return;
                }
                break;
            }
            case DrawState.Finishing:
                if (!leftDown)
                    drawState = DrawState.Idle;
                return;
        }

        var placement = ComputePlacement(drawItem, frame, cornerA, cornerB, baseHeight, height, rotationSteps, snap ? grid : 0);
        UpdatePreview(placement, current.Style);
        DrawFootprint(placement);
    }

    /// <summary>
    /// The corner of the square footprint from <paramref name="a"/> toward <paramref name="b"/>: as wide as deep, the longer side of
    /// the dragged one (on the grid when both corners are).
    /// </summary>
    private static Vector2 SquareCorner(Vector2 a, Vector2 b)
    {
        var delta = b - a;
        var side = Math.Max(MathF.Abs(delta.X), MathF.Abs(delta.Y));
        return a + new Vector2(delta.X < 0 ? -side : side, delta.Y < 0 ? -side : side);
    }

    private void FinishDrawing(BlockoutToolSettings current)
    {
        var placement = ComputePlacement(drawItem, frame, cornerA, cornerB, baseHeight, height, rotationSteps, current.Snap ? current.GridSize : 0);
        drawItem.Height = height;
        drawState = DrawState.Finishing;
        RemovePreview();
        var style = (current.Style ?? new BlockoutStyle()).Clone();
        var tools = ToolsViewModel;
        if (tools != null)
            editor.Dispatcher.InvokeAsync(() => tools.CreateShape(placement, style));
    }

    private void CancelDrawing()
    {
        drawState = DrawState.Idle;
        RemovePreview();
    }

    /// <summary>
    /// The size and transform of a shape drawn from corner <paramref name="a"/> to corner <paramref name="b"/> of a footprint (in
    /// the drawing frame) at a height.
    /// </summary>
    private static BlockoutPlacementResult ComputePlacement(BlockoutShapeItemViewModel item, Frame frame, Vector2 a, Vector2 b, float baseHeight, float height, int rotationSteps, float grid)
    {
        var minimum = grid > 0 ? grid : 0.1f;
        var extent = new Vector2(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
        var center = (a + b) * 0.5f;
        var alongX = extent.X >= extent.Y;
        var length = Math.Max(alongX ? extent.X : extent.Y, minimum);
        var across = alongX ? extent.Y : extent.X;
        float yaw;
        Vector3 size;
        switch (item.Placement)
        {
            case BlockoutPlacement.Span:
                // A wall along the longer side; dragged along a line, the default thickness centered on it
                size = new Vector3(length, height, across < minimum * 0.5f ? item.DefaultSize.Z : across);
                yaw = alongX ? 0 : MathUtil.PiOverTwo;
                break;
            case BlockoutPlacement.Rise:
            {
                // Rising (toward local -Z) along the longer side, toward the end of the drag
                size = new Vector3(across < minimum * 0.5f ? item.DefaultSize.X : across, height, length);
                var direction = alongX ? new Vector2(MathF.Sign(b.X - a.X), 0) : new Vector2(0, MathF.Sign(b.Y - a.Y));
                // RotationY(yaw) turns -Z into (-sin yaw, -cos yaw)
                yaw = MathF.Atan2(-direction.X, -direction.Y);
                break;
            }
            default:
                size = new Vector3(Math.Max(extent.X, minimum), height, Math.Max(extent.Y, minimum));
                yaw = 0;
                break;
        }
        if ((rotationSteps & 1) != 0)
            size = new Vector3(size.Z, size.Y, size.X);
        yaw += rotationSteps * MathUtil.PiOverTwo;

        var position = frame.ToWorld(center, baseHeight);
        var rotation = Quaternion.RotationY(frame.Yaw + yaw);
        return new BlockoutPlacementResult(item, size, position, rotation);
    }

    private void UpdatePreview(BlockoutPlacementResult placement, BlockoutStyle style)
    {
        var scene = FindPreviewScene();
        if (scene == null)
            return;
        if (preview == null)
        {
            previewShape = new BlockoutComponent { Shape = placement.Item.CreateShape(), CastShadows = false };
            preview = new Entity("Blockout preview") { previewShape };
        }
        if (preview.Scene != scene)
            scene.Entities.Add(preview);
        previewShape.Size = placement.Size;
        previewShape.Style = style ?? new BlockoutStyle();
        preview.Transform.Position = placement.Position;
        preview.Transform.Rotation = placement.Rotation;
    }

    /// <summary>
    /// The game-side scene of the edited asset: the editor maps the entities of that scene back to the asset (picking).
    /// </summary>
    private Scene FindPreviewScene()
    {
        var content = game.ContentScene;
        if (content == null)
            return null;
        var rootId = editor.HierarchyRoot?.Id.ObjectId ?? Guid.Empty;
        return FindScene(content, rootId) ?? content;

        static Scene FindScene(Scene scene, Guid id)
        {
            if (scene.Id == id)
                return scene;
            foreach (var child in scene.Children)
            {
                if (FindScene(child, id) is { } found)
                    return found;
            }
            return null;
        }
    }

    private void RemovePreview()
    {
        if (preview != null)
            preview.Scene = null;
        preview = null;
        previewShape = null;
    }

    private void DrawFootprint(BlockoutPlacementResult placement)
    {
        // The box of the new shape, and the footprint dragged on the surface
        var world = Matrix.RotationQuaternion(placement.Rotation) * Matrix.Translation(placement.Position);
        var size = placement.Size;
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) != 0 ? size.X * 0.5f : -size.X * 0.5f, (i & 2) != 0 ? size.Y : 0, (i & 4) != 0 ? size.Z * 0.5f : -size.Z * 0.5f);
            corners[i] = Vector3.TransformCoordinate(local, world);
        }
        foreach (var (i, j) in BoxEdges)
            outlineLines.Line(corners[i], corners[j]);
        var a = frame.ToWorld(cornerA, baseHeight + 0.01f);
        var b = frame.ToWorld(cornerB, baseHeight + 0.01f);
        var ab = frame.ToWorld(new Vector2(cornerA.X, cornerB.Y), baseHeight + 0.01f);
        var ba = frame.ToWorld(new Vector2(cornerB.X, cornerA.Y), baseHeight + 0.01f);
        gridLines.Line(a, ba);
        gridLines.Line(ba, b);
        gridLines.Line(b, ab);
        gridLines.Line(ab, a);
    }

    private static readonly (int, int)[] BoxEdges =
    {
        (0, 1), (2, 3), (4, 5), (6, 7),
        (0, 2), (1, 3), (4, 6), (5, 7),
        (0, 4), (1, 5), (2, 6), (3, 7),
    };

    private void DrawGridPatch(Frame patchFrame, Vector2 point, float y, float spacing, IEditorGameCameraService camera)
    {
        // A few cells of the grid around the snapped point, and a cross on it
        const int cells = 3;
        y += 0.01f;
        for (int i = -cells; i <= cells; i++)
        {
            var offset = i * spacing;
            gridLines.Line(patchFrame.ToWorld(point + new Vector2(-cells * spacing, offset), y), patchFrame.ToWorld(point + new Vector2(cells * spacing, offset), y));
            gridLines.Line(patchFrame.ToWorld(point + new Vector2(offset, -cells * spacing), y), patchFrame.ToWorld(point + new Vector2(offset, cells * spacing), y));
        }
        var center = patchFrame.ToWorld(point, y);
        var tick = Vector3.Distance(camera.Position, center) * 0.02f;
        outlineLines.Line(center - new Vector3(0, 0, 0), center + new Vector3(0, tick * 2, 0));
    }

    private static Vector2 SnapPoint(Vector2 point, bool snap, float grid)
    {
        if (!snap)
            return point;
        return new Vector2(MathF.Round(point.X / grid) * grid, MathF.Round(point.Y / grid) * grid);
    }

    // Drawing on a shape follows its axes and grid; elsewhere, the world's
    private static Frame FrameOf(SurfaceHit hit)
    {
        if (hit.Blockout != null)
        {
            hit.Blockout.Transform.WorldMatrix.Decompose(out _, out Quaternion rotation, out var translation);
            var up = Vector3.Transform(Vector3.UnitY, rotation);
            if (up.Y > 0.999f)
            {
                var x = Vector3.Transform(Vector3.UnitX, rotation);
                return new Frame(new Vector3(translation.X, 0, translation.Z), MathF.Atan2(-x.Z, x.X));
            }
        }
        return new Frame(Vector3.Zero, 0);
    }

    private static bool IntersectHorizontal(Ray ray, float y, out Vector3 point)
    {
        point = default;
        if (MathF.Abs(ray.Direction.Y) < 1e-5f)
            return false;
        var t = (y - ray.Position.Y) / ray.Direction.Y;
        if (t < 0)
            return false;
        point = ray.Position + ray.Direction * t;
        return true;
    }

    // Height along the vertical line through a point closest to the ray
    private static float? VerticalParameter(Ray ray, Vector3 point) => ClosestOnLine(ray, point, Vector3.UnitY);

    /// <summary>
    /// The parameter of the point of a line (origin, unit direction) closest to a ray, or null when they are parallel.
    /// </summary>
    private static float? ClosestOnLine(Ray ray, Vector3 origin, Vector3 direction)
    {
        var w = origin - ray.Position;
        var b = Vector3.Dot(direction, ray.Direction);
        var denominator = 1 - b * b;
        if (denominator < 1e-6f)
            return null;
        var d = Vector3.Dot(direction, w);
        var e = Vector3.Dot(ray.Direction, w);
        return (b * e - d) / denominator;
    }

    #endregion

    #region Resizing

    private static Vector3 FaceCenter(BlockoutHandle handle, Vector3 size) => handle switch
    {
        BlockoutHandle.Right => new Vector3(size.X * 0.5f, size.Y * 0.5f, 0),
        BlockoutHandle.Left => new Vector3(-size.X * 0.5f, size.Y * 0.5f, 0),
        BlockoutHandle.Top => new Vector3(0, size.Y, 0),
        BlockoutHandle.Back => new Vector3(0, size.Y * 0.5f, size.Z * 0.5f),
        BlockoutHandle.Front => new Vector3(0, size.Y * 0.5f, -size.Z * 0.5f),
        _ => Vector3.Zero,
    };

    private static float HandleSize(IEditorGameCameraService camera, Vector3 position)
    {
        var distance = Vector3.Distance(camera.Position, position);
        return Math.Max(distance * MathF.Tan(MathUtil.DegreesToRadians(camera.VerticalFieldOfView) * 0.5f) * 0.035f, 0.02f);
    }

    private void ShowHandles(Entity entity, IEditorGameCameraService camera)
    {
        var shape = entity.Get<BlockoutComponent>();
        entity.Transform.UpdateWorldMatrix();
        var world = entity.Transform.WorldMatrix;
        world.Decompose(out _, out Quaternion rotation, out _);
        var size = shape.ValidSize;
        for (int i = 0; i < handles.Length; i++)
        {
            var handle = (BlockoutHandle)i;
            var position = Vector3.TransformCoordinate(FaceCenter(handle, size), world);
            handles[i].Show(position, rotation, HandleSize(camera, position), handle == hoveredHandle || handle == dragHandle);
        }

        // The box of the shape
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3((i & 1) != 0 ? size.X * 0.5f : -size.X * 0.5f, (i & 2) != 0 ? size.Y : 0, (i & 4) != 0 ? size.Z * 0.5f : -size.Z * 0.5f);
            corners[i] = Vector3.TransformCoordinate(local, world);
        }
        foreach (var (i, j) in BoxEdges)
            outlineLines.Line(corners[i], corners[j]);
    }

    private void HideHandles()
    {
        foreach (var handle in handles)
            handle.Hide();
        hoveredHandle = BlockoutHandle.None;
    }

    private BlockoutHandle HitHandle(Entity entity, Ray ray, IEditorGameCameraService camera)
    {
        var shape = entity.Get<BlockoutComponent>();
        entity.Transform.UpdateWorldMatrix();
        var world = entity.Transform.WorldMatrix;
        var best = BlockoutHandle.None;
        var bestDistance = float.MaxValue;
        for (int i = 0; i < handles.Length; i++)
        {
            var position = Vector3.TransformCoordinate(FaceCenter((BlockoutHandle)i, shape.ValidSize), world);
            var sphere = new BoundingSphere(position, HandleSize(camera, position) * 1.1f);
            if (ray.Intersects(ref sphere, out float distance) && distance < bestDistance)
            {
                bestDistance = distance;
                best = (BlockoutHandle)i;
            }
        }
        return best;
    }

    private void BeginResize(Entity entity, BlockoutHandle handle, Ray ray)
    {
        var shape = entity.Get<BlockoutComponent>();
        dragHandle = handle;
        dragEntity = entity;
        dragStartSize = shape.Size;
        dragStartPosition = entity.Transform.Position;
        GetHandleAxis(entity, handle, out var origin, out var direction, out _);
        dragStartParameter = ClosestOnLine(ray, origin, direction) ?? 0;
    }

    private void GetHandleAxis(Entity entity, BlockoutHandle handle, out Vector3 origin, out Vector3 direction, out float scale)
    {
        entity.Transform.UpdateWorldMatrix();
        var world = entity.Transform.WorldMatrix;
        var axis = Vector3.TransformNormal(HandleAxes[(int)handle], world);
        scale = Math.Max(axis.Length(), 1e-6f);
        direction = axis / scale;
        origin = Vector3.TransformCoordinate(FaceCenter(handle, dragStartSize), world);
    }

    private void UpdateResize(Ray ray, bool snap, float grid, bool symmetric)
    {
        // The axis of the handle as it was when the drag started
        var transform = dragEntity.Transform;
        var position = transform.Position;
        transform.Position = dragStartPosition;
        GetHandleAxis(dragEntity, dragHandle, out var origin, out var direction, out var scale);
        transform.Position = position;
        var parameter = ClosestOnLine(ray, origin, direction);
        if (!parameter.HasValue)
            return;
        var delta = (parameter.Value - dragStartParameter) / scale;
        ApplyResize(dragEntity, dragHandle, dragStartSize, dragStartPosition, delta, symmetric, snap ? grid : 0);
    }

    /// <summary>
    /// Resizes a shape by moving one of its faces along its axis (or both opposite faces when symmetric), the opposite face staying
    /// in place. The size snaps to the grid when <paramref name="grid"/> is positive.
    /// </summary>
    private static void ApplyResize(Entity entity, BlockoutHandle handle, Vector3 startSize, Vector3 startPosition, float delta, bool symmetric, float grid)
    {
        var shape = entity.Get<BlockoutComponent>();
        var axisIndex = handle switch { BlockoutHandle.Right or BlockoutHandle.Left => 0, BlockoutHandle.Top => 1, _ => 2 };
        var start = startSize[axisIndex];
        var raw = start + (symmetric ? 2 * delta : delta);
        var size = grid > 0 ? Math.Max(MathF.Round(raw / grid) * grid, grid) : Math.Max(raw, BlockoutShape.MinSize);
        var change = size - start;

        // How far the center moves (in the shape's axes): half the change toward the moved face; the bottom stays for the top handle
        var shift = Vector3.Zero;
        if (!symmetric && handle != BlockoutHandle.Top)
            shift = HandleAxes[(int)handle] * (change * 0.5f);
        else if (symmetric && handle == BlockoutHandle.Top)
            shift = new Vector3(0, -change * 0.5f, 0);

        var newSize = startSize;
        newSize[axisIndex] = size;
        shape.Size = newSize;
        var transform = entity.Transform;
        entity.Transform.Position = startPosition + Vector3.Transform(shift * transform.Scale, transform.Rotation);
    }

    private void EndResize()
    {
        var entity = dragEntity;
        var shape = entity.Get<BlockoutComponent>();
        var size = shape.Size;
        var position = entity.Transform.Position;
        var changed = size != dragStartSize || position != dragStartPosition;
        dragHandle = BlockoutHandle.None;
        dragEntity = null;
        var tools = ToolsViewModel;
        if (changed && tools != null)
            editor.Dispatcher.InvokeAsync(() => tools.CommitResize(entity.Id, size, position));
    }

    private void CancelResize()
    {
        if (dragEntity != null)
        {
            var shape = dragEntity.Get<BlockoutComponent>();
            if (shape != null)
                shape.Size = dragStartSize;
            dragEntity.Transform.Position = dragStartPosition;
        }
        dragHandle = BlockoutHandle.None;
        dragEntity = null;
    }

    #endregion

    #region Surfaces

    /// <summary>
    /// The closest surface under a ray: blockout shapes, colliders (terrain, physics), else the ground plane (y = 0).
    /// </summary>
    private bool RaycastSurface(Ray ray, float maxDistance, Entity exclude, out SurfaceHit hit)
    {
        hit = default;
        var found = false;
        var best = maxDistance;

        // Blockout shapes (their own triangles: the physics shapes don't follow edits made in the editor)
        var processor = game.SceneSystem.SceneInstance?.Processors.Get<BlockoutProcessor>();
        if (processor != null)
        {
            foreach (var shape in processor.Shapes)
            {
                var entity = shape.Entity;
                if (entity == preview || entity == exclude || !shape.Enabled || IsDescendant(entity, exclude))
                    continue;
                if (RaycastShape(shape, ray, out var point, out var normal))
                {
                    var distance = Vector3.Distance(ray.Position, point);
                    if (distance < best)
                    {
                        best = distance;
                        hit = new SurfaceHit(point, normal, distance, entity);
                        found = true;
                    }
                }
            }
        }

        // Other colliders: terrains, static meshes
        var configuration = game.Services.GetService<BepuConfiguration>();
        if (configuration != null)
        {
            var hits = new List<HitInfo>();
            foreach (var simulation in configuration.BepuSimulations)
            {
                hits.Clear();
                try
                {
                    simulation.RayCastPenetrating(ray.Position, ray.Direction, best, hits);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var info in hits)
                {
                    var entity = info.Collidable?.Entity;
                    if (entity == null || entity.Get<BlockoutComponent>() != null || info.Distance >= best)
                        continue;
                    best = info.Distance;
                    hit = new SurfaceHit(info.Point, info.Normal, info.Distance, null);
                    found = true;
                }
            }
        }

        if (!found && IntersectHorizontal(ray, 0, out var ground) && Vector3.Distance(ray.Position, ground) < maxDistance)
        {
            hit = new SurfaceHit(ground, Vector3.UnitY, Vector3.Distance(ray.Position, ground), null);
            found = true;
        }
        return found;
    }

    private static bool IsDescendant(Entity entity, Entity ancestor)
    {
        if (ancestor == null)
            return false;
        for (var parent = entity.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent == ancestor)
                return true;
        }
        return false;
    }

    private static bool RaycastShape(BlockoutComponent shape, Ray ray, out Vector3 point, out Vector3 normal)
    {
        point = normal = default;
        var transform = shape.Entity.Transform;
        transform.UpdateWorldMatrix();
        var world = transform.WorldMatrix;
        var inverse = Matrix.Invert(world);
        var localRay = new Ray(Vector3.TransformCoordinate(ray.Position, inverse), Vector3.TransformNormal(ray.Direction, inverse));
        if (!shape.GetGeometry().Raycast(localRay, out var t, out var localNormal))
            return false;
        point = Vector3.TransformCoordinate(localRay.Position + localRay.Direction * t, world);
        normal = Vector3.Normalize(Vector3.TransformNormal(localNormal, Matrix.Transpose(inverse)));
        return true;
    }

    #endregion

    #region Commands and automation

    /// <summary>
    /// Positions that put entities on the surface below them (the entity itself and its children aside): the bottom of a blockout
    /// shape, the origin of other entities. Entities with nothing below are left out.
    /// </summary>
    public Task<Dictionary<Guid, Vector3>> ComputeDropPositions(IReadOnlyCollection<Guid> entityIds)
    {
        return controller.InvokeAsync(() =>
        {
            var result = new Dictionary<Guid, Vector3>();
            foreach (var id in entityIds)
            {
                if (controller.FindGameSidePart(new AbsoluteId(editor.Asset.Id, id)) is not Entity entity)
                    continue;
                entity.Transform.UpdateWorldMatrix();
                var origin = entity.Transform.WorldMatrix.TranslationVector;
                var ray = new Ray(origin + new Vector3(0, 0.05f, 0), -Vector3.UnitY);
                if (!RaycastSurface(ray, 10000, entity, out var hit))
                    continue;
                var worldTarget = origin with { Y = hit.Point.Y };
                var parent = entity.GetParent();
                var local = worldTarget;
                if (parent != null)
                {
                    parent.Transform.UpdateWorldMatrix();
                    local = Vector3.TransformCoordinate(worldTarget, Matrix.Invert(parent.Transform.WorldMatrix));
                }
                result[id] = local;
            }
            return result;
        });
    }

    /// <summary>
    /// Runs an action on the game thread (the thread of the viewport's window and its input), for tests and scripts.
    /// </summary>
    public Task InvokeOnGameThread(Action action) => controller.InvokeAsync(action);

    /// <summary>
    /// The surface under a ray (game thread work; for tests and scripts).
    /// </summary>
    public Task<SurfaceHit?> Raycast(Ray ray, float maxDistance = 10000)
    {
        return controller.InvokeAsync(() => RaycastSurface(ray, maxDistance, null, out var hit) ? hit : (SurfaceHit?)null);
    }

    /// <summary>
    /// Computes the shape the tool would draw by dragging from a point to another of the surface under the first one, up to a
    /// height (as with the mouse, on the shape's grid when it starts on a shape), without creating it. <paramref name="square"/> keeps
    /// the footprint as wide as deep (Ctrl).
    /// </summary>
    public Task<BlockoutPlacementResult?> ComputeDrawing(BlockoutShapeItemViewModel item, Vector3 start, Vector3 end, float shapeHeight, int turns = 0, bool square = false)
    {
        return controller.InvokeAsync(() =>
        {
            var current = GetSettings();
            var ray = new Ray(start + new Vector3(0, 1000, 0), -Vector3.UnitY);
            if (!RaycastSurface(ray, 5000, null, out var hit))
                return (BlockoutPlacementResult?)null;
            var drawFrame = FrameOf(hit);
            var grid = current.Snap ? Math.Max(current.GridSize, 0.01f) : 0;
            var a = SnapPoint(drawFrame.ToLocal(hit.Point), grid > 0, grid);
            var b = SnapPoint(drawFrame.ToLocal(end), grid > 0, grid);
            if (square)
                b = SquareCorner(a, b);
            return ComputePlacement(item, drawFrame, a, b, hit.Point.Y, shapeHeight, turns, grid);
        });
    }

    /// <summary>
    /// Resizes a shape as dragging one of its handles by a distance would (and records it), for tests and scripts.
    /// </summary>
    public async Task<bool> SimulateResize(Guid entityId, BlockoutHandle handle, float delta, bool symmetric = false)
    {
        var result = await controller.InvokeAsync(() =>
        {
            if (controller.FindGameSidePart(new AbsoluteId(editor.Asset.Id, entityId)) is not Entity entity || entity.Get<BlockoutComponent>() is not { } shape)
                return ((Vector3, Vector3)?)null;
            var current = GetSettings();
            ApplyResize(entity, handle, shape.Size, entity.Transform.Position, delta, symmetric, current.Snap ? current.GridSize : 0);
            return (shape.Size, entity.Transform.Position);
        });
        if (result == null)
            return false;
        var tools = ToolsViewModel;
        if (tools != null)
            await editor.Dispatcher.InvokeAsync(() => tools.CommitResize(entityId, result.Value.Item1, result.Value.Item2));
        return true;
    }

    /// <summary>
    /// Where a handle of the selected shape is in the viewport (0 to 1 from the top left), or null (for tests).
    /// </summary>
    public Task<Vector2?> GetHandleViewportPosition(BlockoutHandle handle)
    {
        return controller.InvokeAsync(() =>
        {
            var anchor = selection.LastOrDefault(x => x.Get<BlockoutComponent>() != null && x.Scene != null);
            var camera = game.EditorServices.Get<IEditorGameCameraService>();
            if (anchor == null || camera == null || handle == BlockoutHandle.None)
                return (Vector2?)null;
            anchor.Transform.UpdateWorldMatrix();
            var world = Vector3.TransformCoordinate(FaceCenter(handle, anchor.Get<BlockoutComponent>().ValidSize), anchor.Transform.WorldMatrix);
            var clip = Vector3.TransformCoordinate(world, camera.ViewMatrix * camera.ProjectionMatrix);
            return new Vector2((clip.X + 1) * 0.5f, (1 - clip.Y) * 0.5f);
        });
    }

    /// <summary>
    /// The game-side size of a shape (for tests).
    /// </summary>
    public Task<(Vector3 Size, Vector3 Position, int Triangles)?> GetShapeState(Guid entityId)
    {
        return controller.InvokeAsync(() =>
        {
            if (controller.FindGameSidePart(new AbsoluteId(editor.Asset.Id, entityId)) is not Entity entity || entity.Get<BlockoutComponent>() is not { } shape)
                return ((Vector3, Vector3, int)?)null;
            return (shape.Size, entity.Transform.Position, shape.GetGeometry().TriangleCount);
        });
    }

    #endregion
}

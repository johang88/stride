// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.EntityFactories;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.BepuPhysics;
using Stride.Blockout.Physics;
using Stride.Blockout.Rendering;
using Stride.Blockout.Shapes;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Quantum;
using Stride.Engine;

namespace Stride.Blockout.Editor;

/// <summary>
/// A color of the quick palette of the blockout tools.
/// </summary>
public sealed record BlockoutColorItem(string Name, Color3 Color)
{
    public Color4 Display => new(Color, 1);
}

/// <summary>
/// The level blockout tools of a scene editor: draw shapes in the viewport, resize them with handles, give them a pattern and
/// a color.
/// </summary>
[EntityHierarchyEditorTool(typeof(EntityHierarchyEditorViewModel), Order = 10)]
public sealed class BlockoutToolsViewModel : EntityHierarchyEditorToolViewModel
{
    /// <summary>
    /// The folder of the scene hierarchy new shapes go to.
    /// </summary>
    public const string FolderName = "Blockout";

    private readonly IEditorGameController controller;
    private readonly EntityHierarchyEditorViewModel editor;
    private readonly ObservableList<BlockoutPresetViewModel> presets = new();
    private EditorGameBlockoutService service;
    private BlockoutShapeItemViewModel selectedShape;
    private BlockoutPresetViewModel selectedPreset;
    private BlockoutStyle currentStyle;
    private float gridSize = 0.5f;
    private bool snap = true;
    private string selectionInfo;
    private int presetChangeVersion;
    private readonly HashSet<string> pendingPresetNames = new();
    private bool loadingPresets;

    public BlockoutToolsViewModel([NotNull] IViewModelServiceProvider serviceProvider, [NotNull] IEditorGameController controller, [NotNull] EntityHierarchyEditorViewModel editor)
        : base(serviceProvider)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Shapes = BlockoutPalette.CreateItems();
        selectedShape = Shapes[0];

        ApplyPresetCommand = new AnonymousCommand<BlockoutPresetViewModel>(ServiceProvider, ApplyPreset);
        SetPatternCommand = new AnonymousCommand<BlockoutPattern>(ServiceProvider, SetPattern);
        SetColorCommand = new AnonymousCommand<BlockoutColorItem>(ServiceProvider, x => SetColor(x.Color));
        AddPresetCommand = new AnonymousCommand(ServiceProvider, AddPreset);
        RemovePresetCommand = new AnonymousCommand(ServiceProvider, RemovePreset);
        DropToGroundCommand = new AnonymousTaskCommand(ServiceProvider, DropSelectionToGround);
        RotateSelectionCommand = new AnonymousCommand(ServiceProvider, () => RotateSelection(90));
        PickStyleFromSelectionCommand = new AnonymousCommand(ServiceProvider, PickStyleFromSelection);

        LoadPresets();
        currentStyle = presets.FirstOrDefault()?.ToStyle() ?? new BlockoutStyle();
        selectedPreset = presets.FirstOrDefault();
        UpdateCurrentPresetMarks();

        editor.SelectedContent.CollectionChanged += OnSelectionChanged;
        editor.Asset.PropertyGraph.Changed += OnAssetChanged;
        editor.Asset.PropertyGraph.ItemChanged += OnAssetChanged;
        UpdateSelectionInfo();

        Dispatcher.InvokeTask(async () =>
        {
            await controller.GameContentLoaded;
            service = controller.GetService<EditorGameBlockoutService>();
            if (service != null)
            {
                service.ToolsViewModel = this;
                PushSettings();
            }
            OnPropertyChanged(nameof(IsAvailable));
        });
    }

    public override string DisplayName => "Blockout";

    public override string ToolTip => "Block out levels: draw simple shapes with collisions, give them a pattern and a color";

    public override string IconResourceKey => "BlockoutIcon.Tool";

    public override bool IsAvailable => service != null;

    /// <summary>
    /// The editor game service of the tools (null until the scene is loaded).
    /// </summary>
    public EditorGameBlockoutService Service => service;

    protected override void OnIsActiveChanged()
    {
        PushSettings();
    }

    #region Shapes and grid

    public IReadOnlyList<BlockoutShapeItemViewModel> Shapes { get; }

    public BlockoutShapeItemViewModel SelectedShape
    {
        get => selectedShape;
        set => SetValue(ref selectedShape, value ?? selectedShape, PushSettings, nameof(SelectedShape), nameof(Hint));
    }

    public IReadOnlyList<float> GridSizes { get; } = new[] { 0.125f, 0.25f, 0.5f, 1.0f, 2.0f, 4.0f };

    /// <summary>
    /// The grid spacing of the positions and sizes, in meters.
    /// </summary>
    public float GridSize { get => gridSize; set => SetValue(ref gridSize, Math.Clamp(value, 0.01f, 100), PushSettings); }

    /// <summary>
    /// Whether positions and sizes snap to the grid (Shift inverts it while drawing and resizing).
    /// </summary>
    public bool Snap { get => snap; set => SetValue(ref snap, value, PushSettings); }

    public string Hint => $"Drag on the ground, the terrain or a shape to draw a {selectedShape?.Name.ToLowerInvariant()}. Release, move the mouse up or down for the height, click to finish. "
        + "T turns it, Esc cancels, Shift toggles snapping. A click selects as usual: drag the handles of a selected shape to resize it (Ctrl: both sides).";

    #endregion

    #region Styles

    public IReadOnlyList<BlockoutPresetViewModel> Presets => presets;

    /// <summary>
    /// The preset last applied, edited below the presets.
    /// </summary>
    public BlockoutPresetViewModel SelectedPreset
    {
        get => selectedPreset;
        set => SetValue(ref selectedPreset, value, UpdateCurrentPresetMarks, nameof(SelectedPreset), nameof(HasSelectedPreset));
    }

    public bool HasSelectedPreset => selectedPreset != null;

    /// <summary>
    /// The style of new shapes.
    /// </summary>
    public BlockoutStyle CurrentStyle => currentStyle;

    public BlockoutLook CurrentLook => currentStyle.Look;

    public string CurrentStyleName => currentStyle.Preset ?? $"{currentStyle.Pattern}, {FormatColor(currentStyle.Color)}";

    public IReadOnlyList<BlockoutPattern> Patterns { get; } = Enum.GetValues<BlockoutPattern>();

    public IReadOnlyList<BlockoutColorItem> Colors { get; } = new BlockoutColorItem[]
    {
        new("White", new Color3(0.9f, 0.9f, 0.88f)),
        new("Light gray", new Color3(0.7f, 0.7f, 0.7f)),
        new("Gray", new Color3(0.5f, 0.5f, 0.5f)),
        new("Dark gray", new Color3(0.28f, 0.28f, 0.3f)),
        new("Black", new Color3(0.1f, 0.1f, 0.11f)),
        new("Sand", new Color3(0.8f, 0.7f, 0.5f)),
        new("Brown", new Color3(0.5f, 0.34f, 0.2f)),
        new("Red", new Color3(0.78f, 0.2f, 0.17f)),
        new("Orange", new Color3(0.93f, 0.5f, 0.15f)),
        new("Yellow", new Color3(0.95f, 0.8f, 0.15f)),
        new("Green", new Color3(0.35f, 0.62f, 0.25f)),
        new("Teal", new Color3(0.15f, 0.55f, 0.55f)),
        new("Blue", new Color3(0.2f, 0.45f, 0.82f)),
        new("Purple", new Color3(0.5f, 0.3f, 0.7f)),
        new("Pink", new Color3(0.9f, 0.5f, 0.65f)),
    };

    public ICommandBase ApplyPresetCommand { get; }

    public ICommandBase SetPatternCommand { get; }

    public ICommandBase SetColorCommand { get; }

    public ICommandBase AddPresetCommand { get; }

    public ICommandBase RemovePresetCommand { get; }

    public ICommandBase PickStyleFromSelectionCommand { get; }

    /// <summary>
    /// The file the presets are saved to.
    /// </summary>
    public string PresetsPath => BlockoutStyleLibrary.GetPath(editor.Asset.Directory?.Package?.Package);

    /// <summary>
    /// Makes a preset the style of new shapes and applies it to the selected shapes.
    /// </summary>
    public void ApplyPreset(BlockoutPresetViewModel preset)
    {
        if (preset == null)
            return;
        SelectedPreset = preset;
        SetCurrentStyle(preset.ToStyle());
        ApplyStyle(SelectedShapes(), _ => preset.ToStyle(), $"Apply style {preset.Name}");
    }

    /// <summary>
    /// Sets the pattern of new shapes and of the selected ones (their colors stay).
    /// </summary>
    public void SetPattern(BlockoutPattern pattern)
    {
        var style = currentStyle.Clone();
        style.Pattern = pattern;
        style.Preset = null;
        SetCurrentStyle(style);
        ApplyStyle(SelectedShapes(), x =>
        {
            var changed = (x ?? new BlockoutStyle()).Clone();
            changed.Pattern = pattern;
            changed.Preset = null;
            return changed;
        }, $"Set pattern {pattern}");
    }

    /// <summary>
    /// Sets the color of new shapes and of the selected ones (their patterns stay).
    /// </summary>
    public void SetColor(Color3 color)
    {
        var style = currentStyle.Clone();
        style.Color = color;
        style.Preset = null;
        SetCurrentStyle(style);
        ApplyStyle(SelectedShapes(), x =>
        {
            var changed = (x ?? new BlockoutStyle()).Clone();
            changed.Color = color;
            changed.Preset = null;
            return changed;
        }, "Set color");
    }

    private void SetCurrentStyle(BlockoutStyle style)
    {
        currentStyle = style;
        OnPropertyChanged(nameof(CurrentStyle), nameof(CurrentLook), nameof(CurrentStyleName));
        UpdateCurrentPresetMarks();
        PushSettings();
    }

    private void UpdateCurrentPresetMarks()
    {
        foreach (var preset in presets)
            preset.IsCurrent = preset == selectedPreset;
    }

    // Takes the style of the last selected shape for new shapes
    private void PickStyleFromSelection()
    {
        var shape = SelectedShapes().LastOrDefault();
        if (shape?.AssetSideEntity.Get<BlockoutComponent>()?.Style is { } style)
        {
            SetCurrentStyle(style.Clone());
            if (style.Preset != null && presets.FirstOrDefault(x => x.Name == style.Preset) is { } preset)
                SelectedPreset = preset;
        }
    }

    private void AddPreset()
    {
        var style = currentStyle.Clone();
        var name = style.Preset ?? "Style";
        var baseName = name;
        for (int i = 2; presets.Any(x => x.Name == name); i++)
            name = $"{baseName} {i}";
        style.Preset = name;
        var preset = new BlockoutPresetViewModel(ServiceProvider, style, OnPresetChanged);
        presets.Add(preset);
        SelectedPreset = preset;
        SetCurrentStyle(preset.ToStyle());
        SavePresets();
    }

    private void RemovePreset()
    {
        if (selectedPreset == null || presets.Count <= 1)
            return;
        var index = presets.IndexOf(selectedPreset);
        presets.Remove(selectedPreset);
        SelectedPreset = presets[Math.Min(index, presets.Count - 1)];
        SavePresets();
    }

    private void LoadPresets()
    {
        loadingPresets = true;
        presets.Clear();
        var path = PresetsPath;
        foreach (var style in BlockoutStyleLibrary.Load(path, e => editor.Session.ServiceProvider.Get<IEditorDialogService>()?.MessageBoxAsync($"The blockout styles could not be read from {path}: {e.Message}")))
            presets.Add(new BlockoutPresetViewModel(ServiceProvider, style, OnPresetChanged));
        loadingPresets = false;
    }

    private void SavePresets()
    {
        var path = PresetsPath;
        if (path == null)
            return;
        try
        {
            BlockoutStyleLibrary.Save(path, presets.Select(x => x.ToStyle()));
        }
        catch (Exception e)
        {
            editor.Session.ServiceProvider.Get<IEditorDialogService>()?.MessageBoxAsync($"The blockout styles could not be saved to {path}: {e.Message}");
        }
    }

    // A preset was edited in the panel: once the edits settle, save the presets and restyle the shapes that use it
    private void OnPresetChanged(BlockoutPresetViewModel preset, string oldName)
    {
        if (loadingPresets)
            return;
        pendingPresetNames.Add(oldName);
        if (preset == selectedPreset && (currentStyle.Preset == oldName || currentStyle.Preset == preset.Name))
            SetCurrentStyle(preset.ToStyle());
        var version = ++presetChangeVersion;
        Dispatcher.InvokeTask(async () =>
        {
            await Task.Delay(400);
            if (version != presetChangeVersion)
                return;
            SavePresets();
            var names = pendingPresetNames.ToList();
            pendingPresetNames.Clear();
            var updates = new Dictionary<string, BlockoutPresetViewModel>();
            foreach (var name in names)
            {
                var target = presets.FirstOrDefault(x => x.Name == name) ?? (name == oldName ? preset : null);
                if (target != null)
                    updates[name] = target;
            }
            var shapes = AllShapes().Where(x => x.AssetSideEntity.Get<BlockoutComponent>()?.Style?.Preset is { } name && updates.ContainsKey(name)).ToList();
            ApplyStyle(shapes, x => updates[x.Preset].ToStyle(), "Update blockout style");
        });
    }

    #endregion

    #region Selection

    public string SelectionInfo { get => selectionInfo; private set => SetValue(ref selectionInfo, value); }

    public ICommandBase DropToGroundCommand { get; }

    public ICommandBase RotateSelectionCommand { get; }

    private void OnSelectionChanged(object sender, NotifyCollectionChangedEventArgs e) => UpdateSelectionInfo();

    private void OnAssetChanged(object sender, EventArgs e) => UpdateSelectionInfo();

    private void UpdateSelectionInfo()
    {
        var shapes = SelectedShapes().ToList();
        if (shapes.Count == 0)
        {
            SelectionInfo = "No shape selected";
            return;
        }
        if (shapes.Count > 1)
        {
            SelectionInfo = $"{shapes.Count} shapes selected";
            return;
        }
        var component = shapes[0].AssetSideEntity.Get<BlockoutComponent>();
        var kind = component.Shape?.GetType().Name.Replace("Blockout", "") ?? "Shape";
        var size = component.Size;
        var style = component.Style?.Preset != null ? $", {component.Style.Preset}" : "";
        SelectionInfo = string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.##} × {2:0.##} × {3:0.##} m{4}", kind, size.X, size.Y, size.Z, style);
    }

    /// <summary>
    /// The selected entities that are blockout shapes.
    /// </summary>
    public IEnumerable<EntityViewModel> SelectedShapes() => editor.SelectedContent.OfType<EntityViewModel>().Where(x => x.AssetSideEntity.Get<BlockoutComponent>() != null);

    private IEnumerable<EntityViewModel> AllShapes() => Flatten(editor.HierarchyRoot).Where(x => x.AssetSideEntity.Get<BlockoutComponent>() != null);

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

    private async Task DropSelectionToGround()
    {
        if (service == null)
            return;
        var entities = editor.SelectedContent.OfType<EntityViewModel>().ToList();
        if (entities.Count == 0)
            return;
        var positions = await service.ComputeDropPositions(entities.Select(x => x.Id.ObjectId).ToList());
        if (positions.Count == 0)
            return;
        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using var transaction = undoRedo.CreateTransaction();
        foreach (var entity in entities)
        {
            if (positions.TryGetValue(entity.Id.ObjectId, out var position))
                UpdateMember(entity.AssetSideEntity.Transform, nameof(TransformComponent.Position), position);
        }
        undoRedo.SetName(transaction, "Drop to ground");
    }

    /// <summary>
    /// Turns the selected entities around the vertical axis.
    /// </summary>
    public void RotateSelection(float degrees)
    {
        var entities = editor.SelectedContent.OfType<EntityViewModel>().ToList();
        if (entities.Count == 0)
            return;
        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using var transaction = undoRedo.CreateTransaction();
        foreach (var entity in entities)
        {
            var rotation = entity.AssetSideEntity.Transform.Rotation;
            var turned = Quaternion.RotationMatrix(Matrix.RotationQuaternion(rotation) * Matrix.RotationY(MathUtil.DegreesToRadians(degrees)));
            UpdateMember(entity.AssetSideEntity.Transform, nameof(TransformComponent.Rotation), turned);
        }
        undoRedo.SetName(transaction, "Turn selection");
    }

    #endregion

    #region Edits

    /// <summary>
    /// Adds a shape drawn in the viewport to the scene (one undoable edit), with a static collider, and selects it.
    /// </summary>
    public Guid CreateShape(BlockoutPlacementResult placement, BlockoutStyle style)
    {
        var root = editor.HierarchyRoot;
        var name = placement.Item.Name;
        var entity = new Entity(EntityFactory.ComputeNewName(root, name))
        {
            new BlockoutComponent { Shape = placement.Item.CreateShape(), Size = placement.Size, Style = style ?? currentStyle.Clone() },
            new StaticComponent { Collider = new BlockoutCollider() },
        };
        entity.Transform.Position = placement.Position;
        entity.Transform.Rotation = placement.Rotation;

        var collection = new AssetPartCollection<EntityDesign, Entity>();
        AssetCollectionItemIdHelper.GenerateMissingItemIds(entity);
        collection.Add(new EntityDesign(entity, FolderName));

        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using (var transaction = undoRedo.CreateTransaction())
        {
            root.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(collection, collection[entity.Id], null, root.Owner.EntityCount);
            undoRedo.SetName(transaction, $"Draw {name.ToLowerInvariant()}");
        }

        if (editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, entity.Id)) is EntityViewModel created)
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(created);
        }
        return entity.Id;
    }

    /// <summary>
    /// Records a resize made with the handles (one undoable edit).
    /// </summary>
    public void CommitResize(Guid entityId, Vector3 size, Vector3 position)
    {
        if (editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, entityId)) is not EntityViewModel entity)
            return;
        var component = entity.AssetSideEntity.Get<BlockoutComponent>();
        if (component == null)
            return;
        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using (var transaction = undoRedo.CreateTransaction())
        {
            UpdateMember(component, nameof(BlockoutComponent.Size), size);
            UpdateMember(entity.AssetSideEntity.Transform, nameof(TransformComponent.Position), position);
            undoRedo.SetName(transaction, "Resize shape");
        }
        UpdateSelectionInfo();
    }

    private void ApplyStyle(IEnumerable<EntityViewModel> shapes, Func<BlockoutStyle, BlockoutStyle> createStyle, string actionName)
    {
        var targets = shapes.ToList();
        if (targets.Count == 0)
            return;
        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using (var transaction = undoRedo.CreateTransaction())
        {
            foreach (var entity in targets)
            {
                var component = entity.AssetSideEntity.Get<BlockoutComponent>();
                UpdateMember(component, nameof(BlockoutComponent.Style), createStyle(component.Style));
            }
            undoRedo.SetName(transaction, actionName);
        }
        UpdateSelectionInfo();
    }

    private void UpdateMember(object owner, string member, object value)
    {
        var node = editor.Session.AssetNodeContainer.GetNode(owner)[member];
        if (!Equals(node.Retrieve(), value))
            node.Update(value);
    }

    #endregion

    private void PushSettings()
    {
        service?.SetSettings(new BlockoutToolSettings
        {
            DrawEnabled = IsActive,
            Shape = selectedShape,
            Style = currentStyle.Clone(),
            GridSize = gridSize,
            Snap = snap,
        });
    }

    private static string FormatColor(Color3 color)
    {
        var c = (Color)color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public override void Destroy()
    {
        editor.SelectedContent.CollectionChanged -= OnSelectionChanged;
        editor.Asset.PropertyGraph.Changed -= OnAssetChanged;
        editor.Asset.PropertyGraph.ItemChanged -= OnAssetChanged;
        if (service != null)
            service.ToolsViewModel = null;
        base.Destroy();
    }
}

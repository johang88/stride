// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.EntityFactories;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Assets.Quantum;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Terrain.Splines;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Quantum;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain;
using Stride.Terrain.Editing;
using Stride.Terrain.Vegetation;

namespace Stride.Terrain.Editor;

/// <summary>
/// A layer of the target terrain's material.
/// </summary>
public sealed class TerrainLayerItemViewModel
{
    public TerrainLayerItemViewModel(int index, string name)
    {
        Index = index;
        Name = string.IsNullOrEmpty(name) ? $"Layer {index}" : name;
    }

    public int Index { get; }

    public string Name { get; }

    public string DisplayName => $"{Index}: {Name}";
}

/// <summary>
/// The terrain sculpting and painting tools of a scene editor.
/// </summary>
[EntityHierarchyEditorTool(typeof(EntityHierarchyEditorViewModel))]
public sealed class TerrainToolsViewModel : EntityHierarchyEditorToolViewModel
{
    private readonly IEditorGameController controller;
    private readonly EntityHierarchyEditorViewModel editor;
    private EditorGameTerrainService service;
    private string targetName;
    private TerrainBrushTool tool = TerrainBrushTool.Raise;
    private float radius = 10.0f;
    private float strength = 0.5f;
    private float falloff = 0.6f;
    private float flattenHeight;
    private float noiseScale = 8.0f;
    private TerrainLayerItemViewModel selectedLayer;
    private readonly ObservableList<TerrainLayerItemViewModel> layers = new();
    private readonly ObservableList<TerrainBrushItemViewModel> brushes = new();
    private readonly ObservableList<TerrainGrassLayerItemViewModel> grassLayers = new();
    private readonly ObservableList<TerrainVegetationTypeItemViewModel> vegetationTypes = new();
    private TerrainGrassLayerItemViewModel selectedGrassLayer;
    private TerrainVegetationTypeItemViewModel selectedVegetationType;
    // The target terrain's entity, and the grass layer and vegetation type names of its game-side component
    private Guid? targetEntityId;
    private List<string> gameGrassNames = new();
    private List<string> gameVegetationNames = new();
    private bool canEditDefinitions;
    private bool definitionsRefreshQueued;
    private float grassDensity = 1.0f;
    private float vegetationDensity = 2.0f;
    private float vegetationSpacing = 3.0f;
    private float vegetationMinScale = 0.8f;
    private float vegetationMaxScale = 1.2f;
    private float vegetationMaxSlope = 35.0f;
    private bool impostorPreview;
    private bool refreshingInfo;
    private TerrainBrushItemViewModel selectedBrush;
    private float rotation;

    public TerrainToolsViewModel([NotNull] IViewModelServiceProvider serviceProvider, [NotNull] IEditorGameController controller, [NotNull] EntityHierarchyEditorViewModel editor)
        : base(serviceProvider)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.editor = editor ?? throw new ArgumentNullException(nameof(editor));
        NewRoadCommand = new AnonymousTaskCommand(ServiceProvider, () => CreateSpline(TerrainSplineKind.Road));
        NewRiverCommand = new AnonymousTaskCommand(ServiceProvider, () => CreateSpline(TerrainSplineKind.River));
        SnapSplinePointsCommand = new AnonymousTaskCommand(ServiceProvider, () => SnapSplinePoints(GetSelectedSplineIds()));
        ApplySplinesCommand = new AnonymousTaskCommand(ServiceProvider, ApplySplines);
        SelectToolCommand = new AnonymousCommand<TerrainBrushTool>(ServiceProvider, x => Tool = x);
        ImportHeightmapCommand = new AnonymousTaskCommand(ServiceProvider, ImportHeightmap);
        ExportHeightmapCommand = new AnonymousTaskCommand(ServiceProvider, ExportHeightmap);
        ImportBrushCommand = new AnonymousTaskCommand(ServiceProvider, ImportBrush);
        FillGrassFromLayerCommand = new AnonymousCommand(ServiceProvider, () => service?.FillGrassFromLayer(selectedGrassLayer?.Index ?? -1, selectedLayer?.Index ?? 0, grassDensity));
        OpenBrushFolderCommand = new AnonymousCommand(ServiceProvider, OpenBrushFolder);
        AddGrassLayerCommand = new AnonymousTaskCommand(ServiceProvider, PickGrassLayer);
        RemoveGrassLayerCommand = new AnonymousTaskCommand(ServiceProvider, () => RemoveDefinition(grass: true));
        AddVegetationTypesCommand = new AnonymousTaskCommand(ServiceProvider, PickVegetationTypes);
        RemoveVegetationTypeCommand = new AnonymousTaskCommand(ServiceProvider, () => RemoveDefinition(grass: false));
        SelectTerrainCommand = new AnonymousCommand(ServiceProvider, SelectTerrain);
        RefreshBrushes();

        // Grass layers and vegetation types are edited on the scene asset: follow its changes (undo included)
        editor.Asset.PropertyGraph.Changed += OnAssetChanged;
        editor.Asset.PropertyGraph.ItemChanged += OnAssetChanged;

        Dispatcher.InvokeTask(async () =>
        {
            await controller.GameContentLoaded;
            service = controller.GetService<EditorGameTerrainService>();
            if (service != null)
            {
                service.ToolsViewModel = this;
                PushSettings();
            }
            OnPropertyChanged(nameof(IsAvailable));
        });
    }

    public override string DisplayName => "Terrain";

    public override string ToolTip => "Sculpt and paint terrains in the viewport";

    public override string IconResourceKey => "TerrainIcon.Tool";

    public override bool IsAvailable => service != null;

    /// <summary>
    /// The editor game service applying the tools (null until the scene is loaded).
    /// </summary>
    public EditorGameTerrainService Service => service;

    /// <inheritdoc/>
    /// <remarks>The terrain tools take the mouse in the viewport while active.</remarks>
    protected override void OnIsActiveChanged()
    {
        // Brush images may have been added to the folder meanwhile
        if (IsActive)
            RefreshBrushes();
        PushSettings();
        if (IsActive && IsVegetationTool)
            RefreshVegetationInfo();
    }

    public string TargetName { get => targetName; private set => SetValue(ref targetName, value, nameof(TargetName), nameof(HasTarget)); }

    public bool HasTarget => targetName != null;

    public IReadOnlyList<TerrainBrushTool> Tools { get; } = Enum.GetValues<TerrainBrushTool>();

    public TerrainBrushTool Tool { get => tool; set => SetValue(ref tool, value, OnToolChanged, nameof(Tool), nameof(IsPaintTool), nameof(IsFlattenTool), nameof(IsNoiseTool), nameof(IsGrassTool), nameof(IsVegetationTool), nameof(ShowsTerrainLayers), nameof(Hint)); }

    public bool IsPaintTool => tool == TerrainBrushTool.Paint;

    public bool IsFlattenTool => tool == TerrainBrushTool.Flatten;

    public bool IsNoiseTool => tool == TerrainBrushTool.Noise;

    public bool IsGrassTool => tool == TerrainBrushTool.Grass;

    public bool IsVegetationTool => tool == TerrainBrushTool.Vegetation;

    /// <summary>
    /// Whether the terrain material layers are listed (painting, and filling grass from a layer).
    /// </summary>
    public bool ShowsTerrainLayers => IsPaintTool || IsGrassTool;

    /// <summary>
    /// The grass layers of the target terrain (its density channels).
    /// </summary>
    public IReadOnlyObservableList<TerrainGrassLayerItemViewModel> GrassLayers => grassLayers;

    public TerrainGrassLayerItemViewModel SelectedGrassLayer { get => selectedGrassLayer; set => SetValue(ref selectedGrassLayer, value, PushSettings); }

    /// <summary>
    /// Whether the grass layers and vegetation types can be edited here: the target terrain is part of the edited scene.
    /// </summary>
    public bool CanEditDefinitions { get => canEditDefinitions; private set => SetValue(ref canEditDefinitions, value); }

    /// <summary>
    /// Density (0-1) the grass tool paints towards (and fills with).
    /// </summary>
    public float GrassDensity { get => grassDensity; set => SetValue(ref grassDensity, Math.Clamp(value, 0.0f, 1.0f), PushSettings); }

    /// <summary>
    /// The vegetation types of the target terrain.
    /// </summary>
    public IReadOnlyObservableList<TerrainVegetationTypeItemViewModel> VegetationTypes => vegetationTypes;

    public TerrainVegetationTypeItemViewModel SelectedVegetationType { get => selectedVegetationType; set => SetValue(ref selectedVegetationType, value, PushSettings); }

    /// <summary>
    /// Instances per 100 m² the vegetation tool fills up to.
    /// </summary>
    public float VegetationDensity { get => vegetationDensity; set => SetValue(ref vegetationDensity, Math.Clamp(value, 0.01f, 1000.0f), PushSettings); }

    /// <summary>
    /// Smallest distance between placed instances (meters).
    /// </summary>
    public float VegetationSpacing { get => vegetationSpacing; set => SetValue(ref vegetationSpacing, Math.Clamp(value, 0.1f, 100.0f), PushSettings); }

    public float VegetationMinScale { get => vegetationMinScale; set => SetValue(ref vegetationMinScale, Math.Clamp(value, 0.01f, 100.0f), PushSettings); }

    public float VegetationMaxScale { get => vegetationMaxScale; set => SetValue(ref vegetationMaxScale, Math.Clamp(value, 0.01f, 100.0f), PushSettings); }

    /// <summary>
    /// Steepest slope instances are placed on (degrees).
    /// </summary>
    public float VegetationMaxSlope { get => vegetationMaxSlope; set => SetValue(ref vegetationMaxSlope, Math.Clamp(value, 0.0f, 90.0f), PushSettings); }

    /// <summary>
    /// Draws the vegetation as impostors at any distance (to check them).
    /// </summary>
    public bool ImpostorPreview { get => impostorPreview; set => SetValue(ref impostorPreview, value, () => service?.SetImpostorPreview(value)); }

    public ICommandBase FillGrassFromLayerCommand { get; }

    /// <summary>
    /// Adds a grass layer growing the models picked.
    /// </summary>
    public ICommandBase AddGrassLayerCommand { get; }

    /// <summary>
    /// Removes the selected grass layer and its painted density (the later layers move down one).
    /// </summary>
    public ICommandBase RemoveGrassLayerCommand { get; }

    /// <summary>
    /// Adds a vegetation type per model picked.
    /// </summary>
    public ICommandBase AddVegetationTypesCommand { get; }

    /// <summary>
    /// Removes the selected vegetation type and its placed instances (the later types move down one).
    /// </summary>
    public ICommandBase RemoveVegetationTypeCommand { get; }

    /// <summary>
    /// Selects the target terrain's entity, to see all its settings in the property grid.
    /// </summary>
    public ICommandBase SelectTerrainCommand { get; }

    internal SessionViewModel Session => editor.Session;

    public float Radius { get => radius; set => SetValue(ref radius, Math.Clamp(value, 0.5f, 500.0f), PushSettings); }

    public float Strength { get => strength; set => SetValue(ref strength, Math.Clamp(value, 0.0f, 1.0f), PushSettings); }

    public float Falloff { get => falloff; set => SetValue(ref falloff, Math.Clamp(value, 0.0f, 1.0f), PushSettings); }

    /// <summary>
    /// The brushes: built-in shapes, then the brush images of the project (see <see cref="TerrainBrushLibrary"/>).
    /// </summary>
    public IReadOnlyObservableList<TerrainBrushItemViewModel> Brushes => brushes;

    public TerrainBrushItemViewModel SelectedBrush { get => selectedBrush; set => SetValue(ref selectedBrush, value, PushSettings, nameof(SelectedBrush), nameof(IsCustomBrush), nameof(Hint)); }

    /// <summary>
    /// Whether the brush has a custom shape (rotation applies, falloff doesn't).
    /// </summary>
    public bool IsCustomBrush => selectedBrush?.IsCustom == true;

    /// <summary>
    /// Rotation of a custom brush in degrees (counter-clockwise seen from above).
    /// </summary>
    public float Rotation { get => rotation; set => SetValue(ref rotation, ((value % 360) + 360) % 360, PushSettings); }

    /// <summary>
    /// The folder of the project's brush images, in the resources of the edited scene's package.
    /// </summary>
    public string BrushFolder => TerrainBrushLibrary.GetFolder(editor.Asset.Directory?.Package?.Package);

    public float FlattenHeight { get => flattenHeight; set => SetValue(ref flattenHeight, value, PushSettings); }

    public float NoiseScale { get => noiseScale; set => SetValue(ref noiseScale, Math.Max(value, 0.1f), PushSettings); }

    public IReadOnlyObservableList<TerrainLayerItemViewModel> Layers => layers;

    public TerrainLayerItemViewModel SelectedLayer { get => selectedLayer; set => SetValue(ref selectedLayer, value, PushSettings); }

    public string Hint => tool switch
    {
        TerrainBrushTool.Raise => "Click and drag to raise, Shift to lower.",
        TerrainBrushTool.Smooth => "Click and drag to smooth.",
        TerrainBrushTool.Flatten => "Drag to flatten to the target height, Shift to only carve down. Ctrl+click picks the height.",
        TerrainBrushTool.Noise => "Drag to add noise.",
        TerrainBrushTool.Paint => "Drag to paint the layer, Shift to erase it. Ctrl+click picks the layer.",
        TerrainBrushTool.Hole => "Drag to cut holes, Shift to fill them.",
        TerrainBrushTool.Grass => "Drag to paint the grass layer towards the density, Shift to erase it.",
        TerrainBrushTool.Vegetation => "Drag to place the type up to the density, Shift to remove it.",
        _ => "",
    } + " [ and ] change the radius" + (IsCustomBrush ? ", Ctrl+[ and Ctrl+] rotate the brush." : ".");

    public ICommandBase SelectToolCommand { get; }

    public ICommandBase ImportHeightmapCommand { get; }

    public ICommandBase ExportHeightmapCommand { get; }

    public ICommandBase ImportBrushCommand { get; }

    public ICommandBase OpenBrushFolderCommand { get; }

    public ICommandBase NewRoadCommand { get; }

    public ICommandBase NewRiverCommand { get; }

    public ICommandBase SnapSplinePointsCommand { get; }

    public ICommandBase ApplySplinesCommand { get; }

    public override void Destroy()
    {
        editor.Asset.PropertyGraph.Changed -= OnAssetChanged;
        editor.Asset.PropertyGraph.ItemChanged -= OnAssetChanged;
        if (service != null)
        {
            service.SetToolEnabled(false);
            service.ToolsViewModel = null;
        }
        base.Destroy();
    }

    internal void OnTargetChanged(string name, Guid? entityId, List<string> layerNames, List<string> grassNames, List<string> vegetationNames)
    {
        TargetName = name;
        SelectedLayer = Refill(layers, layerNames, selectedLayer);
        targetEntityId = entityId;
        gameGrassNames = grassNames;
        gameVegetationNames = vegetationNames;
        RefreshDefinitions();
    }

    private void OnAssetChanged(object sender, EventArgs e)
    {
        // Once for all the changes of an action
        if (definitionsRefreshQueued)
            return;
        definitionsRefreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            definitionsRefreshQueued = false;
            if (!IsDestroyed)
                RefreshDefinitions();
        });
    }

    // The asset-side terrain component of the target, when the target is part of the edited scene
    private TerrainComponent FindAssetComponent()
    {
        if (targetEntityId is not { } id || editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, id)) is not EntityViewModel entity)
            return null;
        return entity.AssetSideEntity.Get<TerrainComponent>();
    }

    /// <summary>
    /// Updates the grass layer and vegetation type lists: rebuilt when layers or types were added, removed or moved, refreshed
    /// otherwise.
    /// </summary>
    private void RefreshDefinitions()
    {
        var component = FindAssetComponent();
        CanEditDefinitions = component != null;
        var nodes = editor.Session.AssetNodeContainer;

        var grassDefinitions = component?.Grass.Cast<object>().ToList() ?? Enumerable.Repeat<object>(null, gameGrassNames.Count).ToList();
        SelectedGrassLayer = Sync(grassLayers, grassDefinitions, selectedGrassLayer, (i, definition) =>
            new TerrainGrassLayerItemViewModel(this, i, (TerrainGrassLayer)definition, definition != null ? nodes.GetNode(definition) : null, i < gameGrassNames.Count ? gameGrassNames[i] : null));

        var typeDefinitions = component?.Vegetation.Cast<object>().ToList() ?? Enumerable.Repeat<object>(null, gameVegetationNames.Count).ToList();
        SelectedVegetationType = Sync(vegetationTypes, typeDefinitions, selectedVegetationType, (i, definition) =>
            new TerrainVegetationTypeItemViewModel(this, i, (TerrainVegetationType)definition, definition != null ? nodes.GetNode(definition) : null, i < gameVegetationNames.Count ? gameVegetationNames[i] : null));
    }

    private static T Sync<T>(ObservableList<T> items, List<object> definitions, T selected, Func<int, object, T> create) where T : TerrainDefinitionItemViewModel
    {
        var same = items.Count == definitions.Count && definitions.All(x => x != null);
        for (int i = 0; same && i < items.Count; i++)
            same = items[i].Definition == definitions[i];
        if (same)
        {
            foreach (var item in items)
                item.Refresh();
            return selected;
        }

        // Keeps the selected definition (or index) selected
        var selectedDefinition = selected?.Definition;
        var selectedIndex = selected?.Index ?? 0;
        foreach (var item in items)
            item.Destroy();
        items.Clear();
        for (int i = 0; i < definitions.Count; i++)
            items.Add(create(i, definitions[i]));
        return items.FirstOrDefault(x => selectedDefinition != null && x.Definition == selectedDefinition)
            ?? items.FirstOrDefault(x => x.Index == Math.Min(selectedIndex, items.Count - 1));
    }

    /// <summary>
    /// Changes the grass layers or vegetation types of the scene asset, as one undoable action.
    /// </summary>
    internal void EditDefinitions(string name, Action change)
    {
        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using var transaction = undoRedo.CreateTransaction();
        change();
        undoRedo.SetName(transaction, name);
    }

    /// <summary>
    /// Lets the user pick model assets (none when cancelled).
    /// </summary>
    internal async Task<IReadOnlyList<AssetViewModel>> PickModels(bool multiple)
    {
        var picker = ServiceProvider.Get<IEditorDialogService>().CreateAssetPickerDialog(editor.Session);
        picker.Message = multiple ? "Select the models (one or more)" : "Select the model";
        picker.AllowMultiSelection = multiple;
        picker.AcceptedTypes.AddRange(AssetRegistry.GetAssetTypes(typeof(Model)));
        var result = await picker.ShowModal();
        return result == DialogResult.Ok ? picker.SelectedAssets.ToList() : [];
    }

    // "SM_grass_bush_01" gives "Grass bush 01"
    private static string NameFromAsset(AssetViewModel asset)
    {
        var name = asset.Name;
        foreach (var prefix in new[] { "SM_", "SK_", "M_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                name = name[prefix.Length..];
        }
        name = name.Replace('_', ' ').Trim();
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : asset.Name;
    }

    private IObjectNode GetDefinitionList(bool grass)
    {
        var component = FindAssetComponent();
        return component != null ? editor.Session.AssetNodeContainer.GetNode(component)[grass ? nameof(TerrainComponent.Grass) : nameof(TerrainComponent.Vegetation)].Target : null;
    }

    private async Task PickGrassLayer()
    {
        if (!CanEditDefinitions)
            return;
        if (grassLayers.Count >= TerrainData.MaxGrassLayers)
        {
            await ServiceProvider.Get<IDialogService>().MessageBoxAsync($"A terrain has at most {TerrainData.MaxGrassLayers} grass layers. A layer can grow several plants.", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddGrassLayer(await PickModels(multiple: true));
    }

    /// <summary>
    /// Adds a grass layer growing models to the target (an undoable change of the scene) and selects it.
    /// </summary>
    /// <returns>Whether it was added (the target is part of the scene, has room for a layer, and models were given).</returns>
    public bool AddGrassLayer(IReadOnlyList<AssetViewModel> models)
    {
        var list = GetDefinitionList(grass: true);
        if (list == null || models.Count == 0 || grassLayers.Count >= TerrainData.MaxGrassLayers)
            return false;

        var layer = new TerrainGrassLayer { Name = NameFromAsset(models[0]) };
        foreach (var asset in models)
            layer.Models.Add(ContentReferenceHelper.CreateReference<Model>(asset));
        AssetCollectionItemIdHelper.GenerateMissingItemIds(layer);
        EditDefinitions($"Add grass layer {layer.Name}", () => list.Add(layer));
        RefreshDefinitions();
        SelectedGrassLayer = grassLayers.LastOrDefault() ?? SelectedGrassLayer;
        return true;
    }

    private async Task PickVegetationTypes()
    {
        if (CanEditDefinitions)
            AddVegetationTypes(await PickModels(multiple: true));
    }

    /// <summary>
    /// Adds a vegetation type per model to the target (an undoable change of the scene) and selects the last one.
    /// </summary>
    /// <returns>Whether they were added (the target is part of the scene, and models were given).</returns>
    public bool AddVegetationTypes(IReadOnlyList<AssetViewModel> picked)
    {
        var list = GetDefinitionList(grass: false);
        if (list == null || picked.Count == 0)
            return false;

        EditDefinitions(picked.Count == 1 ? $"Add vegetation type {NameFromAsset(picked[0])}" : "Add vegetation types", () =>
        {
            foreach (var asset in picked)
            {
                var type = new TerrainVegetationType { Name = NameFromAsset(asset), Model = ContentReferenceHelper.CreateReference<Model>(asset) };
                AssetCollectionItemIdHelper.GenerateMissingItemIds(type);
                list.Add(type);
            }
        });
        RefreshDefinitions();
        SelectedVegetationType = vegetationTypes.LastOrDefault() ?? SelectedVegetationType;
        return true;
    }

    /// <summary>
    /// Removes the selected grass layer or vegetation type, with its data (painted density, placed instances), as one undoable
    /// action: the later layers or types move down one, in the component and in the data.
    /// </summary>
    /// <param name="grass">The selected grass layer, otherwise the selected vegetation type.</param>
    /// <param name="confirm">Whether to ask first when it has data.</param>
    /// <returns>Whether it was removed.</returns>
    public async Task<bool> RemoveDefinition(bool grass, bool confirm = true)
    {
        TerrainDefinitionItemViewModel item = grass ? selectedGrassLayer : selectedVegetationType;
        var list = GetDefinitionList(grass);
        if (item?.Definition == null || list == null || service == null)
            return false;

        var (instances, painted) = await service.GetDefinitionUsage();
        var usage = grass
            ? (item.Index < painted.Length && painted[item.Index] ? " Its painted density is removed too." : "")
            : (item.Index < instances.Length && instances[item.Index] > 0 ? $" Its {instances[item.Index]:N0} placed instances are removed too." : "");
        var kind = grass ? "grass layer" : "vegetation type";
        if (confirm && usage.Length > 0 && await ServiceProvider.Get<IDialogService>().MessageBoxAsync($"Remove the {kind} {item.Name}?{usage}", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        var index = item.Index;
        var removed = false;
        EditDefinitions($"Remove {kind} {item.Name}", () =>
        {
            // Without the data, only an unused last layer or type goes (removing another would renumber the data's)
            var last = index == (grass ? grassLayers.Count : vegetationTypes.Count) - 1;
            if (!service.RemoveDefinitionData(grass, index) && (usage.Length > 0 || !last))
                return;
            list.Remove(item.Definition, new NodeIndex(index));
            removed = true;
        });
        if (!removed && confirm)
            await ServiceProvider.Get<IDialogService>().MessageBoxAsync($"The terrain data can't be edited, so the {kind} can't be removed without breaking it.", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshDefinitions();
        return removed;
    }

    private void SelectTerrain()
    {
        if (targetEntityId is not { } id || editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, id)) is not EntityViewModel entity)
            return;
        editor.ClearSelection();
        editor.SelectedContent.Add(entity);
    }

    // Refills a list of named items, keeping the selected index when it's still there
    private static TerrainLayerItemViewModel Refill(ObservableList<TerrainLayerItemViewModel> items, List<string> names, TerrainLayerItemViewModel selected)
    {
        var selectedIndex = selected?.Index ?? 0;
        items.Clear();
        for (int i = 0; i < names.Count; i++)
            items.Add(new TerrainLayerItemViewModel(i, names[i]));
        return items.FirstOrDefault(x => x.Index == selectedIndex) ?? items.FirstOrDefault();
    }

    private void OnToolChanged()
    {
        PushSettings();
        if (IsVegetationTool)
            RefreshVegetationInfo();
    }

    // While the vegetation tool is open, the counts are refreshed every second
    private void RefreshVegetationInfo()
    {
        if (refreshingInfo)
            return;
        refreshingInfo = true;
        Dispatcher.InvokeTask(async () =>
        {
            try
            {
                while (IsActive && IsVegetationTool && service != null && !IsDestroyed)
                {
                    var infos = await service.GetVegetationInfo();
                    // The infos skip types without a model: matched by name
                    foreach (var item in vegetationTypes)
                    {
                        var info = infos.FirstOrDefault(x => x.Name == item.Name);
                        if (info == null)
                        {
                            item.Info = "No model (or not loaded yet).";
                            continue;
                        }
                        var lods = string.Join("\n", info.LodTriangles.Select((triangles, lod) => $"  {lod}: {triangles:N0} triangles, {(lod < info.LodInstances.Length ? info.LodInstances[lod] : 0):N0} drawn"));
                        var impostor = info.HasImpostor ? $"Impostor: {info.ImpostorViews}×{info.ImpostorViews} views of {info.ImpostorResolution} px" : "Impostor: not baked yet";
                        item.Info = $"{info.Instances:N0} placed\nLevels of detail:\n{lods}\n{impostor}";
                    }
                    await Task.Delay(1000);
                }
            }
            finally
            {
                refreshingInfo = false;
            }
        });
    }

    internal void OnRadiusChangedFromViewport(float value)
    {
        radius = value;
        OnPropertyChanged(nameof(Radius));
    }

    internal void OnRotationChangedFromViewport(float degrees)
    {
        rotation = degrees;
        OnPropertyChanged(nameof(Rotation));
    }

    internal void OnHeightSampled(float height) => FlattenHeight = height;

    internal void OnLayerSampled(int layer)
    {
        var item = layers.FirstOrDefault(x => x.Index == layer);
        if (item != null)
            SelectedLayer = item;
    }

    private void PushSettings()
    {
        if (service == null)
            return;
        service.SetBrush(new TerrainBrushSettings
        {
            Tool = tool,
            Radius = radius,
            Strength = strength,
            Falloff = falloff,
            TargetHeight = flattenHeight,
            Layer = selectedLayer?.Index ?? 0,
            NoiseScale = noiseScale,
            Mask = selectedBrush?.Mask,
            Rotation = MathUtil.DegreesToRadians(rotation),
            GrassLayer = selectedGrassLayer?.Index ?? -1,
            GrassDensity = grassDensity,
            VegetationType = selectedVegetationType?.Index ?? -1,
            VegetationDensity = vegetationDensity,
            VegetationSpacing = vegetationSpacing,
            VegetationMinScale = vegetationMinScale,
            VegetationMaxScale = Math.Max(vegetationMinScale, vegetationMaxScale),
            VegetationMaxSlope = vegetationMaxSlope,
        });
        service.SetToolEnabled(IsActive);
    }

    /// <summary>
    /// The splines concerned by the selection: selected spline entities and the splines of selected control points (empty: all).
    /// </summary>
    private HashSet<Guid> GetSelectedSplineIds()
    {
        var ids = new HashSet<Guid>();
        foreach (var entity in editor.SelectedContent.OfType<EntityViewModel>())
        {
            var assetEntity = entity.AssetSideEntity;
            if (assetEntity.Get<TerrainSplineComponent>() != null)
                ids.Add(assetEntity.Id);
            else if (assetEntity.Transform.Parent?.Entity is { } parent && parent.Get<TerrainSplineComponent>() != null)
                ids.Add(parent.Id);
        }
        return ids;
    }

    /// <summary>
    /// Applies the selected splines (all when none is selected) to the terrain. Returns the number applied, -1 without terrain.
    /// </summary>
    public async Task<int> ApplySplines()
    {
        if (service == null)
            return -1;
        var applied = await service.ApplySplines(GetSelectedSplineIds());
        if (applied < 0)
            await ServiceProvider.Get<IDialogService>().MessageBoxAsync("There is no editable terrain in this scene.", MessageBoxButton.OK, MessageBoxImage.Information);
        return applied;
    }

    /// <summary>
    /// Moves the control points of splines onto the terrain (an undoable change of their positions).
    /// </summary>
    public async Task<int> SnapSplinePoints(IReadOnlyCollection<Guid> splineIds)
    {
        if (service == null)
            return 0;
        // The editor game receives new entities asynchronously
        List<(Guid Id, Vector3 Position)> points = null;
        for (int attempt = 0; attempt < 40 && (points == null || points.Count == 0); attempt++)
        {
            if (attempt > 0)
                await Task.Delay(50);
            points = await service.ComputeSnappedSplinePoints(splineIds);
            if (splineIds.Count == 0)
                break;
        }
        if (points == null || points.Count == 0)
            return 0;

        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        var moved = 0;
        using (var transaction = undoRedo.CreateTransaction())
        {
            foreach (var (id, position) in points)
            {
                if (editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, id)) is not EntityViewModel point)
                    continue;
                var node = editor.Session.AssetNodeContainer.GetNode(point.AssetSideEntity.Transform)[nameof(TransformComponent.Position)];
                if ((Vector3)node.Retrieve() == position)
                    continue;
                node.Update(position);
                moved++;
            }
            undoRedo.SetName(transaction, "Snap spline points to terrain");
        }
        return moved;
    }

    /// <summary>
    /// Creates a spline entity with four control points at the center of the view, on the terrain, and selects it.
    /// </summary>
    public async Task<Guid> CreateSpline(TerrainSplineKind kind)
    {
        if (service == null)
            return Guid.Empty;
        var (center, right) = await service.GetViewCenter();
        var root = editor.HierarchyRoot;
        var name = kind == TerrainSplineKind.River ? "River" : "Road";
        var spline = new Entity(EntityFactory.ComputeNewName(root, name))
        {
            new TerrainSplineComponent
            {
                Kind = kind,
                Width = kind == TerrainSplineKind.River ? 8.0f : 4.0f,
                Falloff = kind == TerrainSplineKind.River ? 6.0f : 4.0f,
                Depth = kind == TerrainSplineKind.River ? 2.5f : 0.0f,
                ConformToTerrain = kind != TerrainSplineKind.River,
                MeshOffset = kind == TerrainSplineKind.River ? -0.4f : 0.05f,
                PaintLayer = kind == TerrainSplineKind.Road ? SelectedLayer?.Index ?? -1 : -1,
            },
        };
        spline.Transform.Position = center;
        const int count = 4;
        var spacing = kind == TerrainSplineKind.River ? 15.0f : 10.0f;
        for (int i = 0; i < count; i++)
        {
            var point = new Entity($"Point {i + 1}");
            point.Transform.Position = right * ((i - (count - 1) * 0.5f) * spacing);
            spline.AddChild(point);
        }

        var collection = new AssetPartCollection<EntityDesign, Entity>();
        foreach (var entity in new[] { spline }.Concat(spline.GetChildren()))
        {
            AssetCollectionItemIdHelper.GenerateMissingItemIds(entity);
            collection.Add(new EntityDesign(entity, ""));
        }

        var undoRedo = ServiceProvider.Get<IUndoRedoService>();
        using (var transaction = undoRedo.CreateTransaction())
        {
            root.Asset.AssetHierarchyPropertyGraph.AddPartToAsset(collection, collection[spline.Id], null, root.Owner.EntityCount);
            undoRedo.SetName(transaction, $"Create {name.ToLowerInvariant()}");
        }

        // Put the points on the ground, then select the new spline
        await SnapSplinePoints(new[] { spline.Id });
        if (editor.FindPartViewModel(new AbsoluteId(editor.Asset.Id, spline.Id)) is EntityViewModel created)
        {
            editor.ClearSelection();
            editor.SelectedContent.Add(created);
        }
        return spline.Id;
    }

    /// <summary>
    /// Reloads the brush list (built-in brushes and the images of the brush folder), keeping the selection when it's still there.
    /// </summary>
    public void RefreshBrushes()
    {
        var selectedName = selectedBrush?.Name;
        var selectedFile = selectedBrush?.FilePath;
        brushes.Clear();
        brushes.AddRange(TerrainBrushLibrary.BuiltIn);
        brushes.AddRange(TerrainBrushLibrary.LoadFolder(BrushFolder, (file, e) => editor.Logger.Warning($"Could not load the terrain brush [{file}]: {e.Message}")));
        SelectedBrush = brushes.FirstOrDefault(x => x.FilePath == selectedFile && x.Name == selectedName) ?? brushes[0];
        OnPropertyChanged(nameof(Hint));
    }

    private async Task ImportBrush()
    {
        var folder = BrushFolder;
        if (folder == null)
            return;
        var file = await ServiceProvider.Get<IDialogService>().OpenFilePickerAsync(null,
        [
            new FilePickerFilter("Brush images") { Patterns = TerrainBrushLibrary.ImageExtensions.Select(x => "*" + x).ToList() },
        ]);
        if (file == null)
            return;

        var source = file.ToOSPath();
        try
        {
            // Checks it loads before copying it into the project
            TerrainBrushLibrary.LoadMask(source);
            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(source);
            var extension = Path.GetExtension(source);
            var target = Path.Combine(folder, name + extension);
            for (int i = 2; File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase); i++)
                target = Path.Combine(folder, $"{name} {i}{extension}");
            if (!File.Exists(target))
                File.Copy(source, target);

            RefreshBrushes();
            SelectedBrush = brushes.FirstOrDefault(x => string.Equals(x.FilePath, target, StringComparison.OrdinalIgnoreCase)) ?? SelectedBrush;
        }
        catch (Exception e)
        {
            await ServiceProvider.Get<IDialogService>().MessageBoxAsync($"Failed to import the brush: {e.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenBrushFolder()
    {
        var folder = BrushFolder;
        if (folder == null)
            return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private async Task ImportHeightmap()
    {
        if (service == null || !HasTarget)
            return;

        var file = await ServiceProvider.Get<IDialogService>().OpenFilePickerAsync(null,
        [
            new FilePickerFilter("Height maps") { Patterns = ["*.r16", "*.raw", "*.png", "*.tif", "*.tiff", "*.dds"] },
        ]);
        if (file == null)
            return;

        float[] samples;
        int size;
        try
        {
            (samples, size) = LoadNormalizedHeights(file.ToOSPath());
        }
        catch (Exception e)
        {
            await ServiceProvider.Get<IDialogService>().MessageBoxAsync($"Failed to load the height map: {e.Message}", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Normalized heights are mapped to the terrain height range, resampled to its resolution
        service.ImportHeights(data =>
        {
            var resolution = data.Resolution;
            var result = new float[resolution * resolution];
            var scale = (size - 1) / (float)(resolution - 1);
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    var v = SampleBilinear(samples, size, x * scale, z * scale);
                    result[z * resolution + x] = data.MinHeight + v * (data.MaxHeight - data.MinHeight);
                }
            }
            return result;
        }, "Import terrain height map");
    }

    private async Task ExportHeightmap()
    {
        if (service == null || !HasTarget)
            return;
        var file = await ServiceProvider.Get<IDialogService>().SaveFilePickerAsync(null,
            [new FilePickerFilter("Raw 16-bit height map") { Patterns = ["*.r16"] }], ".r16", (TargetName ?? "Terrain") + ".r16");
        if (file == null)
            return;
        var data = await service.GetTargetDataCopy();
        if (data == null)
            return;

        // Little-endian 16-bit, normalized over the terrain height range
        using var stream = File.Create(file.ToOSPath());
        using var writer = new BinaryWriter(stream);
        foreach (var value in data.Heights)
            writer.Write(value);
    }

    private static (float[] Samples, int Size) LoadNormalizedHeights(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".r16" or ".raw")
        {
            var bytes = File.ReadAllBytes(path);
            var count = bytes.Length / 2;
            var size = (int)Math.Round(Math.Sqrt(count));
            if (size * size != count)
                throw new InvalidDataException("A raw height map must be a square of 16-bit samples.");
            var samples = new float[count];
            for (int i = 0; i < count; i++)
                samples[i] = BitConverter.ToUInt16(bytes, i * 2) / 65535.0f;
            return (samples, size);
        }

        using var stream = File.OpenRead(path);
        using var image = Image.Load(stream);
        var description = image.Description;
        if (description.Width != description.Height)
            throw new InvalidDataException("The height map must be square.");
        var pixelBuffer = image.PixelBuffer[0];
        var width = description.Width;
        var result = new float[width * width];
        switch (description.Format)
        {
            case PixelFormat.R16_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<ushort>();
                for (int i = 0; i < result.Length; i++)
                    result[i] = pixels[i] / 65535.0f;
                break;
            }
            case PixelFormat.R16G16B16A16_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<ushort>();
                for (int i = 0; i < result.Length; i++)
                    result[i] = pixels[i * 4] / 65535.0f;
                break;
            }
            case PixelFormat.R32_Float:
            {
                var pixels = pixelBuffer.GetPixels<float>();
                Array.Copy(pixels, result, result.Length);
                break;
            }
            case PixelFormat.R8_UNorm:
            {
                var pixels = pixelBuffer.GetPixels<byte>();
                for (int i = 0; i < result.Length; i++)
                    result[i] = pixels[i] / 255.0f;
                break;
            }
            case PixelFormat.R8G8B8A8_UNorm:
            case PixelFormat.R8G8B8A8_UNorm_SRgb:
            case PixelFormat.B8G8R8A8_UNorm:
            case PixelFormat.B8G8R8A8_UNorm_SRgb:
            {
                var pixels = pixelBuffer.GetPixels<byte>();
                for (int i = 0; i < result.Length; i++)
                    result[i] = pixels[i * 4 + 1] / 255.0f;
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported height map format {description.Format} (use 16-bit grayscale PNG or raw .r16).");
        }
        return (result, width);
    }

    private static float SampleBilinear(float[] samples, int size, float x, float z)
    {
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, size - 1);
        var z0 = Math.Clamp((int)MathF.Floor(z), 0, size - 1);
        var x1 = Math.Min(x0 + 1, size - 1);
        var z1 = Math.Min(z0 + 1, size - 1);
        var tx = x - x0;
        var tz = z - z0;
        var a = samples[z0 * size + x0] + (samples[z0 * size + x1] - samples[z0 * size + x0]) * tx;
        var b = samples[z1 * size + x0] + (samples[z1 * size + x1] - samples[z1 * size + x0]) * tx;
        return a + (b - a) * tz;
    }
}

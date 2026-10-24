// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Quantum;
using Stride.Rendering;
using Stride.Rendering.VolumetricFog;
using Stride.Terrain.Fog;
using Stride.Terrain.Vegetation;

namespace Stride.Terrain.Editor;

/// <summary>
/// A model of a grass layer or vegetation type, with the thumbnail of its asset.
/// </summary>
public sealed class TerrainModelItemViewModel
{
    public TerrainModelItemViewModel(AssetViewModel asset)
    {
        Asset = asset;
    }

    /// <summary>
    /// The model's asset (null when the reference is broken).
    /// </summary>
    public AssetViewModel Asset { get; }

    public string Name => Asset?.Name ?? "(missing model)";
}

/// <summary>
/// A grass layer, vegetation type or fog layer of the target terrain. When the terrain is part of the edited scene, its
/// settings are edited on the scene asset (undoable); otherwise only its name is known (from the editor game).
/// </summary>
public abstract class TerrainDefinitionItemViewModel : DispatcherViewModel
{
    private readonly string gameName;
    private string[] propertyNames;

    protected TerrainDefinitionItemViewModel(TerrainToolsViewModel tools, int index, object definition, IObjectNode node, string gameName)
        : base(tools.ServiceProvider)
    {
        Tools = tools;
        Index = index;
        Definition = definition;
        Node = node;
        this.gameName = gameName;
    }

    protected TerrainToolsViewModel Tools { get; }

    public int Index { get; }

    /// <summary>
    /// The asset-side grass layer or vegetation type (null when it can't be edited).
    /// </summary>
    public object Definition { get; }

    internal IObjectNode Node { get; }

    /// <summary>
    /// Whether the settings can be edited here (the terrain is part of the edited scene).
    /// </summary>
    public bool CanEdit => Node != null;

    public string Name
    {
        get => Node != null ? (string)Node[nameof(Name)].Retrieve() : gameName;
        set => Set(nameof(Name), value?.Trim() ?? "");
    }

    public string DisplayName => $"{Index}: {(string.IsNullOrEmpty(Name) ? "(unnamed)" : Name)}";

    /// <summary>
    /// The model shown in the list (the first one; null for a fog layer).
    /// </summary>
    public abstract TerrainModelItemViewModel Thumbnail { get; }

    protected T Get<T>(string member) => Node != null ? (T)Node[member].Retrieve() : default;

    protected void Set(string member, object value)
    {
        if (Node == null || Equals(Node[member].Retrieve(), value))
            return;
        Tools.EditDefinitions($"Change {Name} {member}", () => Node[member].Update(value));
    }

    /// <summary>
    /// Raises the change of every property (the asset changed, e.g. undone).
    /// </summary>
    internal virtual void Refresh()
    {
        propertyNames ??= GetType().GetProperties().Select(x => x.Name).ToArray();
        OnPropertyChanged(propertyNames);
    }

    protected AssetViewModel GetAsset(object reference) => reference != null ? ContentReferenceHelper.GetReferenceTarget(Tools.Session, reference) : null;
}

/// <summary>
/// A grass layer of the target terrain (see <see cref="TerrainGrassLayer"/>).
/// </summary>
public sealed class TerrainGrassLayerItemViewModel : TerrainDefinitionItemViewModel
{
    private readonly ObservableList<TerrainModelItemViewModel> models = new();
    private TerrainModelItemViewModel selectedModel;

    public TerrainGrassLayerItemViewModel(TerrainToolsViewModel tools, int index, TerrainGrassLayer definition, IObjectNode node, string gameName)
        : base(tools, index, definition, node, gameName)
    {
        AddModelsCommand = new AnonymousTaskCommand(ServiceProvider, AddModels);
        RemoveModelCommand = new AnonymousCommand(ServiceProvider, RemoveModel);
        RefreshModels();
    }

    public IReadOnlyObservableList<TerrainModelItemViewModel> Models => models;

    public TerrainModelItemViewModel SelectedModel { get => selectedModel; set => SetValue(ref selectedModel, value); }

    public override TerrainModelItemViewModel Thumbnail => models.FirstOrDefault();

    /// <summary>
    /// Plants per square meter where fully painted.
    /// </summary>
    public float Density { get => Get<float>(nameof(TerrainGrassLayer.Density)); set => Set(nameof(TerrainGrassLayer.Density), Math.Clamp(value, 0.01f, 64.0f)); }

    public float MinScale { get => Get<float>(nameof(TerrainGrassLayer.MinScale)); set => Set(nameof(TerrainGrassLayer.MinScale), Math.Clamp(value, 0.01f, 10.0f)); }

    public float MaxScale { get => Get<float>(nameof(TerrainGrassLayer.MaxScale)); set => Set(nameof(TerrainGrassLayer.MaxScale), Math.Clamp(value, 0.01f, 10.0f)); }

    public float DrawDistance { get => Get<float>(nameof(TerrainGrassLayer.DrawDistance)); set => Set(nameof(TerrainGrassLayer.DrawDistance), Math.Clamp(value, 1.0f, 500.0f)); }

    /// <summary>
    /// Where the plants start thinning out, in percent of the draw distance.
    /// </summary>
    public float ThinOutStartPercent
    {
        get => Get<float>(nameof(TerrainGrassLayer.ThinOutStart)) * 100;
        set => Set(nameof(TerrainGrassLayer.ThinOutStart), Math.Clamp(value / 100, 0.0f, 1.0f));
    }

    public bool AlignToTerrain { get => Get<bool>(nameof(TerrainGrassLayer.AlignToTerrain)); set => Set(nameof(TerrainGrassLayer.AlignToTerrain), value); }

    public bool CastShadows { get => Get<bool>(nameof(TerrainGrassLayer.CastShadows)); set => Set(nameof(TerrainGrassLayer.CastShadows), value); }

    public ICommandBase AddModelsCommand { get; }

    public ICommandBase RemoveModelCommand { get; }

    internal override void Refresh()
    {
        RefreshModels();
        base.Refresh();
    }

    private void RefreshModels()
    {
        var selected = selectedModel?.Asset;
        models.Clear();
        if (Definition is TerrainGrassLayer layer)
            models.AddRange(layer.Models.Select(x => new TerrainModelItemViewModel(GetAsset(x))));
        SelectedModel = models.FirstOrDefault(x => x.Asset == selected && selected != null) ?? models.FirstOrDefault();
    }

    private async Task AddModels()
    {
        if (Node == null)
            return;
        var picked = await Tools.PickModels(multiple: true);
        if (picked.Count == 0)
            return;
        var list = Node[nameof(TerrainGrassLayer.Models)].Target;
        Tools.EditDefinitions($"Add plants to {Name}", () =>
        {
            foreach (var asset in picked)
                list.Add(ContentReferenceHelper.CreateReference<Model>(asset));
        });
    }

    private void RemoveModel()
    {
        var index = selectedModel != null ? models.IndexOf(selectedModel) : -1;
        if (Node == null || index < 0)
            return;
        var list = Node[nameof(TerrainGrassLayer.Models)].Target;
        var value = ((TerrainGrassLayer)Definition).Models[index];
        Tools.EditDefinitions($"Remove a plant from {Name}", () => list.Remove(value, new NodeIndex(index)));
    }
}

/// <summary>
/// A fog layer of the target terrain (see <see cref="TerrainFogLayer"/>). Its colors are edited in the property grid of the terrain.
/// </summary>
public sealed class TerrainFogLayerItemViewModel : TerrainDefinitionItemViewModel
{
    public TerrainFogLayerItemViewModel(TerrainToolsViewModel tools, int index, TerrainFogLayer definition, IObjectNode node, string gameName)
        : base(tools, index, definition, node, gameName)
    {
    }

    public override TerrainModelItemViewModel Thumbnail => null;

    public IReadOnlyList<FogLayerHeightMode> HeightModes { get; } = Enum.GetValues<FogLayerHeightMode>();

    /// <summary>
    /// Extinction per meter where fully painted.
    /// </summary>
    public float Density { get => Get<float>(nameof(TerrainFogLayer.Density)); set => Set(nameof(TerrainFogLayer.Density), Math.Clamp(value, 0.0f, 10.0f)); }

    public FogLayerHeightMode HeightMode { get => Get<FogLayerHeightMode>(nameof(TerrainFogLayer.HeightMode)); set => Set(nameof(TerrainFogLayer.HeightMode), value); }

    /// <summary>
    /// Top of the fog (meters above the ground, or altitude).
    /// </summary>
    public float Height { get => Get<float>(nameof(TerrainFogLayer.Height)); set => Set(nameof(TerrainFogLayer.Height), Math.Clamp(value, -10000.0f, 10000.0f)); }

    public float Softness { get => Get<float>(nameof(TerrainFogLayer.Softness)); set => Set(nameof(TerrainFogLayer.Softness), Math.Clamp(value, 0.01f, 1000.0f)); }

    public float NoiseStrength { get => Get<float>(nameof(TerrainFogLayer.NoiseStrength)); set => Set(nameof(TerrainFogLayer.NoiseStrength), Math.Clamp(value, 0.0f, 1.0f)); }
}

/// <summary>
/// A vegetation type of the target terrain (see <see cref="TerrainVegetationType"/>).
/// </summary>
public sealed class TerrainVegetationTypeItemViewModel : TerrainDefinitionItemViewModel
{
    private string info;

    public TerrainVegetationTypeItemViewModel(TerrainToolsViewModel tools, int index, TerrainVegetationType definition, IObjectNode node, string gameName)
        : base(tools, index, definition, node, gameName)
    {
        ChangeModelCommand = new AnonymousTaskCommand(ServiceProvider, ChangeModel);
    }

    public TerrainModelItemViewModel Model => Definition is TerrainVegetationType type ? new TerrainModelItemViewModel(GetAsset(type.Model)) : null;

    public override TerrainModelItemViewModel Thumbnail => Model;

    /// <summary>
    /// Where the impostors replace the meshes (meters).
    /// </summary>
    public float ImpostorDistance { get => Get<float>(nameof(TerrainVegetationType.ImpostorDistance)); set => Set(nameof(TerrainVegetationType.ImpostorDistance), Math.Clamp(value, 0.0f, 5000.0f)); }

    public float ImpostorFadeRange { get => Get<float>(nameof(TerrainVegetationType.ImpostorFadeRange)); set => Set(nameof(TerrainVegetationType.ImpostorFadeRange), Math.Clamp(value, 0.0f, 500.0f)); }

    public int ImpostorViews { get => Get<int>(nameof(TerrainVegetationType.ImpostorViews)); set => Set(nameof(TerrainVegetationType.ImpostorViews), Math.Clamp(value, 4, 16)); }

    public int ImpostorResolution { get => Get<int>(nameof(TerrainVegetationType.ImpostorResolution)); set => Set(nameof(TerrainVegetationType.ImpostorResolution), Math.Clamp(value, 32, 1024)); }

    public bool CastShadows { get => Get<bool>(nameof(TerrainVegetationType.CastShadows)); set => Set(nameof(TerrainVegetationType.CastShadows), value); }

    public bool ImpostorsCastShadows { get => Get<bool>(nameof(TerrainVegetationType.ImpostorsCastShadows)); set => Set(nameof(TerrainVegetationType.ImpostorsCastShadows), value); }

    /// <summary>
    /// Where each level of detail hands over to the next, as "20, 45, 80" (meters; empty: spread up to the impostor distance).
    /// </summary>
    public string LodDistances
    {
        get => Definition is TerrainVegetationType type ? string.Join(", ", type.LodDistances.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture))) : "";
        set
        {
            if (Node == null || Definition is not TerrainVegetationType type)
                return;
            var distances = new List<float>();
            foreach (var part in (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) && distance > 0)
                    distances.Add(distance);
            }
            distances.Sort();
            if (distances.SequenceEqual(type.LodDistances))
                return;
            var list = Node[nameof(TerrainVegetationType.LodDistances)].Target;
            Tools.EditDefinitions($"Change {Name} level of detail distances", () =>
            {
                for (int i = type.LodDistances.Count - 1; i >= 0; i--)
                    list.Remove(type.LodDistances[i], new NodeIndex(i));
                foreach (var distance in distances)
                    list.Add(distance);
            });
        }
    }

    /// <summary>
    /// What the editor game draws of the type: instances placed, levels of detail (triangles, instances drawn), impostor.
    /// </summary>
    public string Info { get => info; internal set => SetValue(ref info, value); }

    public ICommandBase ChangeModelCommand { get; }

    private async Task ChangeModel()
    {
        if (Node == null)
            return;
        var picked = await Tools.PickModels(multiple: false);
        if (picked.Count == 0)
            return;
        Set(nameof(TerrainVegetationType.Model), ContentReferenceHelper.CreateReference<Model>(picked[0]));
    }
}

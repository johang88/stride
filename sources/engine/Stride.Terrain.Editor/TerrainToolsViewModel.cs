// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stride.Assets.Entities;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.EntityFactories;
using Stride.Assets.Presentation.AssetEditors.EntityHierarchyEditor.ViewModels;
using Stride.Assets.Presentation.AssetEditors.GameEditor.Services;
using Stride.Core.Assets;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Terrain.Splines;
using Stride.Core.Annotations;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.ViewModels;
using Stride.Graphics;
using Stride.Terrain;
using Stride.Terrain.Editing;

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

    public override bool IsAvailable => service != null;

    /// <summary>
    /// The editor game service applying the tools (null until the scene is loaded).
    /// </summary>
    public EditorGameTerrainService Service => service;

    /// <inheritdoc/>
    /// <remarks>The terrain tools take the mouse in the viewport while active.</remarks>
    protected override void OnIsActiveChanged() => PushSettings();

    public string TargetName { get => targetName; private set => SetValue(ref targetName, value, nameof(TargetName), nameof(HasTarget)); }

    public bool HasTarget => targetName != null;

    public IReadOnlyList<TerrainBrushTool> Tools { get; } = Enum.GetValues<TerrainBrushTool>();

    public TerrainBrushTool Tool { get => tool; set => SetValue(ref tool, value, PushSettings, nameof(Tool), nameof(IsPaintTool), nameof(IsFlattenTool), nameof(IsNoiseTool), nameof(Hint)); }

    public bool IsPaintTool => tool == TerrainBrushTool.Paint;

    public bool IsFlattenTool => tool == TerrainBrushTool.Flatten;

    public bool IsNoiseTool => tool == TerrainBrushTool.Noise;

    public float Radius { get => radius; set => SetValue(ref radius, Math.Clamp(value, 0.5f, 500.0f), PushSettings); }

    public float Strength { get => strength; set => SetValue(ref strength, Math.Clamp(value, 0.0f, 1.0f), PushSettings); }

    public float Falloff { get => falloff; set => SetValue(ref falloff, Math.Clamp(value, 0.0f, 1.0f), PushSettings); }

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
        _ => "",
    } + " [ and ] change the radius.";

    public ICommandBase SelectToolCommand { get; }

    public ICommandBase ImportHeightmapCommand { get; }

    public ICommandBase ExportHeightmapCommand { get; }

    public ICommandBase NewRoadCommand { get; }

    public ICommandBase NewRiverCommand { get; }

    public ICommandBase SnapSplinePointsCommand { get; }

    public ICommandBase ApplySplinesCommand { get; }

    public override void Destroy()
    {
        if (service != null)
        {
            service.SetToolEnabled(false);
            service.ToolsViewModel = null;
        }
        base.Destroy();
    }

    internal void OnTargetChanged(string name, List<string> layerNames)
    {
        TargetName = name;
        var selectedIndex = selectedLayer?.Index ?? 0;
        layers.Clear();
        for (int i = 0; i < layerNames.Count; i++)
            layers.Add(new TerrainLayerItemViewModel(i, layerNames[i]));
        SelectedLayer = layers.FirstOrDefault(x => x.Index == selectedIndex) ?? layers.FirstOrDefault();
    }

    internal void OnRadiusChangedFromViewport(float value)
    {
        radius = value;
        OnPropertyChanged(nameof(Radius));
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

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stride.Blockout.Rendering;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.ViewModels;

namespace Stride.Blockout.Editor;

/// <summary>
/// A named style of the blockout tools, applied to shapes with one click.
/// </summary>
public sealed class BlockoutPresetViewModel : DispatcherViewModel
{
    private readonly Action<BlockoutPresetViewModel, string> changed;
    private string name;
    private BlockoutPattern pattern;
    private Color3 color;
    private float scale;
    private float contrast;
    private float roughness;
    private bool isCurrent;

    public BlockoutPresetViewModel(IViewModelServiceProvider serviceProvider, BlockoutStyle style, Action<BlockoutPresetViewModel, string> changed)
        : base(serviceProvider)
    {
        name = style.Preset ?? "Style";
        pattern = style.Pattern;
        color = style.Color;
        scale = style.Scale;
        contrast = style.Contrast;
        roughness = style.Roughness;
        this.changed = changed;
    }

    public string Name
    {
        get => name;
        set
        {
            var old = name;
            value = string.IsNullOrWhiteSpace(value) ? old : value.Trim();
            if (SetValue(ref name, value))
                changed?.Invoke(this, old);
        }
    }

    public BlockoutPattern Pattern { get => pattern; set { if (SetValue(ref pattern, value, nameof(Pattern), nameof(Look))) changed?.Invoke(this, name); } }

    /// <summary>
    /// The color (sRGB).
    /// </summary>
    public Color3 Color { get => color; set { if (SetValue(ref color, value, nameof(Color), nameof(PickerColor), nameof(Look))) changed?.Invoke(this, name); } }

    /// <summary>
    /// The color for the color picker.
    /// </summary>
    public Color4 PickerColor { get => new(color, 1); set => Color = new Color3(value.R, value.G, value.B); }

    public float Scale { get => scale; set { if (SetValue(ref scale, Math.Clamp(value, 0.05f, 100), nameof(Scale), nameof(Look))) changed?.Invoke(this, name); } }

    public float Contrast { get => contrast; set { if (SetValue(ref contrast, Math.Clamp(value, 0, 1), nameof(Contrast), nameof(Look))) changed?.Invoke(this, name); } }

    public float Roughness { get => roughness; set { if (SetValue(ref roughness, Math.Clamp(value, 0, 1), nameof(Roughness), nameof(Look))) changed?.Invoke(this, name); } }

    public BlockoutLook Look => new(pattern, color, scale, contrast, roughness);

    public IReadOnlyList<BlockoutPattern> Patterns { get; } = Enum.GetValues<BlockoutPattern>();

    /// <summary>
    /// Whether it's the preset last applied (the one edited in the panel).
    /// </summary>
    public bool IsCurrent { get => isCurrent; set => SetValue(ref isCurrent, value); }

    /// <summary>
    /// A new style of shapes with this preset.
    /// </summary>
    public BlockoutStyle ToStyle() => new() { Preset = name, Pattern = pattern, Color = color, Scale = scale, Contrast = contrast, Roughness = roughness };

    /// <summary>
    /// Whether a style of a shape matches this preset (name aside).
    /// </summary>
    public bool Matches(BlockoutStyle style) => style != null && style.Look == Look;

    public override string ToString() => name;
}

/// <summary>
/// Loads and saves the style presets of a project: <c>BlockoutStyles.json</c> beside its package file.
/// </summary>
public static class BlockoutStyleLibrary
{
    public const string FileName = "BlockoutStyles.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Entry
    {
        public string Name { get; set; }
        public BlockoutPattern Pattern { get; set; }
        public string Color { get; set; }
        public float Scale { get; set; } = 1;
        public float Contrast { get; set; } = 0.5f;
        public float Roughness { get; set; } = 0.85f;
    }

    /// <summary>
    /// The file of the presets of a package, or null.
    /// </summary>
    public static string GetPath(Package package)
    {
        var root = package?.RootDirectory;
        return root != null ? Path.Combine(root.ToOSPath(), FileName) : null;
    }

    /// <summary>
    /// The presets a project starts with.
    /// </summary>
    public static List<BlockoutStyle> Defaults() => new()
    {
        Style("Floor", BlockoutPattern.Tiles, new Color3(0.52f, 0.52f, 0.5f), 1, 0.35f),
        Style("Wall", BlockoutPattern.Grid, new Color3(0.78f, 0.76f, 0.72f), 1, 0.45f),
        Style("Trim", BlockoutPattern.Grid, new Color3(0.3f, 0.3f, 0.32f), 0.5f, 0.5f),
        Style("Path", BlockoutPattern.Checker, new Color3(0.46f, 0.6f, 0.36f), 1, 0.25f),
        Style("Blocked", BlockoutPattern.Stripes, new Color3(0.75f, 0.2f, 0.18f), 1, 0.5f),
        Style("Interactive", BlockoutPattern.Grid, new Color3(0.2f, 0.5f, 0.85f), 0.5f, 0.6f),
        Style("Hazard", BlockoutPattern.Stripes, new Color3(0.95f, 0.75f, 0.1f), 0.5f, 0.9f),
        Style("Water", BlockoutPattern.Solid, new Color3(0.15f, 0.35f, 0.55f), 1, 0.3f, 0.15f),
        Style("Wood", BlockoutPattern.Planks, new Color3(0.55f, 0.38f, 0.22f), 1, 0.45f),
        Style("Stone", BlockoutPattern.Bricks, new Color3(0.6f, 0.58f, 0.55f), 1, 0.4f),
    };

    private static BlockoutStyle Style(string name, BlockoutPattern pattern, Color3 color, float scale, float contrast, float roughness = 0.85f)
        => new() { Preset = name, Pattern = pattern, Color = color, Scale = scale, Contrast = contrast, Roughness = roughness };

    /// <summary>
    /// Loads the presets of a file; the defaults when there's no file, it can't be read or it's empty.
    /// </summary>
    public static List<BlockoutStyle> Load(string path, Action<Exception> onError = null)
    {
        if (path == null || !File.Exists(path))
            return Defaults();
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Options);
            var styles = entries?.Where(x => !string.IsNullOrWhiteSpace(x?.Name))
                .Select(x => Style(x.Name, x.Pattern, ParseColor(x.Color), x.Scale, x.Contrast, x.Roughness))
                .ToList();
            return styles is { Count: > 0 } ? styles : Defaults();
        }
        catch (Exception e)
        {
            onError?.Invoke(e);
            return Defaults();
        }
    }

    public static void Save(string path, IEnumerable<BlockoutStyle> styles)
    {
        var entries = styles.Select(x => new Entry
        {
            Name = x.Preset,
            Pattern = x.Pattern,
            Color = FormatColor(x.Color),
            Scale = x.Scale,
            Contrast = x.Contrast,
            Roughness = x.Roughness,
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
    }

    private static string FormatColor(Color3 color)
    {
        var c = (Color)color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static Color3 ParseColor(string text)
    {
        if (text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new Color3(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f);
        return new Color3(0.55f);
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Rendering;

/// <summary>
/// The procedural patterns of the blockout material, sized by <see cref="BlockoutStyle.Scale"/> (one cell).
/// </summary>
public enum BlockoutPattern
{
    /// <summary>
    /// Lines around each cell, fainter ones every quarter.
    /// </summary>
    Grid = 0,

    /// <summary>
    /// Alternating cells.
    /// </summary>
    Checker = 1,

    /// <summary>
    /// Diagonal stripes, half a cell wide.
    /// </summary>
    Stripes = 2,

    /// <summary>
    /// Bricks a cell long and half a cell high.
    /// </summary>
    Bricks = 3,

    /// <summary>
    /// Square tiles of a cell, each a slightly different tone.
    /// </summary>
    Tiles = 4,

    /// <summary>
    /// Boards a quarter of a cell wide.
    /// </summary>
    Planks = 5,

    /// <summary>
    /// The color only.
    /// </summary>
    Solid = 6,
}

/// <summary>
/// The look of a blockout shape: a pattern in a color.
/// </summary>
[DataContract("BlockoutStyle")]
public sealed class BlockoutStyle
{
    /// <summary>
    /// The name of the preset of the blockout tools it was set from (presets edited in the tools update the shapes that use them).
    /// </summary>
    [DataMember(0)]
    [System.ComponentModel.DefaultValue(null)]
    public string Preset { get; set; }

    [DataMember(10)]
    public BlockoutPattern Pattern { get; set; } = BlockoutPattern.Grid;

    /// <summary>
    /// The base color (sRGB).
    /// </summary>
    [DataMember(20)]
    public Color3 Color { get; set; } = new(0.55f, 0.55f, 0.55f);

    /// <summary>
    /// The size of a cell of the pattern, in meters.
    /// </summary>
    [DataMember(30)]
    [Display("Pattern size (m)")]
    [DataMemberRange(0.05, 100, 0.25, 1, 2)]
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// How much the lines, stripes or alternate cells of the pattern stand out from the base color.
    /// </summary>
    [DataMember(40)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float Contrast { get; set; } = 0.5f;

    [DataMember(50)]
    [DataMemberRange(0, 1, 0.05, 0.1, 2)]
    public float Roughness { get; set; } = 0.85f;

    public BlockoutStyle Clone() => (BlockoutStyle)MemberwiseClone();

    /// <summary>
    /// The values that make a material (the preset name aside).
    /// </summary>
    public BlockoutLook Look => new(Pattern, Color, Scale, Contrast, Roughness);
}

/// <summary>
/// The values of a <see cref="BlockoutStyle"/> that make its material.
/// </summary>
public readonly record struct BlockoutLook(BlockoutPattern Pattern, Color3 Color, float Scale, float Contrast, float Roughness)
{
    /// <summary>
    /// The base color, linear.
    /// </summary>
    public Color3 LinearColor => Color.ToLinear();

    /// <summary>
    /// The color of the lines, stripes or alternate cells (linear): darker on a light color, lighter on a dark one.
    /// </summary>
    public Color3 LinearAccent
    {
        get
        {
            var contrast = Math.Clamp(Contrast, 0, 1);
            var luminance = 0.2126f * Color.R + 0.7152f * Color.G + 0.0722f * Color.B;
            var accent = luminance > 0.3f
                ? Color * (1 - 0.8f * contrast)
                : Color3.Lerp(Color, new Color3(1), 0.55f * contrast);
            return accent.ToLinear();
        }
    }

    /// <summary>
    /// How much the tone of the tiles, bricks and planks varies.
    /// </summary>
    public float ToneVariation => Pattern is BlockoutPattern.Bricks or BlockoutPattern.Tiles or BlockoutPattern.Planks ? 0.25f * Math.Clamp(Contrast, 0, 1) + 0.05f : 0;
}

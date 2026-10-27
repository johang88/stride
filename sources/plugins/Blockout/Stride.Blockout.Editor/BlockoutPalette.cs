// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Blockout.Shapes;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Editor;

/// <summary>
/// How a shape drawn in the viewport turns to follow the footprint dragged.
/// </summary>
public enum BlockoutPlacement
{
    /// <summary>
    /// Along the axes of the surface drawn on.
    /// </summary>
    Free,

    /// <summary>
    /// Rising along the longer side of the footprint, toward where the drag ended (ramps, stairs).
    /// </summary>
    Rise,

    /// <summary>
    /// Spanning the longer side of the footprint (walls); a drag along a line gives the default thickness.
    /// </summary>
    Span,
}

/// <summary>
/// A shape of the blockout tool's palette: a shape with its settings, the size of new ones.
/// </summary>
public sealed class BlockoutShapeItemViewModel
{
    private readonly Func<BlockoutShape> create;
    private BlockoutGeometry previewGeometry;

    public BlockoutShapeItemViewModel(string name, string toolTip, Func<BlockoutShape> create, Vector3 defaultSize, BlockoutPlacement placement)
    {
        Name = name;
        ToolTip = toolTip;
        this.create = create;
        DefaultSize = defaultSize;
        Placement = placement;
        Height = defaultSize.Y;
    }

    public string Name { get; }

    public string ToolTip { get; }

    public Vector3 DefaultSize { get; }

    public BlockoutPlacement Placement { get; }

    /// <summary>
    /// The height new shapes start with (the last one drawn).
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// A new shape with the settings of this item.
    /// </summary>
    public BlockoutShape CreateShape() => create();

    /// <summary>
    /// The triangles of the shape at its default size (palette thumbnails).
    /// </summary>
    public BlockoutGeometry PreviewGeometry
    {
        get
        {
            if (previewGeometry == null)
            {
                var builder = new BlockoutMeshBuilder();
                CreateShape().Build(builder, DefaultSize);
                previewGeometry = builder.ToGeometry();
            }
            return previewGeometry;
        }
    }

    public override string ToString() => Name;
}

/// <summary>
/// The shapes of the blockout tool's palette.
/// </summary>
public static class BlockoutPalette
{
    public static IReadOnlyList<BlockoutShapeItemViewModel> CreateItems() => new[]
    {
        new BlockoutShapeItemViewModel("Box", "Box: floors, platforms, crates, buildings", () => new BlockoutBox(), new Vector3(2, 2, 2), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Ramp", "Ramp rising toward where the drag ends", () => new BlockoutRamp(), new Vector3(2, 1, 4), BlockoutPlacement.Rise),
        new BlockoutShapeItemViewModel("Stairs", "Stairs climbing toward where the drag ends (steps of about 20 cm; characters collide with a ramp)", () => new BlockoutStairs(), new Vector3(2, 2, 4), BlockoutPlacement.Rise),
        new BlockoutShapeItemViewModel("Wall", "Wall along the drag (drag a line for the default thickness)", () => new BlockoutWall(), new Vector3(4, 3, 0.3f), BlockoutPlacement.Span),
        new BlockoutShapeItemViewModel("Doorway", "Wall with a door in the middle", () => Wall(new BlockoutOpening { Width = 1.2f, Height = 2.2f }), new Vector3(4, 3, 0.3f), BlockoutPlacement.Span),
        new BlockoutShapeItemViewModel("Window", "Wall with a window in the middle", () => Wall(new BlockoutOpening { Bottom = 1.0f, Width = 1.4f, Height = 1.2f }), new Vector3(4, 3, 0.3f), BlockoutPlacement.Span),
        new BlockoutShapeItemViewModel("Arch", "Wall with an arched passage", () => Wall(new BlockoutOpening { Width = 2.0f, Height = 2.8f, Arched = true }), new Vector3(3, 3.5f, 0.6f), BlockoutPlacement.Span),
        new BlockoutShapeItemViewModel("Cylinder", "Cylinder: pillars, columns, tanks", () => new BlockoutCylinder(), new Vector3(1, 3, 1), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Tower", "Hollow cylinder: towers, wells, pipes", () => new BlockoutCylinder { Sides = 24, WallThickness = 0.3f }, new Vector3(4, 6, 4), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Curved wall", "Quarter of a hollow cylinder filling the footprint: rounded corners", () => new BlockoutCylinder { Sides = 32, Sweep = 90, WallThickness = 0.3f }, new Vector3(4, 3, 4), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Cone", "Cone", () => new BlockoutCone(), new Vector3(2, 2, 2), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Pyramid", "Pyramid: roofs, spires", () => new BlockoutCone { Sides = 4, Smooth = false }, new Vector3(2, 2, 2), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Sphere", "Sphere", () => new BlockoutSphere(), new Vector3(2, 2, 2), BlockoutPlacement.Free),
        new BlockoutShapeItemViewModel("Dome", "Half sphere on the ground: domes, mounds", () => new BlockoutSphere { Hemisphere = true }, new Vector3(4, 2, 4), BlockoutPlacement.Free),
    };

    private static BlockoutWall Wall(BlockoutOpening opening)
    {
        var wall = new BlockoutWall();
        wall.Openings.Add(opening);
        return wall;
    }
}

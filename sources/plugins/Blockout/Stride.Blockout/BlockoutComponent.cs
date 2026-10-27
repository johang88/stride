// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Blockout.Rendering;
using Stride.Blockout.Shapes;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Rendering;

namespace Stride.Blockout;

/// <summary>
/// A simple shape to block out a level: a box, ramp, stairs, wall with openings, cylinder, cone or sphere, with a
/// procedural pattern material. Its mesh is generated when the scene loads; with a static collider using
/// <see cref="Physics.BlockoutCollider"/> on the same entity, characters walk on it and navigation meshes include it.
/// </summary>
/// <remarks>
/// The entity origin is at the bottom center of the shape: X spans the width, Y the height, Z the depth, and shapes that
/// rise (ramps, stairs) climb toward the front (-Z).
/// </remarks>
[DataContract("BlockoutComponent")]
[Display("Blockout shape", Expand = ExpandRule.Once)]
[DefaultEntityComponentProcessor(typeof(BlockoutProcessor), ExecutionMode = ExecutionMode.All)]
[ComponentCategory("Model")]
[ComponentOrder(11100)]
public sealed class BlockoutComponent : ActivableEntityComponent
{
    private readonly object geometryLock = new();
    private int geometryHash;
    private BlockoutGeometry geometry;
    private BlockoutGeometry collisionGeometry;

    /// <summary>
    /// The shape and its settings.
    /// </summary>
    [DataMember(10)]
    [NotNull]
    [Display(Expand = ExpandRule.Always)]
    public BlockoutShape Shape { get; set; } = new BlockoutBox();

    /// <summary>
    /// Width (X), height (Y) and depth (Z) in meters.
    /// </summary>
    [DataMember(20)]
    public Vector3 Size { get; set; } = new(2, 2, 2);

    /// <summary>
    /// The pattern and color.
    /// </summary>
    [DataMember(30)]
    [NotNull]
    [Display(Expand = ExpandRule.Always)]
    public BlockoutStyle Style { get; set; } = new();

    /// <summary>
    /// A material used instead of the pattern (its texture coordinates are in meters along each face).
    /// </summary>
    [DataMember(40)]
    [Display("Material override")]
    [System.ComponentModel.DefaultValue(null)]
    public Material Material { get; set; }

    [DataMember(50)]
    [Display("Cast shadows")]
    public bool CastShadows { get; set; } = true;

    [DataMember(60)]
    [Display("Render group")]
    public RenderGroup RenderGroup { get; set; }

    /// <summary>
    /// The size, at least <see cref="BlockoutShape.MinSize"/> along each axis.
    /// </summary>
    [DataMemberIgnore]
    public Vector3 ValidSize => Vector3.Max(Size, new Vector3(BlockoutShape.MinSize));

    /// <summary>
    /// Changes when the triangles of the shape change.
    /// </summary>
    [DataMemberIgnore]
    public int GeometryHash
    {
        get
        {
            var hash = new HashCode();
            hash.Add(ValidSize);
            hash.Add(Shape?.GetType());
            Shape?.AddToHash(ref hash);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// The triangles of the shape, in the local space of the entity (built on first use after a change).
    /// </summary>
    public BlockoutGeometry GetGeometry()
    {
        lock (geometryLock)
        {
            Update();
            return geometry;
        }
    }

    /// <summary>
    /// The triangles of the collision shape (the visible ones, or simpler ones such as a ramp for stairs).
    /// </summary>
    public BlockoutGeometry GetCollisionGeometry()
    {
        lock (geometryLock)
        {
            Update();
            return collisionGeometry;
        }
    }

    private void Update()
    {
        var hash = GeometryHash;
        if (geometry != null && hash == geometryHash)
            return;
        geometryHash = hash;
        var size = ValidSize;
        if (Shape == null)
        {
            geometry = collisionGeometry = BlockoutGeometry.Empty;
            return;
        }
        var builder = new BlockoutMeshBuilder();
        Shape.Build(builder, size);
        geometry = builder.ToGeometry();
        if (Shape.HasSeparateCollision)
        {
            builder = new BlockoutMeshBuilder();
            Shape.BuildCollision(builder, size);
            collisionGeometry = builder.ToGeometry();
        }
        else
        {
            collisionGeometry = geometry;
        }
    }
}

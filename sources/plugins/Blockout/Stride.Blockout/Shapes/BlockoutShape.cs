// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Shapes;

/// <summary>
/// The shape of a <see cref="BlockoutComponent"/>, built inside its size: X from -width/2 to width/2, Y from 0 (the entity
/// origin is at the bottom center) to the height, Z from -depth/2 to depth/2. Shapes that rise or open in a direction face
/// -Z (the forward of the entity).
/// </summary>
[DataContract(Inherited = true)]
public abstract class BlockoutShape
{
    /// <summary>
    /// The smallest size along each axis.
    /// </summary>
    public const float MinSize = 0.01f;

    /// <summary>
    /// Adds the triangles of the shape.
    /// </summary>
    public abstract void Build(BlockoutMeshBuilder builder, Vector3 size);

    /// <summary>
    /// Adds the triangles of the collision shape (by default the visible ones).
    /// </summary>
    public virtual void BuildCollision(BlockoutMeshBuilder builder, Vector3 size) => Build(builder, size);

    /// <summary>
    /// Whether the collision shape differs from the visible one.
    /// </summary>
    public virtual bool HasSeparateCollision => false;

    /// <summary>
    /// Adds the settings that change the triangles.
    /// </summary>
    public abstract void AddToHash(ref HashCode hash);

    /// <summary>
    /// A copy of the shape and its settings.
    /// </summary>
    public BlockoutShape Clone() => (BlockoutShape)MemberwiseCloneDeep();

    protected virtual object MemberwiseCloneDeep() => MemberwiseClone();
}

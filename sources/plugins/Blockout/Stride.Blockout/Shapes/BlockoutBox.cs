// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;

namespace Stride.Blockout.Shapes;

/// <summary>
/// A box filling the size.
/// </summary>
[DataContract("BlockoutBox")]
[Display("Box")]
public sealed class BlockoutBox : BlockoutShape
{
    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        builder.AddBox(new Vector3(-size.X * 0.5f, 0, -size.Z * 0.5f), new Vector3(size.X * 0.5f, size.Y, size.Z * 0.5f));
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(1);
    }
}

/// <summary>
/// A wedge rising to the full height at the front (-Z).
/// </summary>
[DataContract("BlockoutRamp")]
[Display("Ramp")]
public sealed class BlockoutRamp : BlockoutShape
{
    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var x0 = -size.X * 0.5f;
        var x1 = size.X * 0.5f;
        var zBack = size.Z * 0.5f;
        var zFront = -size.Z * 0.5f;
        var h = size.Y;

        builder.AddFaceY(0, x0, x1, zFront, zBack, -1);
        builder.AddFaceZ(zFront, x0, x1, 0, h, -1);
        // The slope, up toward the front
        builder.AddQuad(new Vector3(x0, 0, zBack), new Vector3(x1, 0, zBack), new Vector3(x1, h, zFront), new Vector3(x0, h, zFront), new Vector3(0, size.Z, h));
        builder.AddPolygon(stackalloc[] { new Vector3(x0, 0, zBack), new Vector3(x0, 0, zFront), new Vector3(x0, h, zFront) }, -Vector3.UnitX);
        builder.AddPolygon(stackalloc[] { new Vector3(x1, 0, zBack), new Vector3(x1, 0, zFront), new Vector3(x1, h, zFront) }, Vector3.UnitX);
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(2);
    }
}

/// <summary>
/// Steps climbing toward the front (-Z), solid underneath.
/// </summary>
[DataContract("BlockoutStairs")]
[Display("Stairs")]
public sealed class BlockoutStairs : BlockoutShape
{
    /// <summary>
    /// The height of a step the stairs get closest to: the number of steps is the height divided by it, rounded.
    /// </summary>
    [DataMember(10)]
    [Display("Step height (m)")]
    [DataMemberRange(0.05, 2, 0.01, 0.05, 2)]
    public float StepHeight { get; set; } = 0.2f;

    /// <summary>
    /// Whether characters and navigation see a smooth slope instead of the steps.
    /// </summary>
    [DataMember(20)]
    [Display("Collide as a ramp")]
    public bool CollideAsRamp { get; set; } = true;

    /// <summary>
    /// The number of steps for a height.
    /// </summary>
    public int GetStepCount(float height) => Math.Clamp((int)MathF.Round(height / Math.Max(StepHeight, 0.01f)), 1, 256);

    public override void Build(BlockoutMeshBuilder builder, Vector3 size)
    {
        var x0 = -size.X * 0.5f;
        var x1 = size.X * 0.5f;
        var zBack = size.Z * 0.5f;
        var count = GetStepCount(size.Y);
        var rise = size.Y / count;
        var run = size.Z / count;

        builder.AddFaceY(0, x0, x1, -zBack, zBack, -1);
        builder.AddFaceZ(-zBack, x0, x1, 0, size.Y, -1);
        for (int i = 0; i < count; i++)
        {
            var zNear = zBack - i * run;
            var zFar = zBack - (i + 1) * run;
            var top = (i + 1) * rise;
            // Riser, tread, and the sides of the column under the step
            builder.AddFaceZ(zNear, x0, x1, i * rise, top, 1);
            builder.AddFaceY(top, x0, x1, zFar, zNear, 1);
            builder.AddFaceX(x0, 0, top, zFar, zNear, -1);
            builder.AddFaceX(x1, 0, top, zFar, zNear, 1);
        }
    }

    public override bool HasSeparateCollision => CollideAsRamp;

    public override void BuildCollision(BlockoutMeshBuilder builder, Vector3 size)
    {
        if (!CollideAsRamp)
        {
            Build(builder, size);
            return;
        }
        // A slope through the middle of the treads (half a step under the noses), flat on the last tread
        var zBack = size.Z * 0.5f;
        var count = GetStepCount(size.Y);
        var rise = size.Y / count;
        var run = size.Z / count;
        builder.AddExtrudedX(stackalloc[]
        {
            new Vector2(zBack, 0),
            new Vector2(zBack, rise * 0.5f),
            new Vector2(-zBack + run * 0.5f, size.Y),
            new Vector2(-zBack, size.Y),
            new Vector2(-zBack, 0),
        }, -size.X * 0.5f, size.X * 0.5f);
    }

    public override void AddToHash(ref HashCode hash)
    {
        hash.Add(3);
        hash.Add(StepHeight);
        hash.Add(CollideAsRamp);
    }
}

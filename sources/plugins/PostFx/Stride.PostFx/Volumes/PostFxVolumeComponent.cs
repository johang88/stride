// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.PostFx;

/// <summary>
/// The region a post-processing volume affects.
/// </summary>
public enum PostFxVolumeShape
{
    /// <summary>
    /// The whole scene.
    /// </summary>
    Global,

    /// <summary>
    /// A box (the entity's transform, the size along its axes).
    /// </summary>
    Box,

    /// <summary>
    /// A sphere (the size's X is its radius).
    /// </summary>
    Sphere,
}

/// <summary>
/// Post-processing settings for a region of the scene (or the whole of it): the volumes around the camera are blended
/// by priority and weight, each overriding the groups of settings it enables. The renderer's quality settings
/// (what runs, at which resolution) stay in the graphics compositor; volumes set the look.
/// </summary>
[DataContract("PostFxVolumeComponent")]
[Display("Post-processing volume", Expand = ExpandRule.Once)]
[DefaultEntityComponentProcessor(typeof(PostFxVolumeProcessor))]
[ComponentCategory("Post-processing")]
[ComponentOrder(12200)]
public sealed class PostFxVolumeComponent : ActivableEntityComponent
{
    /// <summary>
    /// The region the volume affects.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(PostFxVolumeShape.Global)]
    public PostFxVolumeShape Shape { get; set; } = PostFxVolumeShape.Global;

    /// <summary>
    /// Size of the box (meters, along the entity's axes), or the sphere's radius (X).
    /// </summary>
    [DataMember(20)]
    public Vector3 Size { get; set; } = new(10.0f);

    /// <summary>
    /// Distance outside the shape over which the volume fades out (meters).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(5.0f)]
    [DataMemberRange(0, 1000, 0.1, 1, 1)]
    public float BlendDistance { get; set; } = 5.0f;

    /// <summary>
    /// Order of the volumes: a higher priority is applied over a lower one.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(0.0f)]
    public float Priority { get; set; }

    /// <summary>
    /// Overall strength of the volume.
    /// </summary>
    [DataMember(50)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 1, 0.01, 0.1, 2)]
    public float Weight { get; set; } = 1.0f;

    /// <summary>
    /// The settings: enable the groups the volume overrides.
    /// </summary>
    [DataMember(60)]
    [NotNull]
    [Display(Expand = ExpandRule.Always)]
    public PostFxProfile Profile { get; set; } = new();

    /// <summary>
    /// How much the volume applies at a point (0-1, its weight included).
    /// </summary>
    public float GetInfluence(Vector3 position)
    {
        if (Shape == PostFxVolumeShape.Global)
            return Math.Clamp(Weight, 0.0f, 1.0f);

        // Into the volume's space without its scale (sizes are in meters)
        Entity.Transform.WorldMatrix.Decompose(out _, out Quaternion rotation, out var translation);
        var local = Vector3.Transform(position - translation, Quaternion.Invert(rotation));
        float distance;
        if (Shape == PostFxVolumeShape.Box)
        {
            var outside = Vector3.Max(new Vector3(Math.Abs(local.X), Math.Abs(local.Y), Math.Abs(local.Z)) - Size * 0.5f, Vector3.Zero);
            distance = outside.Length();
        }
        else
        {
            distance = Math.Max(local.Length() - Size.X, 0.0f);
        }
        var fade = BlendDistance > 0.0f ? 1.0f - Math.Clamp(distance / BlendDistance, 0.0f, 1.0f) : (distance > 0.0f ? 0.0f : 1.0f);
        return fade * Math.Clamp(Weight, 0.0f, 1.0f);
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.Water;

/// <summary>
/// Makes ripples in the water the entity wades through (a <see cref="WaterSurfaceComponent"/>'s surface between the entity's
/// origin, at its feet, and <see cref="Height"/> above): a wake while it moves, its steps, a splash when it enters, small
/// disturbances while it stands still.
/// </summary>
[DataContract("WaterRippleEmitterComponent")]
[Display("Water Ripple Emitter", Expand = ExpandRule.Once)]
[DefaultEntityComponentProcessor(typeof(WaterRippleEmitterProcessor), ExecutionMode = ExecutionMode.All)]
[ComponentCategory("Water")]
public sealed class WaterRippleEmitterComponent : ActivableEntityComponent
{
    /// <userdoc>Radius of the entity's footprint in the water (meters).</userdoc>
    [DataMember(10)]
    [DefaultValue(0.3f)]
    [DataMemberRange(0.02, 20, 0.01, 0.1, 2)]
    public float Radius { get; set; } = 0.3f;

    /// <userdoc>Height of the entity above its origin (meters): it disturbs the water while the surface is between its origin and this height.</userdoc>
    [DataMember(20)]
    [DefaultValue(1.8f)]
    [DataMemberRange(0.01, 100, 0.05, 0.5, 2)]
    public float Height { get; set; } = 1.8f;

    /// <userdoc>Scale of the disturbances.</userdoc>
    [DataMember(30)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.05, 0.25, 2)]
    public float Strength { get; set; } = 1.0f;

    /// <userdoc>Distance between steps while moving (meters); 0: no steps, only the wake.</userdoc>
    [DataMember(40)]
    [DefaultValue(0.7f)]
    [DataMemberRange(0, 10, 0.05, 0.25, 2)]
    public float StepLength { get; set; } = 0.7f;

    /// <userdoc>Distance between the left and right feet (meters).</userdoc>
    [DataMember(50)]
    [DefaultValue(0.22f)]
    [DataMemberRange(0, 5, 0.01, 0.1, 2)]
    public float StepWidth { get; set; } = 0.22f;
}

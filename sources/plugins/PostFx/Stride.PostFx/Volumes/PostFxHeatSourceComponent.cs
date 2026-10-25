// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Engine;
using Stride.Engine.Design;

namespace Stride.PostFx;

/// <summary>
/// Hot air rising (a fire, a forge, an exhaust): what is seen through it shimmers. A vertical column from the entity up,
/// strongest at its base.
/// </summary>
[DataContract("PostFxHeatSourceComponent")]
[Display("Heat haze source", Expand = ExpandRule.Once)]
[DefaultEntityComponentProcessor(typeof(PostFxHeatSourceProcessor))]
[ComponentCategory("Post-processing")]
[ComponentOrder(12210)]
public sealed class PostFxHeatSourceComponent : ActivableEntityComponent
{
    /// <summary>
    /// Radius of the column of hot air (meters).
    /// </summary>
    [DataMember(10)]
    [DefaultValue(0.6f)]
    [DataMemberRange(0.05, 50, 0.05, 0.5, 2)]
    public float Radius { get; set; } = 0.6f;

    /// <summary>
    /// Height the hot air rises before it has cooled (meters).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(2.5f)]
    [DataMemberRange(0.1, 200, 0.1, 1, 1)]
    public float Height { get; set; } = 2.5f;

    /// <summary>
    /// How much it bends the light (1: a campfire's).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(1.0f)]
    [DataMemberRange(0, 10, 0.05, 0.5, 2)]
    public float Strength { get; set; } = 1.0f;

    /// <summary>
    /// How fast the shimmer rises (meters per second).
    /// </summary>
    [DataMember(40)]
    [DefaultValue(1.5f)]
    [DataMemberRange(0, 20, 0.1, 1, 1)]
    public float RiseSpeed { get; set; } = 1.5f;
}

/// <summary>
/// Keeps the <see cref="PostFxVolumes"/> service up to date with the heat sources of the scenes.
/// </summary>
public sealed class PostFxHeatSourceProcessor : EntityProcessor<PostFxHeatSourceComponent>
{
    private PostFxVolumes volumes;

    protected override void OnSystemAdd()
    {
        volumes = PostFxVolumes.GetOrCreate(Services);
    }

    protected override void OnEntityComponentAdding(Entity entity, PostFxHeatSourceComponent component, PostFxHeatSourceComponent data)
    {
        volumes.HeatSources.Add(component);
    }

    protected override void OnEntityComponentRemoved(Entity entity, PostFxHeatSourceComponent component, PostFxHeatSourceComponent data)
    {
        volumes.HeatSources.Remove(component);
    }
}

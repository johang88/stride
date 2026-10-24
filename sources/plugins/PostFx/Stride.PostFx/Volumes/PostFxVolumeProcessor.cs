// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Stride.PostFx;

/// <summary>
/// The post-processing volumes of the scenes, for the renderer to blend at its cameras.
/// </summary>
public sealed class PostFxVolumes
{
    private readonly List<PostFxVolumeComponent> volumes = new();
    private readonly List<(PostFxVolumeComponent Volume, float Influence)> active = new();

    /// <summary>
    /// Temporary overrides of the game (dialogue focus, low health, menus), applied over the volumes in their order:
    /// each blends the groups it overrides by its weight.
    /// </summary>
    public List<(PostFxProfile Profile, float Weight)> Overrides { get; } = new();

    internal void Add(PostFxVolumeComponent volume) => volumes.Add(volume);

    internal void Remove(PostFxVolumeComponent volume) => volumes.Remove(volume);

    /// <summary>
    /// Blends the volumes affecting a point (the camera) into a profile, over its current values (the defaults).
    /// </summary>
    public void Resolve(Vector3 position, PostFxProfile result)
    {
        active.Clear();
        foreach (var volume in volumes)
        {
            if (!volume.Enabled || volume.Entity?.Scene == null)
                continue;
            var influence = volume.GetInfluence(position);
            if (influence > 0.0f)
                active.Add((volume, influence));
        }
        // Lower priorities first; at equal priority, global volumes under local ones
        active.Sort((a, b) => a.Volume.Priority != b.Volume.Priority
            ? a.Volume.Priority.CompareTo(b.Volume.Priority)
            : (a.Volume.Shape == PostFxVolumeShape.Global ? 0 : 1).CompareTo(b.Volume.Shape == PostFxVolumeShape.Global ? 0 : 1));
        foreach (var (volume, influence) in active)
            result.BlendTowards(volume.Profile, influence);
        foreach (var (profile, weight) in Overrides)
        {
            if (profile != null && weight > 0.0f)
                result.BlendTowards(profile, Math.Min(weight, 1.0f));
        }
    }
}

/// <summary>
/// Keeps the <see cref="PostFxVolumes"/> service up to date with the volumes of the scenes.
/// </summary>
public sealed class PostFxVolumeProcessor : EntityProcessor<PostFxVolumeComponent>
{
    private PostFxVolumes volumes;

    protected override void OnSystemAdd()
    {
        volumes = Services.GetService<PostFxVolumes>();
        if (volumes == null)
        {
            volumes = new PostFxVolumes();
            Services.AddService(volumes);
        }
    }

    protected override void OnEntityComponentAdding(Entity entity, PostFxVolumeComponent component, PostFxVolumeComponent data)
    {
        volumes.Add(component);
    }

    protected override void OnEntityComponentRemoved(Entity entity, PostFxVolumeComponent component, PostFxVolumeComponent data)
    {
        volumes.Remove(component);
    }
}

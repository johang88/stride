// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering;

namespace Stride.Decals;

/// <summary>
/// A decal as the renderer sees it.
/// </summary>
public sealed class RenderDecal
{
    /// <summary>
    /// The unit box (-0.5 to 0.5) to world.
    /// </summary>
    public Matrix BoxToWorld;

    /// <summary>
    /// World to the unit box.
    /// </summary>
    public Matrix WorldToBox;

    /// <summary>
    /// World bounds of the box.
    /// </summary>
    public BoundingBoxExt Bounds;

    public DecalComponent Component;

    /// <summary>
    /// Position among the decals of the same order (when it was added).
    /// </summary>
    public long Sequence;
}

/// <summary>
/// Hands the enabled <see cref="DecalComponent"/>s of the scene to the decal renderers (<see cref="DecalRenderer.CurrentDecals"/>).
/// </summary>
public sealed class DecalProcessor : EntityProcessor<DecalComponent, RenderDecal>, IEntityComponentRenderProcessor
{
    private readonly List<RenderDecal> activeDecals = new();
    private long nextSequence;

    public DecalProcessor() : base(typeof(TransformComponent))
    {
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    protected override void OnSystemAdd()
    {
        base.OnSystemAdd();
        VisibilityGroup.Tags.Set(DecalRenderer.CurrentDecals, activeDecals);
    }

    protected override void OnSystemRemove()
    {
        VisibilityGroup.Tags.Set(DecalRenderer.CurrentDecals, null);
        base.OnSystemRemove();
    }

    protected override RenderDecal GenerateComponentData(Entity entity, DecalComponent component) => new() { Component = component, Sequence = nextSequence++ };

    protected override bool IsAssociatedDataValid(Entity entity, DecalComponent component, RenderDecal associatedData) => associatedData.Component == component;

    public override void Draw(RenderContext context)
    {
        activeDecals.Clear();
        foreach (var (component, decal) in ComponentDatas)
        {
            if (!component.Enabled || component.Opacity <= 0 || component.Color.A <= 0 || component.Surface == null)
                continue;
            var size = component.Size;
            if (size.X <= 1e-4f || size.Y <= 1e-4f || size.Z <= 1e-4f)
                continue;

            decal.BoxToWorld = Matrix.Scaling(size) * component.Entity.Transform.WorldMatrix;
            if (MathUtil.IsZero(decal.BoxToWorld.Determinant()))
                continue;
            Matrix.Invert(ref decal.BoxToWorld, out decal.WorldToBox);
            var bounds = new BoundingBoxExt(new Vector3(-0.5f), new Vector3(0.5f));
            bounds.Transform(decal.BoxToWorld);
            decal.Bounds = bounds;
            activeDecals.Add(decal);
        }
    }
}

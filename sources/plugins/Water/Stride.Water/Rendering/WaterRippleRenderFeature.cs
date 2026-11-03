// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Water.Rendering;

/// <summary>
/// Sub render feature of the <see cref="MeshRenderFeature"/> running the dynamic ripples of the scene once per frame
/// (<see cref="WaterRippleSimulation"/>, around the main camera's view of the water) and giving every view their heights
/// (the PerView.WaterRipples group read by the Water Normal material node). Added by the <see cref="WaterSurfaceProcessor"/>.
/// </summary>
[DataContract("WaterRippleRenderFeature")]
public sealed class WaterRippleRenderFeature : SubRenderFeature
{
    private readonly Dictionary<ObjectId, ParameterCollection> layoutParameters = new();
    private readonly List<Vector4> disturbances = new();
    private LogicalGroupReference viewKey;
    private WaterRippleSimulation simulation;
    private bool enabled;
    private float elapsed;
    private float level;
    private long simulatedFrame = -1;

    protected override void InitializeCore()
    {
        base.InitializeCore();
        viewKey = ((RootEffectRenderFeature)RootRenderFeature).CreateViewLogicalGroup("WaterRipples");
    }

    /// <summary>
    /// The frame's input from the scene: whether any water ripples, the game time elapsed, the water's height (where the
    /// window centers on the camera's view) and the disturbances.
    /// </summary>
    internal void Submit(bool rippling, float elapsedTime, float waterLevel, ReadOnlySpan<Vector4> frameDisturbances)
    {
        enabled = rippling;
        elapsed += elapsedTime;
        level = waterLevel;
        foreach (var disturbance in frameDisturbances)
            disturbances.Add(disturbance);
    }

    public override void Prepare(RenderDrawContext context)
    {
        base.Prepare(context);

        if (simulatedFrame != RenderSystem.FrameCounter)
        {
            simulatedFrame = RenderSystem.FrameCounter;
            if (enabled && TryGetFocus(out var focus))
            {
                simulation ??= new WaterRippleSimulation(Context);
                simulation.Update(context, elapsed, focus, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(disturbances));
            }
            else
            {
                simulation?.Suspend();
            }
            elapsed = 0.0f;
            disturbances.Clear();
        }

        var active = simulation?.IsActive ?? false;
        var heights = active ? simulation.Heights : context.GraphicsDevice.GetSharedWhiteTexture();
        var window = active ? simulation.Window : Vector4.Zero;
        var fade = active ? simulation.Fade : Vector4.Zero;
        foreach (var view in RenderSystem.Views)
        {
            var viewFeature = view.Features[RootRenderFeature.Index];
            foreach (var viewLayout in viewFeature.Layouts)
            {
                if (viewLayout.State != RenderEffectState.Normal)
                    continue;
                var group = viewLayout.GetLogicalGroup(viewKey);
                if (group.Hash == ObjectId.Empty)
                    continue;
                if (!layoutParameters.TryGetValue(group.Hash, out var parameters))
                {
                    var layout = new ParameterCollectionLayout();
                    layout.ProcessLogicalGroup(viewLayout, ref group);
                    parameters = new ParameterCollection();
                    parameters.UpdateLayout(layout);
                    layoutParameters.Add(group.Hash, parameters);
                }

                parameters.Set(WaterRipplesKeys.WaterRippleHeight, heights);
                parameters.Set(WaterRipplesKeys.WaterRippleWindow, window);
                parameters.Set(WaterRipplesKeys.WaterRippleFade, fade);
                viewLayout.Entries[view.Index].Resources.UpdateLogicalGroup(ref group, parameters);
            }
        }
    }

    /// <summary>
    /// Where the main camera looks at the water: its view axis meets the water's height, not too far from the camera.
    /// </summary>
    private bool TryGetFocus(out Vector2 focus)
    {
        foreach (var view in RenderSystem.Views)
        {
            // (camera views only: shadow views are subclasses)
            if (view.GetType() != typeof(RenderView))
                continue;
            Matrix.Invert(ref view.View, out var viewInverse);
            var eye = viewInverse.TranslationVector;
            // (the camera looks down its local -Z)
            var forward = Vector3.Normalize(new Vector3(-viewInverse.M31, -viewInverse.M32, -viewInverse.M33));
            var reach = WaterRippleSimulation.Size * WaterRippleSimulation.Cell * 0.3f;
            var distance = forward.Y < -1e-3f ? MathF.Min((eye.Y - level) / -forward.Y, reach) : 0.0f;
            distance = MathF.Max(distance, 0.0f);
            var point = eye + forward * distance;
            focus = new Vector2(point.X, point.Z);
            return true;
        }
        focus = default;
        return false;
    }

    protected override void Destroy()
    {
        simulation?.Dispose();
        simulation = null;
        base.Destroy();
    }
}

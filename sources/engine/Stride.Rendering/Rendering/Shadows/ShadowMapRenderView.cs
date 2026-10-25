// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;

namespace Stride.Rendering.Shadows
{
    /// <summary>
    /// A view used to render a shadow map to a <see cref="LightShadowMapTexture"/>
    /// </summary>
    public class ShadowMapRenderView : RenderView
    {
        public ShadowMapRenderView()
        {
            VisiblityIgnoreDepthPlanes = true;
        }

        /// <summary>
        /// The view for which this shadow map is rendered
        /// </summary>
        public RenderView RenderView;

        /// <summary>
        /// The shadow map to render
        /// </summary>
        public LightShadowMapTexture ShadowMapTexture;

        /// <summary>
        /// The rectangle to render to in the shadow map
        /// </summary>
        public Rectangle Rectangle;

        public ProfilingKey ProfilingKey { get; } = new ProfilingKey($"ShadowMapRenderView");

        /// <summary>
        /// The volume of the points reading this shadow map (planes facing inwards, none: no culling) and the direction the light
        /// travels: a caster whose shadow cannot reach the volume is not drawn (see <see cref="CastsOnReceivers"/>).
        /// </summary>
        internal readonly Plane[] ReceiverPlanes = new Plane[6];
        internal int ReceiverPlaneCount;
        internal Vector3 LightDirection;
        // Added to the caster bounds: the reach of the shadow filter and normal offset
        internal float CasterMargin;

        /// <summary>
        /// Whether the shadow of a caster in <paramref name="bounds"/>, swept along the light, enters the receiver volume.
        /// </summary>
        internal bool CastsOnReceivers(ref BoundingBoxExt bounds)
        {
            // The sweep of the bounding sphere's center, clipped by the receiver planes moved out by its radius
            float radius = bounds.Extent.Length() + CasterMargin;
            float enter = 0, exit = float.MaxValue;
            for (int i = 0; i < ReceiverPlaneCount; i++)
            {
                ref var plane = ref ReceiverPlanes[i];
                float distance = Vector3.Dot(plane.Normal, bounds.Center) + plane.D + radius;
                float rate = Vector3.Dot(plane.Normal, LightDirection);
                if (rate > 1e-6f)
                    enter = MathF.Max(enter, -distance / rate);
                else if (rate < -1e-6f)
                    exit = MathF.Min(exit, -distance / rate);
                else if (distance < 0)
                    return false;
                if (enter > exit)
                    return false;
            }
            return true;
        }
        
        internal ParameterCollection ViewParameters = new ParameterCollection();
    }
}

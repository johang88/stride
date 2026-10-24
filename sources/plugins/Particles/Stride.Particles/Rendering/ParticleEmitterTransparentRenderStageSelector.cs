// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Engine;
using Stride.Rendering;

namespace Stride.Particles.Rendering
{
    public class ParticleEmitterTransparentRenderStageSelector : TransparentRenderStageSelector
    {
        public override void Process(RenderObject renderObject)
        {
            if (TransparentRenderStage != null && ((RenderGroupMask)(1U << (int)renderObject.RenderGroup) & RenderGroup) != 0)
            {
                var renderParticleEmitter = (RenderParticleEmitter)renderObject;
                // An emitter without a material simulates but draws nothing (its particles feed other systems, e.g. volumetric smoke)
                var material = renderParticleEmitter.ParticleEmitter.Material;
                if (material == null)
                    return;
                var effectName = material.EffectName;

                renderObject.ActiveRenderStages[TransparentRenderStage.Index] = new ActiveRenderStage(effectName);
            }
        }
    }
}

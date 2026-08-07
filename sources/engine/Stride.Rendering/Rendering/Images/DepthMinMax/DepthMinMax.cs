// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;

using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Rendering.Images
{
    public class DepthMinMax : ImageEffect
    {
        internal static PermutationParameterKey<bool> IsFirstPassKey = ParameterKeys.NewPermutation<bool>();

        // TODO: Currently capturing two effects, because sdfx permutation triggers DynamicEffectCompiler
        private ImageEffectShader effectFirstPass;
        private ImageEffectShader effectNotFirstPass;

        private ImageReadback<Vector2> readback;

        public DepthMinMax()
        {
        }

        protected override void InitializeCore()
        {
            base.InitializeCore();

            effectFirstPass = ToLoadAndUnload(new ImageEffectShader("DepthMinMaxEffect"));
            effectNotFirstPass = ToLoadAndUnload(new ImageEffectShader("DepthMinMaxEffect"));
            readback = ToLoadAndUnload(new ImageReadback<Vector2>());
        }

        public bool IsResultAvailable { get; private set; }

        /// <summary>
        /// The depth of the closest fragment in X and of the furthest non-background one in Y, as raw depth-buffer
        /// values. Views use a reversed depth range, so X is the greater of the two.
        /// </summary>
        public Vector2 Result { get; private set; }

        protected override void DrawCore(RenderDrawContext context)
        {
            var input = GetSafeInput(0);

            Texture fromTexture = input;
            Texture downTexture = null;
            var nextSize = Math.Max(MathUtil.NextPowerOfTwo(input.Size.Width), MathUtil.NextPowerOfTwo(input.Size.Height));
            bool isFirstPass = true;
            while (nextSize > 1)
            {
                nextSize = nextSize / 2;
                downTexture = NewScopedRenderTarget2D(nextSize, nextSize, PixelFormat.R32G32_Float, 1);

                var effect = isFirstPass ? effectFirstPass : effectNotFirstPass;
                effect.Parameters.Set(DepthMinMaxShaderKeys.TextureMap, fromTexture);
                effect.Parameters.Set(DepthMinMaxShaderKeys.TextureReduction, fromTexture);

                effect.SetOutput(downTexture);
                effect.Parameters.Set(IsFirstPassKey, isFirstPass);
                ((RendererBase)effect).Draw(context);

                fromTexture = downTexture;

                isFirstPass = false;
            }

            readback.SetInput(downTexture);
            readback.Draw(context);
            IsResultAvailable = readback.IsResultAvailable;
            if (IsResultAvailable)
            {
                float closest = -float.MaxValue;
                float furthest = float.MaxValue;
                var results = readback.Result;
                foreach (var result in results)
                {
                    closest = Math.Max(result.X, closest);
                    if (result.Y != 0.0f)
                    {
                        furthest = Math.Min(result.Y, furthest);
                    }
                }

                Result = new Vector2(closest, furthest);
            }
        }
    }
}

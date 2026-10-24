// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;

namespace Stride.PostFx.Passes;

/// <summary>
/// The glow of bright light: a pyramid of half resolutions down to a few dozen pixels, filtered down (13 taps, Karis
/// average on the first level) and back up (tent, blended by the scatter), in 11:11:10 floats.
/// </summary>
internal sealed class BloomPass
{
    private const int MaxLevels = 8;

    private readonly ImageEffectShader downsample;
    private readonly ImageEffectShader upsample;
    private readonly Texture[] down = new Texture[MaxLevels];

    public BloomPass(ImageEffectShader downsample, ImageEffectShader upsample)
    {
        this.downsample = downsample;
        this.upsample = upsample;
    }

    /// <summary>
    /// The glow of the scene, at half its resolution.
    /// </summary>
    public Texture Draw(RenderDrawContext context, Func<int, int, PixelFormat, Texture> target, Texture scene, float scatter)
    {
        // Down to about 16 pixels on the shorter side
        var levels = 0;
        var width = scene.ViewWidth;
        var height = scene.ViewHeight;
        while (levels < MaxLevels && Math.Min(width, height) > 32)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            down[levels++] = target(width, height, PixelFormat.R11G11B10_Float);
        }
        if (levels == 0)
            return null;

        var source = scene;
        for (int i = 0; i < levels; i++)
        {
            downsample.Parameters.Set(PostFxBloomDownsampleKeys.BloomSourceTexel, new Vector2(1.0f / source.ViewWidth, 1.0f / source.ViewHeight));
            downsample.Parameters.Set(PostFxBloomDownsampleKeys.BloomFirstLevel, i == 0);
            downsample.SetInput(0, source);
            downsample.SetOutput(down[i]);
            downsample.Draw(context, "PostFx.BloomDown");
            source = down[i];
        }

        // Back up: each level blended with the wider one below it
        var wide = down[levels - 1];
        for (int i = levels - 2; i >= 0; i--)
        {
            var result = target(down[i].ViewWidth, down[i].ViewHeight, PixelFormat.R11G11B10_Float);
            upsample.Parameters.Set(PostFxBloomUpsampleKeys.BloomSourceTexel, new Vector2(1.0f / wide.ViewWidth, 1.0f / wide.ViewHeight));
            upsample.Parameters.Set(PostFxBloomUpsampleKeys.BloomScatter, Math.Clamp(scatter, 0.0f, 1.0f));
            upsample.SetInput(0, wide);
            upsample.SetInput(1, down[i]);
            upsample.SetOutput(result);
            upsample.Draw(context, "PostFx.BloomUp");
            wide = result;
        }
        Array.Clear(down);
        return wide;
    }
}

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
    private readonly ImageEffectShader streak;
    private readonly Texture[] down = new Texture[MaxLevels];
    private readonly Texture[] streakDown = new Texture[MaxLevels];

    public BloomPass(ImageEffectShader downsample, ImageEffectShader upsample, ImageEffectShader streak)
    {
        this.downsample = downsample;
        this.upsample = upsample;
        this.streak = streak;
    }

    /// <summary>
    /// The glow of the scene, at half its resolution, and its horizontal streak (at a quarter, when asked).
    /// </summary>
    public (Texture Glow, Texture Streak) Draw(RenderDrawContext context, Func<int, int, PixelFormat, Texture> target, Texture scene, float scatter, bool withStreak, float white)
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
            return (null, null);

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

        var streakResult = withStreak ? DrawStreak(context, target, down[Math.Min(1, levels - 1)], white) : null;

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
        return (wide, streakResult);
    }

    private Texture DrawStreak(RenderDrawContext context, Func<int, int, PixelFormat, Texture> target, Texture source, float white)
    {
        streak.Parameters.Set(PostFxStreakKeys.StreakThreshold, white);
        // Halving the width only, down to a few dozen pixels
        var levels = 0;
        var width = source.ViewWidth;
        var level = source;
        while (levels < MaxLevels && width > 32)
        {
            width = Math.Max(1, width / 2);
            streakDown[levels] = target(width, source.ViewHeight, PixelFormat.R11G11B10_Float);
            streak.Parameters.Set(PostFxStreakKeys.StreakSourceTexel, new Vector2(1.0f / level.ViewWidth, 1.0f / level.ViewHeight));
            streak.Parameters.Set(PostFxStreakKeys.StreakUp, false);
            streak.Parameters.Set(PostFxStreakKeys.StreakFirst, levels == 0);
            streak.SetInput(0, level);
            streak.SetOutput(streakDown[levels]);
            streak.Draw(context, "PostFx.StreakDown");
            level = streakDown[levels++];
        }

        var wide = level;
        for (int i = levels - 2; i >= -1; i--)
        {
            var own = i >= 0 ? streakDown[i] : source;
            var result = target(own.ViewWidth, own.ViewHeight, PixelFormat.R11G11B10_Float);
            streak.Parameters.Set(PostFxStreakKeys.StreakSourceTexel, new Vector2(1.0f / wide.ViewWidth, 1.0f / wide.ViewHeight));
            streak.Parameters.Set(PostFxStreakKeys.StreakUp, true);
            streak.Parameters.Set(PostFxStreakKeys.StreakFirst, false);
            streak.Parameters.Set(PostFxStreakKeys.StreakScatter, 0.9f);
            streak.SetInput(0, wide);
            streak.SetInput(1, own);
            streak.SetOutput(result);
            streak.Draw(context, "PostFx.StreakUp");
            wide = result;
        }
        Array.Clear(streakDown);
        return wide;
    }
}

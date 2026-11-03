// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Water.Rendering;

/// <summary>
/// The dynamic ripples on the GPU: the wave equation over a 64 m window of the world (6.25 cm cells) that follows the camera,
/// stepped at a fixed 60 Hz (WaterRippleStep). Two ping-pong height textures, wrapped by world cell so the window moves
/// without copies. Idle (no steps, no ripples drawn) after the water has been undisturbed long enough to settle.
/// </summary>
internal sealed class WaterRippleSimulation : IDisposable
{
    public const int Size = 1024;
    public const float Cell = 0.0625f;
    private const float StepTime = 1.0f / 60.0f;
    private const int MaxStepsPerFrame = 4;
    // Speed of the rings (m/s) and how fast they die out (1/s)
    private const float WaveSpeed = 1.1f;
    private const float DampingRate = 0.9f;
    // Absorbing border and the fade of the drawn ripples inside it
    private const float SpongeCells = 48;
    private const float FadeWidth = 6.0f;
    // Undisturbed time after which the water has settled
    private const float SettleTime = 12.0f;
    private const int MaxSources = 128;

    private readonly ComputeEffectShader step;
    private readonly Texture[] states = new Texture[2];
    private readonly Buffer sources;
    private readonly Vector4[] pending = new Vector4[MaxSources];
    private int pendingCount;
    private int current;
    private Int2 origin;
    private float accumulated;
    private float undisturbed = float.PositiveInfinity;
    private bool cleared;

    public WaterRippleSimulation(RenderContext context)
    {
        var device = context.GraphicsDevice;
        for (var i = 0; i < states.Length; i++)
        {
            states[i] = Texture.New2D(device, Size, Size, PixelFormat.R32G32_Float, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            states[i].Name = $"WaterRipples{i}";
        }
        sources = Buffer.Structured.New<Vector4>(device, MaxSources);
        sources.Name = "WaterRippleSources";
        step = new ComputeEffectShader(context) { ShaderSourceName = "WaterRippleStep" };
    }

    /// <summary>
    /// The current heights (null while idle).
    /// </summary>
    public Texture Heights => IsActive ? states[current] : null;

    public bool IsActive => undisturbed < SettleTime;

    /// <summary>
    /// x = 1 / world size of the texture, y = cell size (see WaterRipples.sdsl).
    /// </summary>
    public Vector4 Window => new(1.0f / (Size * Cell), Cell, 0, 0);

    /// <summary>
    /// xy = center of the window, z = half size where the ripples start fading, w = 1 / fade width (0 while idle).
    /// </summary>
    public Vector4 Fade
    {
        get
        {
            if (!IsActive)
                return Vector4.Zero;
            var center = (new Vector2(origin.X, origin.Y) + new Vector2(Size * 0.5f)) * Cell;
            return new Vector4(center, Size * Cell * 0.5f - SpongeCells * Cell, 1.0f / FadeWidth);
        }
    }

    /// <summary>
    /// Advances the simulation by the game's elapsed time, around a world position, with the frame's disturbances.
    /// </summary>
    public void Update(RenderDrawContext context, float elapsed, Vector2 focus, ReadOnlySpan<Vector4> disturbances)
    {
        foreach (var disturbance in disturbances)
        {
            if (pendingCount < MaxSources)
                pending[pendingCount++] = disturbance;
        }
        if (disturbances.Length > 0)
            undisturbed = 0.0f;
        else
            undisturbed += elapsed;

        if (!IsActive)
        {
            cleared = false;
            accumulated = 0.0f;
            pendingCount = 0;
            return;
        }

        var commandList = context.CommandList;
        if (!cleared)
        {
            // (waking up: whatever was left from before has settled)
            foreach (var state in states)
                commandList.ClearReadWrite(state, Vector4.Zero);
            cleared = true;
            origin = OriginAround(focus);
        }

        accumulated += elapsed;
        var steps = (int)(accumulated / StepTime);
        if (steps > MaxStepsPerFrame)
        {
            steps = MaxStepsPerFrame;
            accumulated = 0.0f;
        }
        else
        {
            accumulated -= steps * StepTime;
        }
        if (steps == 0)
            return;

        if (pendingCount > 0)
            sources.SetData(commandList, new ReadOnlySpan<Vector4>(pending, 0, pendingCount));

        var newOrigin = OriginAround(focus);
        var wave = WaveSpeed * StepTime / Cell;
        for (var i = 0; i < steps; i++)
        {
            var previousOrigin = i == 0 ? origin : newOrigin;
            step.Parameters.Set(WaterRippleStepKeys.WaterRipplePrevious, states[current]);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleNext, states[1 - current]);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleSources, sources);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleOrigin, newOrigin);
            step.Parameters.Set(WaterRippleStepKeys.WaterRipplePreviousOrigin, previousOrigin);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleSize, Size);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleCell, Cell);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleWave, wave * wave);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleDamping, MathF.Exp(-DampingRate * StepTime));
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleSponge, SpongeCells);
            step.Parameters.Set(WaterRippleStepKeys.WaterRippleSourceCount, i == 0 ? pendingCount : 0);
            step.ThreadNumbers = new Int3(8, 8, 1);
            step.ThreadGroupCounts = new Int3(Size / 8, Size / 8, 1);
            step.Draw(context, "Water.Ripples");
            commandList.ResourceBarrierTransition(states[1 - current], BarrierLayout.ShaderResource);
            current = 1 - current;
        }
        origin = newOrigin;
        pendingCount = 0;
    }

    /// <summary>
    /// Stops the ripples (no rippling water in the scene, or no camera): they start flat when the water is disturbed again.
    /// </summary>
    public void Suspend()
    {
        undisturbed = float.PositiveInfinity;
        cleared = false;
        accumulated = 0.0f;
        pendingCount = 0;
    }

    private static Int2 OriginAround(Vector2 focus)
        => new((int)MathF.Floor(focus.X / Cell) - Size / 2, (int)MathF.Floor(focus.Y / Cell) - Size / 2);

    public void Dispose()
    {
        step.Dispose();
        foreach (var state in states)
            state?.Dispose();
        sources.Dispose();
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;

namespace Stride.Water;

/// <summary>
/// Turns the movement of <see cref="WaterRippleEmitterComponent"/>s through water into disturbances of the dynamic ripples
/// (<see cref="WaterSurfaceProcessor.AddRipple"/>). Follows the game's scaled time: nothing while the game is paused.
/// </summary>
public sealed class WaterRippleEmitterProcessor : EntityProcessor<WaterRippleEmitterComponent, WaterRippleEmitterProcessor.EmitterState>
{
    // Depth of the water (m) at which a wading entity disturbs it fully (shallower: less)
    private const float FullDepth = 0.3f;
    // Height pushed down per second at the wake, per m/s of speed (m)
    private const float WakeHeight = 0.035f;
    // A step (m), at 4 m/s; scales with the square root of the speed
    private const float StepHeight = 0.08f;
    // Entering the water (m), per m/s of falling speed
    private const float SplashHeight = 0.03f;
    // Shifting weight while standing (m), every 1.2-3 s
    private const float IdleHeight = 0.02f;
    private const float MaxSpeed = 8.0f;

    public sealed class EmitterState
    {
        internal Vector3 Previous;
        internal bool HasPrevious;
        internal bool InWater;
        internal float StepDistance;
        internal bool LeftFoot;
        internal float IdleTimer;
    }

    private readonly Random random = new(1234);

    public WaterRippleEmitterProcessor() : base(typeof(TransformComponent))
    {
    }

    protected override EmitterState GenerateComponentData(Entity entity, WaterRippleEmitterComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, WaterRippleEmitterComponent component, EmitterState associatedData) => true;

    public override void Update(GameTime time)
    {
        var dt = (float)time.WarpElapsed.TotalSeconds;
        if (dt <= 0.0f)
            return;
        var water = EntityManager.GetProcessor<WaterSurfaceProcessor>();

        foreach (var (emitter, state) in ComponentDatas)
        {
            var position = emitter.Entity.Transform.WorldMatrix.TranslationVector;
            var velocity = state.HasPrevious ? (position - state.Previous) / dt : Vector3.Zero;
            state.Previous = position;
            state.HasPrevious = true;

            float surfaceHeight = 0;
            WaterSurfaceComponent surface = null;
            var submersion = water != null && emitter.Enabled && water.TryGetSurface(position, out surfaceHeight, out surface) && surface.Ripples
                ? surfaceHeight - position.Y
                : 0.0f;
            if (submersion <= 0.0f || submersion > emitter.Height)
            {
                state.InWater = false;
                continue;
            }

            var depth = MathUtil.Clamp(submersion / FullDepth, 0.0f, 1.0f) * emitter.Strength;
            var horizontal = new Vector3(velocity.X, 0, velocity.Z);
            var speed = MathF.Min(horizontal.Length(), MaxSpeed);
            var forward = speed > 1e-3f ? horizontal / horizontal.Length() : Vector3.UnitZ;
            var right = new Vector3(forward.Z, 0, -forward.X);
            var radius = emitter.Radius;

            // Entering the water: a splash by the falling (or stepping-in) speed
            if (!state.InWater)
            {
                state.InWater = true;
                state.IdleTimer = 0.5f;
                var fall = MathUtil.Clamp(-velocity.Y, 0.5f, MaxSpeed);
                water.AddRipple(position, surfaceHeight, radius * 1.2f, -SplashHeight * fall * depth);
            }

            if (speed > 0.05f)
            {
                // The wake: the body pushes the water aside ahead of it
                water.AddRipple(position + forward * (radius * 0.4f), surfaceHeight, radius, -WakeHeight * speed * depth * dt);

                // Steps, left and right
                if (emitter.StepLength > 0.0f && speed > 0.3f)
                {
                    state.StepDistance += speed * dt;
                    if (state.StepDistance >= emitter.StepLength)
                    {
                        state.StepDistance %= emitter.StepLength;
                        state.LeftFoot = !state.LeftFoot;
                        var foot = position + right * (emitter.StepWidth * 0.5f * (state.LeftFoot ? -1.0f : 1.0f)) + forward * (radius * 0.5f);
                        water.AddRipple(foot, surfaceHeight, radius * 0.45f, -StepHeight * MathF.Sqrt(speed / 4.0f) * depth);
                    }
                }
                state.IdleTimer = 0.8f;
            }
            else
            {
                // Standing: shifting weight now and then
                state.IdleTimer -= dt;
                if (state.IdleTimer <= 0.0f)
                {
                    state.IdleTimer = 1.2f + 1.8f * random.NextSingle();
                    var angle = random.NextSingle() * MathF.Tau;
                    var offset = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * (radius * 0.4f);
                    water.AddRipple(position + offset, surfaceHeight, radius * 0.5f, -IdleHeight * depth);
                }
            }
        }
    }
}

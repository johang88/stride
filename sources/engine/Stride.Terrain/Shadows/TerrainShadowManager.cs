// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Terrain.Rendering;

namespace Stride.Terrain.Shadows;

/// <summary>
/// Shadows cast by the active terrain at any distance, independently of the shadow map cascades: a ray march of the
/// height field towards the sun stores, per texel, the height above which a point is lit and the distance to its occluder
/// (for the penumbra). Directional light shadows (<see cref="TerrainDirectionalShadowMapRenderer"/>) combine it with their
/// shadow map, so it darkens the terrain and the objects on it.
/// </summary>
/// <remarks>
/// Baked on the CPU in the background (a parallel ray march over a copy of the heights) and uploaded once complete, again
/// when the sun turns, the heights change or the settings change. The previous result stays in use meanwhile.
/// </remarks>
public sealed class TerrainShadowManager : IRenderViewExtension, IDisposable
{
    /// <summary>
    /// Minimum time (seconds) between two bakes caused by height changes (sculpting).
    /// </summary>
    private const double HeightChangeBakeInterval = 0.5;

    private readonly GraphicsDevice device;
    private readonly Stopwatch sinceLastBake = Stopwatch.StartNew();
    private Texture texture;
    private Texture retiredTexture;
    private ushort[] heightsCopy;
    private Task<uint[]> bakeTask;
    private CancellationTokenSource bakeCancellation;
    private long lastDrawFrame = -1;

    // What the texture holds, and what the bake in progress computes
    private BakeState baked;
    private BakeState baking;

    private struct BakeState
    {
        public TerrainRenderObject Terrain;
        public Vector3 Direction;
        public int Resolution;
        public int Revision;
        public float MinHeight;
        public float HeightRange;
    }

    public TerrainShadowManager(GraphicsDevice device)
    {
        this.device = device;
    }

    /// <summary>
    /// Whether the terrain casts these shadows.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum size of the shadow texture (texels per side); never more than the height samples.
    /// </summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>
    /// Penumbra width per meter between a receiver and its occluder (the sun is about 0.01; wider looks softer).
    /// </summary>
    public float PenumbraScale { get; set; } = 0.02f;

    /// <summary>
    /// Maximum distance (meters) of the occluders.
    /// </summary>
    public float MaxDistance { get; set; } = 2000.0f;

    /// <summary>
    /// The terrain casting the shadows (set by the <see cref="TerrainProcessor"/>).
    /// </summary>
    public TerrainRenderObject Active { get; internal set; }

    /// <summary>
    /// Direction of the light casting the shadows, from the light towards the scene (set by the shadow receivers).
    /// </summary>
    internal Vector3? LightDirection { get; set; }

    /// <summary>
    /// Whether the shadows of the active terrain are baked.
    /// </summary>
    public bool IsReady => Enabled && texture != null && Active != null && baked.Terrain == Active;

    /// <summary>
    /// Whether a bake is in progress (its result replaces the current shadows when complete).
    /// </summary>
    public bool IsBaking => bakeTask != null;

    public void Dispose()
    {
        // A bake in progress only touches its own copy of the heights: let it stop and drop the result
        bakeCancellation?.Cancel();
        bakeTask = null;
        texture?.Dispose();
        texture = null;
        retiredTexture?.Dispose();
        retiredTexture = null;
        baked = default;
    }

    /// <summary>
    /// Registers this extension with a render system and lets its directional shadow maps receive the terrain shadows (idempotent).
    /// </summary>
    public void Register(RenderSystem renderSystem)
    {
        if (!renderSystem.ViewExtensions.Contains(this))
            renderSystem.ViewExtensions.Add(this);

        foreach (var lighting in renderSystem.RenderFeatures.OfType<MeshRenderFeature>().SelectMany(x => x.RenderFeatures).OfType<ForwardLightingRenderFeature>())
        {
            if (lighting.ShadowMapRenderer is not ShadowMapRenderer shadows)
                continue;
            for (int i = 0; i < shadows.Renderers.Count; i++)
            {
                // Exactly the stock renderer: a custom one is left alone
                if (shadows.Renderers[i] is { } renderer && renderer.GetType() == typeof(LightDirectionalShadowMapRenderer))
                    shadows.Renderers[i] = new TerrainDirectionalShadowMapRenderer { ShadowCasterRenderStage = renderer.ShadowCasterRenderStage };
            }
        }
    }

    public void Unregister(RenderSystem renderSystem)
    {
        renderSystem.ViewExtensions.Remove(this);
    }

    /// <summary>
    /// Shader parameters of the receivers: region (xy = world XZ of the terrain origin, z = 1 / size, w = ready),
    /// params (min height, height range, max occluder distance, penumbra scale) and bias (height bias, minimum penumbra).
    /// The texture is null when not ready.
    /// </summary>
    internal (Texture Texture, Vector4 Region, Vector4 Params, Vector2 Bias) GetReceiverParameters()
    {
        var data = Active?.Data;
        if (!IsReady || data == null)
            return default;
        var position = Active.World.TranslationVector;
        var size = (data.Resolution - 1) * data.CellSize;
        var texelSize = size / texture.Width;
        // A texel of slack for the interpolation between texels
        var bias = new Vector2(0.1f + texelSize, 0.3f);
        return (texture, new Vector4(position.X, position.Z, 1.0f / size, 1.0f), new Vector4(baked.MinHeight + position.Y, baked.HeightRange, MaxDistance, PenumbraScale), bias);
    }

    void IRenderViewExtension.Collect(RenderContext context)
    {
    }

    void IRenderViewExtension.Draw(RenderContext context, RenderDrawContext drawContext)
    {
        var frame = context.Time?.FrameCount ?? 0;
        if (frame == lastDrawFrame)
            return;
        lastDrawFrame = frame;

        // Replaced last frame: no longer bound
        retiredTexture?.Dispose();
        retiredTexture = null;

        if (bakeTask is { IsCompleted: true } finished)
        {
            bakeTask = null;
            bakeCancellation.Dispose();
            bakeCancellation = null;
            if (finished.Status == TaskStatus.RanToCompletion)
                Upload(drawContext.CommandList, finished.Result);
        }

        var terrain = Active;
        if (!Enabled || terrain?.Data is not { Resolution: >= 2 } data || LightDirection is not { } direction)
        {
            bakeCancellation?.Cancel();
            return;
        }

        direction = Vector3.Normalize(direction);
        var resolution = Math.Clamp(Math.Min(Resolution, data.Resolution), 64, 4096);
        if (bakeTask != null)
        {
            // Outdated before it completes (the sun turned, the settings changed): stop it, the next one starts once it has
            if (IsOutdated(baking, terrain, direction, resolution))
                bakeCancellation.Cancel();
            return;
        }

        var reshaped = baked.Revision != data.Revision && sinceLastBake.Elapsed.TotalSeconds >= HeightChangeBakeInterval;
        if (IsOutdated(baked, terrain, direction, resolution) || reshaped)
            StartBake(terrain, data, direction, resolution);
    }

    private static bool IsOutdated(in BakeState state, TerrainRenderObject terrain, Vector3 direction, int resolution)
    {
        return state.Terrain != terrain || state.Resolution != resolution
            || Vector3.Dot(direction, state.Direction) < MathF.Cos(MathUtil.DegreesToRadians(0.05f));
    }

    private void StartBake(TerrainRenderObject terrain, TerrainData data, Vector3 direction, int resolution)
    {
        // The heights can change while baking (sculpting): bake a copy
        if (heightsCopy?.Length != data.Heights.Length)
            heightsCopy = new ushort[data.Heights.Length];
        Array.Copy(data.Heights, heightsCopy, heightsCopy.Length);
        var minHeight = data.Dequantize(0);
        baking = new BakeState
        {
            Terrain = terrain,
            Direction = direction,
            Resolution = resolution,
            Revision = data.Revision,
            MinHeight = minHeight,
            HeightRange = data.Dequantize(ushort.MaxValue) - minHeight,
        };

        // Towards the sun: the opposite of the light direction. Sun at the zenith (or below the horizon): nothing to march.
        var horizontal = new Vector2(-direction.X, -direction.Z);
        var horizontalLength = horizontal.Length();
        var tanElevation = horizontalLength > 1e-4f ? -direction.Y / horizontalLength : 0;
        var maxDistance = horizontalLength > 1e-4f && tanElevation > 0 ? MaxDistance : 0;
        var sun = horizontal / Math.Max(horizontalLength, 1e-4f);
        var field = new HeightField(heightsCopy, data.Resolution, data.CellSize, baking.MinHeight, baking.HeightRange);
        bakeCancellation = new CancellationTokenSource();
        var cancellation = bakeCancellation.Token;
        bakeTask = Task.Run(() => Bake(field, resolution, sun, tanElevation, maxDistance, cancellation), cancellation);
        sinceLastBake.Restart();
    }

    private void Upload(CommandList commandList, uint[] texels)
    {
        var resolution = baking.Resolution;
        if (texture == null || texture.Width != resolution)
        {
            // The receivers may still have the old one bound this frame
            retiredTexture = texture;
            texture = Texture.New2D(device, resolution, resolution, PixelFormat.R16G16_UNorm, TextureFlags.ShaderResource);
            texture.Name = "TerrainShadows";
        }
        texture.SetData(commandList, texels);
        baked = baking;
    }

    /// <summary>
    /// Ray marches every texel towards the sun. Texel centers map linearly onto the terrain (local XZ = UV * terrain size).
    /// Each texel packs (R16) the height above which a point is lit, normalized to the height range, and (G16) the distance
    /// to that occluder, normalized to the maximum distance.
    /// </summary>
    private static uint[] Bake(HeightField field, int resolution, Vector2 sun, float tanElevation, float maxDistance, CancellationToken cancellation)
    {
        var texels = new uint[resolution * resolution];
        var size = field.Size;
        var maxHeight = field.MinHeight + field.HeightRange;
        var cell = field.CellSize;
        Parallel.For(0, resolution, new ParallelOptions { CancellationToken = cancellation }, row =>
        {
            var z = (row + 0.5f) / resolution * size;
            for (int column = 0; column < resolution; column++)
            {
                var x = (column + 0.5f) / resolution * size;
                var originHeight = field.Sample(x, z);

                // Height of the highest ray towards the sun blocked by the terrain
                var litHeight = float.NegativeInfinity;
                var occluderDistance = 0.0f;
                var t = cell;
                while (t < maxDistance)
                {
                    var px = x + sun.X * t;
                    var pz = z + sun.Y * t;
                    if (px < 0 || pz < 0 || px > size || pz > size)
                        break;
                    // The ray climbs above the highest point of the terrain: nothing further can block it
                    if (originHeight + t * tanElevation > maxHeight)
                        break;
                    var blocking = field.Sample(px, pz) - t * tanElevation;
                    if (blocking > litHeight)
                    {
                        litHeight = blocking;
                        occluderDistance = t;
                    }
                    // Longer steps further away
                    t += Math.Max(cell, t * 0.02f);
                }

                var lit = float.IsNegativeInfinity(litHeight) ? 0 : Math.Clamp((litHeight - field.MinHeight) / Math.Max(field.HeightRange, 1e-4f), 0, 1);
                var distance = maxDistance > 0 ? Math.Clamp(occluderDistance / maxDistance, 0, 1) : 0;
                texels[row * resolution + column] = (uint)(lit * ushort.MaxValue + 0.5f) | (uint)(distance * ushort.MaxValue + 0.5f) << 16;
            }
        });
        return texels;
    }

    /// <summary>
    /// Bilinear sampling of a copy of the quantized heights (local meters).
    /// </summary>
    private readonly struct HeightField(ushort[] heights, int resolution, float cellSize, float minHeight, float heightRange)
    {
        public float CellSize => cellSize;

        public float MinHeight => minHeight;

        public float HeightRange => heightRange;

        public float Size => (resolution - 1) * cellSize;

        public float Sample(float x, float z)
        {
            var fx = x / cellSize;
            var fz = z / cellSize;
            var x0 = Math.Clamp((int)fx, 0, resolution - 2);
            var z0 = Math.Clamp((int)fz, 0, resolution - 2);
            var tx = Math.Clamp(fx - x0, 0, 1);
            var tz = Math.Clamp(fz - z0, 0, 1);
            var index0 = z0 * resolution + x0;
            var index1 = index0 + resolution;
            var top = heights[index0] + (heights[index0 + 1] - heights[index0]) * tx;
            var bottom = heights[index1] + (heights[index1 + 1] - heights[index1]) * tx;
            return minHeight + (top + (bottom - top) * tz) * (heightRange / ushort.MaxValue);
        }
    }
}

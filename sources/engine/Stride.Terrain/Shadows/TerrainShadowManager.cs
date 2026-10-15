// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Terrain.Rendering;

namespace Stride.Terrain.Shadows;

/// <summary>
/// Shadows cast by the active terrain at any distance, independently of the shadow map cascades: a compute line sweep
/// along the light (TerrainShadowGenerator, from the Ogre Terra sample) stores, per texel, the height above which a point
/// is lit and the distance to its occluder (for the penumbra). Directional light shadows
/// (<see cref="TerrainDirectionalShadowMapRenderer"/>) combine it with their shadow map, so it darkens the terrain and the
/// objects on it.
/// </summary>
/// <remarks>
/// Generated again when the light turns, the heights change or the settings change.
/// </remarks>
public sealed class TerrainShadowManager : IRenderViewExtension, IDisposable
{
    /// <summary>
    /// Minimum number of frames between two generations caused by height changes (sculpting).
    /// </summary>
    private const int HeightChangeInterval = 10;

    private const int ThreadsPerGroup = 64;

    /// <summary>
    /// Entries of the line start and group arrays (cbuffers of 4096 entries; starts past them use zw).
    /// </summary>
    private const int MaxEntries = 4096;

    private readonly GraphicsDevice device;
    private readonly Int4[] starts = new Int4[MaxEntries];
    private readonly TerrainShadowGroupData[] groups = new TerrainShadowGroupData[MaxEntries];
    private ComputeEffectShader generator;
    private Texture texture;
    private long lastDrawFrame = -1;
    private long lastGenerationFrame;

    // What the texture holds
    private TerrainRenderObject bakedTerrain;
    private Vector3 bakedDirection;
    private int bakedRevision;
    private float bakedMinHeight;
    private float bakedHeightRange;

    public TerrainShadowManager(GraphicsDevice device)
    {
        this.device = device;
    }

    /// <summary>
    /// Whether the terrain casts these shadows.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Size of the shadow texture (texels per side), at most 4096 and never more than the height samples.
    /// </summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>
    /// Penumbra width per meter between a receiver and its occluder (the sun is about 0.01; wider looks softer).
    /// </summary>
    public float PenumbraScale { get; set; } = 0.02f;

    /// <summary>
    /// Occluder distance (meters) at which the penumbra stops widening.
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
    /// Whether the shadows of the active terrain are generated.
    /// </summary>
    public bool IsReady => Enabled && texture != null && Active != null && bakedTerrain == Active;

    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        generator?.Dispose();
        generator = null;
        bakedTerrain = null;
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
        return (texture, new Vector4(position.X, position.Z, 1.0f / size, 1.0f), new Vector4(bakedMinHeight + position.Y, bakedHeightRange, MaxDistance, PenumbraScale), bias);
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

        var terrain = Active;
        if (!Enabled || terrain?.Data is not { Resolution: >= 2 } data || terrain.Gpu == null || LightDirection is not { } direction)
            return;

        direction = Vector3.Normalize(direction);
        var resolution = Math.Clamp(Math.Min(Resolution, data.Resolution), 64, MaxEntries);
        var turned = Vector3.Dot(direction, bakedDirection) < MathF.Cos(MathUtil.DegreesToRadians(0.05f));
        var rebuild = bakedTerrain != terrain || texture == null || texture.Width != resolution;
        var reshaped = bakedRevision != data.Revision && frame - lastGenerationFrame >= HeightChangeInterval;
        if (!rebuild && !turned && !reshaped)
            return;

        if (texture == null || texture.Width != resolution)
        {
            texture?.Dispose();
            texture = Texture.New2D(device, resolution, resolution, PixelFormat.R16G16_UNorm, TextureFlags.ShaderResource | TextureFlags.UnorderedAccess);
            texture.Name = "TerrainShadows";
        }
        Generate(context, drawContext, terrain, data, direction, resolution);

        bakedTerrain = terrain;
        bakedDirection = direction;
        bakedRevision = data.Revision;
        lastGenerationFrame = frame;
    }

    /// <summary>
    /// Sets up the lines of the sweep (Bresenham, as in the Terra sample's TerraShadowMapper) and dispatches one thread per line.
    /// </summary>
    private void Generate(RenderContext context, RenderDrawContext drawContext, TerrainRenderObject terrain, TerrainData data, Vector3 direction, int resolution)
    {
        var size = (data.Resolution - 1) * data.CellSize;
        var texelSize = size / resolution;

        var light2d = new Vector2(direction.X, direction.Z);
        if (light2d.LengthSquared() < 1e-12f)
            light2d = Vector2.UnitX; // Light straight down: any direction, nothing is shadowed
        light2d.Normalize();

        var width = (uint)resolution;
        var height = (uint)resolution;

        // Line from (x0, y0) to (x1, y1) along the light, with its slope (Bresenham)
        var x0 = 0.0f;
        var y0 = 0.0f;
        var x1 = (float)(width - 1u);
        var y1 = (float)(height - 1u);
        uint heightOrWidth;
        uint widthOrHeight;
        float majorComponent;
        if (Math.Abs(light2d.X) > Math.Abs(light2d.Y))
        {
            y1 *= Math.Abs(light2d.Y) / Math.Abs(light2d.X);
            heightOrWidth = height;
            widthOrHeight = width;
            majorComponent = Math.Abs(direction.X);
        }
        else
        {
            x1 *= Math.Abs(light2d.X) / Math.Abs(light2d.Y);
            heightOrWidth = width;
            widthOrHeight = height;
            majorComponent = Math.Abs(direction.Z);
        }

        if (light2d.X < 0)
            (x0, x1) = (x1, x0);
        if (light2d.Y < 0)
            (y0, y1) = (y1, y0);

        var steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            (x0, y0) = (y0, x0);
            (x1, y1) = (y1, x1);
        }

        float dx;
        float dy;
        {
            var lx0 = x0;
            var ly0 = y0;
            var lx1 = x1;
            var ly1 = y1;
            if (lx0 > lx1)
            {
                (lx0, lx1) = (lx1, lx0);
                (ly0, ly1) = (ly1, ly0);
            }
            dx = lx1 - lx0 + 1.0f;
            dy = Math.Abs(ly1 - ly0);
            if (Math.Abs(light2d.X) > Math.Abs(light2d.Y))
                dy += Math.Abs(light2d.Y) / Math.Abs(light2d.X);
            else
                dy += Math.Abs(light2d.X) / Math.Abs(light2d.Y);
        }

        var xyStep = new Int2(x0 < x1 ? 1 : -1, y0 < y1 ? 1 : -1);

        // Per step along the major axis: the horizontal distance travelled and the height the light ray drops
        var horizontalLength = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        var stepLength = horizontalLength > 1e-6f ? texelSize * horizontalLength / Math.Max(majorComponent, 1e-6f) : texelSize;
        var heightRange = data.Dequantize(ushort.MaxValue) - data.Dequantize(0);
        var heightDelta = -direction.Y / Math.Max(majorComponent, 1e-6f) * texelSize;
        // Beyond the height range nothing can be shadowed (or everything, below the horizon): avoid infinities
        heightDelta = Math.Clamp(heightDelta, -2 * heightRange - 1, 2 * heightRange + 1);

        // y0 must be 0 or heightOrWidth depending on the direction of the y steps
        if (y0 >= y1)
            y0 = heightOrWidth;

        var fStep = dx * 0.5f / dy;
        var extraIterations = (uint)Math.Min(Math.Ceiling(dy), Math.Ceiling(((heightOrWidth - 1u) / fStep - 1u) * 0.5f));

        var firstThreadGroups = AlignToNextMultiple(heightOrWidth, ThreadsPerGroup) / ThreadsPerGroup;
        var lastThreadGroups = AlignToNextMultiple(extraIterations, ThreadsPerGroup) / ThreadsPerGroup;
        var totalThreadGroups = firstThreadGroups + lastThreadGroups;
        var idy = (int)Math.Floor(dy);

        // "First" series of thread groups: lines starting on the edge facing the light
        var startsIndex = 0;
        var writeXY = true;
        for (var h = 0; h < firstThreadGroups; ++h)
        {
            var startY = h * ThreadsPerGroup;
            for (var i = 0; i < ThreadsPerGroup; ++i)
                SetStart(ref startsIndex, ref writeXY, (int)x0, (int)y0 + (startY + i) * xyStep.Y);
            groups[h].Iterations = (int)widthOrHeight - Math.Max(0, idy - (int)(heightOrWidth - startY));
            groups[h].DeltaErrorStart = 0;
        }

        // "Last" series: lines starting on the other edge, covering the corner the first ones miss
        for (var h = 0; h < lastThreadGroups; ++h)
        {
            var xN = GetXStepsNeededToReachY(ThreadsPerGroup * (uint)h + 1u, fStep);
            for (var i = 0; i < ThreadsPerGroup; ++i)
                SetStart(ref startsIndex, ref writeXY, (int)x0 + xN * xyStep.X, (int)y0 - i * xyStep.Y);
            groups[firstThreadGroups + h].Iterations = (int)widthOrHeight - xN;
            groups[firstThreadGroups + h].DeltaErrorStart = GetErrorAfterXSteps((uint)xN, dx, dy) - dx * 0.5f;
        }

        // Heights read from the mip closest to the shadow texel size
        var heightMip = (int)Math.Floor(Math.Log2(Math.Max(1.0f, texelSize / data.CellSize)));
        var heightSamples = Math.Max(1, data.Resolution >> heightMip);

        generator ??= new ComputeEffectShader(context) { ShaderSourceName = "TerrainShadowGenerator" };
        var parameters = generator.Parameters;
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowOutput, texture);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightmap, terrain.Gpu.Heightmap);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowStartXY, starts.Length, ref starts[0]);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowGroup, groups.Length, ref groups[0]);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowDelta, new Vector2(dx, dy));
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowXyStep, new Vector2(xyStep.X, xyStep.Y));
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowIsSteep, steep ? 1 : 0);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightDelta, heightDelta);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowStepLength, stepLength);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowTexelSize, texelSize);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightMip, heightMip);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightSpacing, data.CellSize * (1 << heightMip));
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightSamples, heightSamples);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowMinHeight, data.Dequantize(0));
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowHeightRange, heightRange);
        parameters.Set(TerrainShadowGeneratorKeys.TerrainShadowMaxDistance, Math.Max(MaxDistance, 1.0f));
        generator.ThreadGroupCounts = new Int3((int)totalThreadGroups, 1, 1);
        generator.ThreadNumbers = new Int3(ThreadsPerGroup, 1, 1);
        generator.Draw(drawContext, "TerrainShadowGenerator");

        bakedMinHeight = data.Dequantize(0);
        bakedHeightRange = heightRange;
    }

    private void SetStart(ref int index, ref bool writeXY, int x, int y)
    {
        if (writeXY)
        {
            starts[index].X = x;
            starts[index].Y = y;
        }
        else
        {
            starts[index].Z = x;
            starts[index].W = y;
        }
        if (++index >= MaxEntries)
        {
            index -= MaxEntries;
            writeXY = false;
        }
    }

    private static int GetXStepsNeededToReachY(uint y, float fStep)
        => (int)Math.Ceiling(Math.Max(((y << 1) - 1u) * fStep, 0.0f));

    private static float GetErrorAfterXSteps(uint xIterationsToSkip, float dx, float dy)
    {
        // Round the accumulated error to the next multiple of dx, minus the accumulated error: the error at (x, y).
        // Must be done in double precision, or some light angles give artifacts.
        var accumulatedError = dx * 0.5 + dy * (double)xIterationsToSkip;
        var newErrorAtX = Math.Ceiling(accumulatedError / dx) * dx - accumulatedError;
        return (float)newErrorAtX;
    }

    private static uint AlignToNextMultiple(uint offset, uint alignment)
        => (offset + alignment - 1u) / alignment * alignment;
}

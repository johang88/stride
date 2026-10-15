// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
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
/// Baked again when the sun turns, the heights change or the settings change.
/// </remarks>
public sealed class TerrainShadowManager : IRenderViewExtension, IDisposable
{
    /// <summary>
    /// Minimum number of frames between two bakes caused by height changes (sculpting).
    /// </summary>
    private const int HeightChangeBakeInterval = 10;

    private readonly GraphicsDevice device;
    private ImageEffectShader bakeEffect;
    private Texture texture;
    private Texture dummyTexture;
    private TerrainRenderObject bakedTerrain;
    private TerrainData subscribedData;
    private Vector3 bakedDirection;
    private int bakedResolution;
    private bool heightsChanged;
    private long lastDrawFrame = -1;
    private long lastBakeFrame;
    private bool firstBake = true;

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
    /// Whether the shadows of the active terrain are baked for the current light.
    /// </summary>
    public bool IsReady => Enabled && texture != null && Active != null && bakedTerrain == Active;

    public void Dispose()
    {
        Subscribe(null);
        texture?.Dispose();
        texture = null;
        bakeEffect?.Dispose();
        bakeEffect = null;
        dummyTexture?.Dispose();
        dummyTexture = null;
        firstBake = true;
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
                    shadows.Renderers[i] = new TerrainDirectionalShadowMapRenderer(this) { ShadowCasterRenderStage = renderer.ShadowCasterRenderStage };
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
    /// </summary>
    internal (Texture Texture, Vector4 Region, Vector4 Params, Vector2 Bias) GetReceiverParameters()
    {
        var data = Active?.Data;
        if (!IsReady || data == null)
        {
            dummyTexture ??= Texture.New2D(device, 1, 1, PixelFormat.R16G16_UNorm, new uint[] { 0 });
            return (dummyTexture, Vector4.Zero, Vector4.Zero, Vector2.Zero);
        }
        var position = Active.World.TranslationVector;
        var size = (data.Resolution - 1) * data.CellSize;
        var texelSize = size / texture.Width;
        // A texel of slack for the interpolation between texels
        var bias = new Vector2(0.1f + texelSize, 0.3f);
        return (texture, new Vector4(position.X, position.Z, 1.0f / size, 1.0f), new Vector4(data.MinHeight + position.Y, data.MaxHeight - data.MinHeight, MaxDistance, PenumbraScale), bias);
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
        Subscribe(terrain?.Data);
        if (!Enabled || terrain?.Data is not { Resolution: >= 2 } data || terrain.Gpu == null || LightDirection is not { } direction)
            return;

        var resolution = Math.Clamp(Math.Min(Resolution, data.Resolution), 64, 4096);
        var turned = Vector3.Dot(Vector3.Normalize(direction), bakedDirection) < MathF.Cos(MathUtil.DegreesToRadians(0.05f));
        var rebuild = bakedTerrain != terrain || bakedResolution != resolution || texture == null;
        if (!rebuild && !turned && !(heightsChanged && frame - lastBakeFrame >= HeightChangeBakeInterval))
            return;

        Bake(drawContext, terrain, data, Vector3.Normalize(direction), resolution);
        lastBakeFrame = frame;
    }

    private void Bake(RenderDrawContext context, TerrainRenderObject terrain, TerrainData data, Vector3 direction, int resolution)
    {
        if (texture == null || texture.Width != resolution)
        {
            texture?.Dispose();
            texture = Texture.New2D(device, resolution, resolution, PixelFormat.R16G16_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
            texture.Name = "TerrainShadows";
        }
        bakeEffect ??= new ImageEffectShader("TerrainShadowBakeShader");

        // Towards the sun: the opposite of the light direction
        var horizontal = new Vector2(-direction.X, -direction.Z);
        var horizontalLength = horizontal.Length();
        // Sun at the zenith (or below the horizon): nothing to march, every texel is lit
        var tanElevation = horizontalLength > 1e-4f ? -direction.Y / horizontalLength : 1e4f;
        var sun = horizontalLength > 1e-4f && tanElevation > 0
            ? new Vector4(horizontal / horizontalLength, tanElevation, MaxDistance)
            : new Vector4(1, 0, 1e4f, 0);

        TerrainMaterialParameters.Apply(bakeEffect.Parameters, terrain);
        bakeEffect.Parameters.Set(TerrainShadowBakeShaderKeys.TerrainShadowSun, sun);
        bakeEffect.SetOutput(texture);
        using (context.PushRenderTargetsAndRestore())
            bakeEffect.Draw(context);

        bakedTerrain = terrain;
        bakedDirection = direction;
        bakedResolution = resolution;
        // The first draw compiles the effect and rebuilds its parameter layout: bake once more with the parameters in place
        heightsChanged = firstBake;
        firstBake = false;
    }

    private void Subscribe(TerrainData data)
    {
        if (subscribedData == data)
            return;
        if (subscribedData != null)
            subscribedData.Changed -= OnDataChanged;
        subscribedData = data;
        if (data != null)
            data.Changed += OnDataChanged;
    }

    private void OnDataChanged(object sender, TerrainDataChangedEventArgs e)
    {
        if ((e.Flags & TerrainChangeFlags.Heights) != 0)
            heightsChanged = true;
    }
}

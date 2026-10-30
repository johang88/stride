// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Decals;

/// <summary>
/// Channels of the decal buffer written by the decals of a view (the surfaces skip the others).
/// </summary>
[Flags]
internal enum DecalChannels
{
    None = 0,
    Albedo = 1,
    Normal = 2,
    Surface = 4,
    Emissive = 8,
}

/// <summary>
/// The decals of a view this frame and the buffer they are drawn into.
/// </summary>
internal sealed class DecalViewState : IDisposable
{
    // Most frames the buffer is kept without decals before it is freed
    public const int IdleTimeout = 300;

    public int Frame = -1;
    public int LastUsedFrame;
    public DecalChannels Channels;

    // The decal buffer: premultiplied albedo + remaining (alpha); normal offset + remaining; roughness, metalness,
    // occlusion + remaining; emitted light (added)
    public Texture Albedo;
    public Texture Normal;
    public Texture Surface;
    public Texture Emissive;

    public Buffer Instances;
    public Vector4[] InstanceData = new Vector4[DecalRenderer.InstanceStride * 64];
    public int Count;
    public readonly List<DecalBatch> Batches = new();

    public void EnsureTargets(GraphicsDevice device, int width, int height)
    {
        if (Albedo != null && Albedo.Width == width && Albedo.Height == height)
            return;
        DisposeTargets();
        Albedo = New(PixelFormat.R8G8B8A8_UNorm_SRgb, "DecalAlbedo");
        Normal = New(PixelFormat.R8G8B8A8_UNorm, "DecalNormal");
        Surface = New(PixelFormat.R8G8B8A8_UNorm, "DecalSurface");
        Emissive = New(PixelFormat.R11G11B10_Float, "DecalEmissive");

        Texture New(PixelFormat format, string name)
        {
            var texture = Texture.New2D(device, width, height, format, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            texture.Name = name;
            return texture;
        }
    }

    public void DisposeTargets()
    {
        Albedo?.Dispose();
        Normal?.Dispose();
        Surface?.Dispose();
        Emissive?.Dispose();
        Albedo = Normal = Surface = Emissive = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        Instances?.Dispose();
        Instances = null;
    }
}

/// <summary>
/// Consecutive decals of a view with the same textures, drawn with one instanced draw.
/// </summary>
internal readonly record struct DecalBatch(int First, int Count, Texture Albedo, Texture Normal, Texture Surface, Texture Emissive);

/// <summary>
/// The decals of the views of a renderer, shared with the <see cref="DecalRenderFeature"/> of the opaque surfaces.
/// </summary>
internal sealed class DecalViews
{
    public readonly Dictionary<RenderView, DecalViewState> Views = new();

    /// <summary>
    /// The render stage of the surfaces that apply the decals.
    /// </summary>
    public RenderStage OpaqueStage;

    /// <summary>
    /// The frame the renderer last collected a view (the surfaces mix in the decals while it does).
    /// </summary>
    public int CollectedFrame = -1;

    public bool SkinnedMeshesReceive;
}

/// <summary>
/// Projects the <see cref="DecalComponent"/>s of the scene onto the opaque surfaces: after the depth prepass, the decal boxes
/// of each view are drawn into a screen-space decal buffer (albedo, normal, roughness / metalness / occlusion, emitted light),
/// which the opaque surfaces apply before their lighting (DecalShading, see <see cref="ForwardShadingKeys.Decals"/>), so decals
/// are lit like the surfaces. Set it as the decals of the forward renderer (it needs its depth prepass).
/// </summary>
/// <remarks>
/// Surfaces opt out with Receive decals on their model or material; skinned models don't receive decals unless
/// <see cref="SkinnedMeshesReceive"/> (a decal fixed in the world would slide over a moving character). Where a surface that
/// doesn't receive decals hides one that does, the decal is hidden too. Transparent surfaces don't receive decals.
/// </remarks>
[DataContract("DecalRenderer")]
[Display("Decals")]
public sealed class DecalRenderer : RendererCoreBase, IDecalRenderer
{
    /// <summary>
    /// Property key of the scene's decals in <see cref="VisibilityGroup.Tags"/>.
    /// </summary>
    [DataMemberIgnore]
    public static readonly PropertyKey<List<RenderDecal>> CurrentDecals = new("Decals.CurrentDecals", typeof(DecalRenderer));

    public static readonly ProfilingKey DrawKey = new("Decals.Draw");

    /// <summary>
    /// Float4 elements per decal in the instance buffer (see DecalProjection.sdsl).
    /// </summary>
    internal const int InstanceStride = 11;

    // Frames a view is kept after it was last drawn
    private const int ViewTimeout = 120;

    private readonly DecalViews decalViews = new();
    private readonly List<RenderView> staleViews = new();
    private readonly List<(RenderDecal Decal, float Fade, float Distance)> visible = new();
    private DecalRenderFeature renderFeature;
    private DynamicEffectInstance effect;
    private MutablePipelineState pipelineState;
    private EffectBytecode previousBytecode;
    private Texture flatNormal;

    public DecalRenderer()
    {
        Enabled = true;
    }

    /// <summary>
    /// Whether skinned models (characters) receive decals.
    /// </summary>
    [DataMember(10)]
    [DefaultValue(false)]
    public bool SkinnedMeshesReceive { get; set; }

    /// <summary>
    /// Most decals drawn in a view (the nearest ones).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(1024)]
    [DataMemberRange(1, 65536, 16, 256, 0)]
    public int MaxDecalsPerView { get; set; } = 1024;

    protected override void InitializeCore()
    {
        base.InitializeCore();

        var meshRenderFeature = Context.RenderSystem.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
        if (meshRenderFeature != null)
        {
            renderFeature = meshRenderFeature.RenderFeatures.OfType<DecalRenderFeature>().FirstOrDefault();
            if (renderFeature == null)
            {
                renderFeature = new DecalRenderFeature();
                meshRenderFeature.RenderFeatures.Add(renderFeature);
            }
            renderFeature.Decals = decalViews;
        }

        effect = new DynamicEffectInstance("DecalProjection");
        effect.Initialize(Context.Services);

        // Premultiplied over the remaining share of the surface (alpha) for the buffer, added for the emitted light
        var blend = new BlendStateDescription { IndependentBlendEnable = true };
        for (int i = 0; i < 3; i++)
        {
            ref var target = ref blend.RenderTargets[i];
            target.BlendEnable = true;
            target.ColorSourceBlend = Blend.One;
            target.ColorDestinationBlend = Blend.InverseSourceAlpha;
            target.ColorBlendFunction = BlendFunction.Add;
            target.AlphaSourceBlend = Blend.Zero;
            target.AlphaDestinationBlend = Blend.InverseSourceAlpha;
            target.AlphaBlendFunction = BlendFunction.Add;
            target.ColorWriteChannels = ColorWriteChannels.All;
        }
        ref var emissive = ref blend.RenderTargets[3];
        emissive.BlendEnable = true;
        emissive.ColorSourceBlend = Blend.One;
        emissive.ColorDestinationBlend = Blend.One;
        emissive.ColorBlendFunction = BlendFunction.Add;
        emissive.AlphaSourceBlend = Blend.One;
        emissive.AlphaDestinationBlend = Blend.One;
        emissive.AlphaBlendFunction = BlendFunction.Add;
        emissive.ColorWriteChannels = ColorWriteChannels.All;

        pipelineState = new MutablePipelineState(GraphicsDevice);
        pipelineState.State.SetDefaults();
        pipelineState.State.InputElements = [];
        pipelineState.State.PrimitiveType = PrimitiveType.TriangleList;
        pipelineState.State.BlendState = blend;
        pipelineState.State.DepthStencilState = DepthStencilStates.None;
        // (the faces towards the camera are dropped in the vertex shader, whatever the winding: each pixel is shaded once,
        // also from inside the box)
        pipelineState.State.RasterizerState = RasterizerStates.CullNone;

        flatNormal = Texture.New2D(GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(128, 128, 255, 255) });
        flatNormal.Name = "DecalFlatNormal";
    }

    protected override void Destroy()
    {
        foreach (var state in decalViews.Views.Values)
            state.Dispose();
        decalViews.Views.Clear();
        if (renderFeature?.Decals == decalViews)
            renderFeature.Decals = null;
        effect?.Dispose();
        flatNormal?.Dispose();
        base.Destroy();
    }

    /// <inheritdoc/>
    public void Collect(RenderContext context, RenderStage opaqueStage)
    {
        if (!Initialized)
            Initialize(context);
        if (!Enabled || opaqueStage == null)
            return;

        var frame = context.RenderSystem.FrameCounter;
        var view = context.RenderView;
        decalViews.OpaqueStage = opaqueStage;
        decalViews.CollectedFrame = frame;
        decalViews.SkinnedMeshesReceive = SkinnedMeshesReceive;

        // Forget the views that aren't drawn anymore
        staleViews.Clear();
        foreach (var (otherView, otherState) in decalViews.Views)
        {
            if (frame - otherState.Frame > ViewTimeout)
                staleViews.Add(otherView);
        }
        foreach (var staleView in staleViews)
        {
            decalViews.Views[staleView].Dispose();
            decalViews.Views.Remove(staleView);
        }

        if (!decalViews.Views.TryGetValue(view, out var state))
            decalViews.Views.Add(view, state = new DecalViewState());
        state.Frame = frame;
        state.Count = 0;
        state.Channels = DecalChannels.None;
        state.Batches.Clear();

        GatherVisible(context, view);
        if (visible.Count == 0)
        {
            // (the buffer is kept for a while: decals come and go)
            if (state.Albedo != null && frame - state.LastUsedFrame > DecalViewState.IdleTimeout)
                state.DisposeTargets();
            return;
        }

        FillInstances(state);
        state.LastUsedFrame = frame;
        // (the surfaces' parameters are prepared before the decals are drawn: the buffer is created now)
        state.EnsureTargets(GraphicsDevice, Math.Max(1, (int)view.ViewSize.X), Math.Max(1, (int)view.ViewSize.Y));
    }

    // The decals in the view, nearest first beyond the maximum, then in drawing order
    private void GatherVisible(RenderContext context, RenderView view)
    {
        visible.Clear();
        var decals = context.VisibilityGroup.Tags.Get(CurrentDecals);
        if (decals == null || decals.Count == 0)
            return;

        Matrix.Invert(ref view.View, out var viewInverse);
        var eye = viewInverse.TranslationVector;
        // (collected for camera views, which have a frustum)
        var frustum = view.Frustum;
        foreach (var decal in decals)
        {
            if (!frustum.Contains(ref decal.Bounds))
                continue;
            var component = decal.Component;
            var distance = Vector3.Distance(eye, decal.Bounds.Center);
            var fade = 1.0f;
            if (component.DrawDistance > 0)
            {
                fade = Math.Clamp((component.DrawDistance - distance) / (component.DrawDistance * 0.1f), 0.0f, 1.0f);
                if (fade <= 0)
                    continue;
            }
            visible.Add((decal, fade, distance));
        }

        if (visible.Count > MaxDecalsPerView)
        {
            visible.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            visible.RemoveRange(MaxDecalsPerView, visible.Count - MaxDecalsPerView);
        }
        visible.Sort(static (a, b) =>
        {
            var order = a.Decal.Component.Order.CompareTo(b.Decal.Component.Order);
            return order != 0 ? order : a.Decal.Sequence.CompareTo(b.Decal.Sequence);
        });
    }

    private void FillInstances(DecalViewState state)
    {
        var count = visible.Count;
        if (state.InstanceData.Length < count * InstanceStride)
            state.InstanceData = new Vector4[Math.Max(count, state.InstanceData.Length / InstanceStride * 2) * InstanceStride];
        var data = state.InstanceData;
        var linear = GraphicsDevice.ColorSpace == ColorSpace.Linear;

        var batchStart = 0;
        for (int i = 0; i < count; i++)
        {
            var (decal, fade, _) = visible[i];
            var component = decal.Component;
            var surface = component.Surface;
            var o = i * InstanceStride;

            // Columns of the matrices (row vectors: x' = dot(float4(p, 1), column 0)...)
            ref var toBox = ref decal.WorldToBox;
            data[o + 0] = new Vector4(toBox.M11, toBox.M21, toBox.M31, toBox.M41);
            data[o + 1] = new Vector4(toBox.M12, toBox.M22, toBox.M32, toBox.M42);
            data[o + 2] = new Vector4(toBox.M13, toBox.M23, toBox.M33, toBox.M43);
            ref var toWorld = ref decal.BoxToWorld;
            data[o + 3] = new Vector4(toWorld.M11, toWorld.M21, toWorld.M31, toWorld.M41);
            data[o + 4] = new Vector4(toWorld.M12, toWorld.M22, toWorld.M32, toWorld.M42);
            data[o + 5] = new Vector4(toWorld.M13, toWorld.M23, toWorld.M33, toWorld.M43);

            var tint = ToColorSpace(component.Color, linear);
            var color = ToColorSpace(surface.Color, linear) * tint;
            data[o + 6] = new Vector4(color.R, color.G, color.B, Math.Clamp(color.A * component.Opacity * fade, 0.0f, 1.0f));
            var emissiveColor = linear ? surface.EmissiveColor.ToLinear() : surface.EmissiveColor;
            var emissive = emissiveColor * Math.Max(surface.EmissiveIntensity, 0.0f) * new Color3(tint.R, tint.G, tint.B);
            data[o + 7] = new Vector4(emissive.R, emissive.G, emissive.B, surface.NormalStrength);

            var channels = DecalChannels.None;
            if (surface.AffectAlbedo)
                channels |= DecalChannels.Albedo;
            if (surface.AffectNormal)
                channels |= DecalChannels.Normal;
            if (surface.AffectSurface)
                channels |= DecalChannels.Surface;
            if (emissive.R > 0 || emissive.G > 0 || emissive.B > 0)
                channels |= DecalChannels.Emissive;
            state.Channels |= channels;
            data[o + 8] = new Vector4(surface.Roughness, surface.Metalness, surface.Occlusion, (float)channels);
            data[o + 9] = new Vector4(component.TextureScale, component.TextureOffset.X, component.TextureOffset.Y);

            var fadeStart = MathUtil.DegreesToRadians(Math.Clamp(component.AngleFadeStart, 0.0f, 90.0f));
            var fadeEnd = MathUtil.DegreesToRadians(Math.Clamp(Math.Max(component.AngleFadeEnd, component.AngleFadeStart), 0.0f, 90.0f));
            data[o + 10] = new Vector4(Math.Clamp(component.DepthFade, 0.0f, 1.0f), MathF.Cos(fadeStart), MathF.Cos(fadeEnd), 0);

            // A new batch when the textures change
            if (i > 0 && !SameTextures(surface, visible[i - 1].Decal.Component.Surface))
            {
                AddBatch(state, batchStart, i);
                batchStart = i;
            }
        }
        AddBatch(state, batchStart, count);
        state.Count = count;
    }

    private void AddBatch(DecalViewState state, int start, int end)
    {
        var surface = visible[start].Decal.Component.Surface;
        state.Batches.Add(new DecalBatch(start, end - start, surface.Albedo, surface.Normal, surface.SurfaceMap, surface.Emissive));
    }

    private static bool SameTextures(DecalSurface a, DecalSurface b)
    {
        return a.Albedo == b.Albedo && a.Normal == b.Normal && a.SurfaceMap == b.SurfaceMap && a.Emissive == b.Emissive;
    }

    private static Color4 ToColorSpace(Color4 color, bool linear)
    {
        return linear ? new Color4(MathUtil.SRgbToLinear(color.R), MathUtil.SRgbToLinear(color.G), MathUtil.SRgbToLinear(color.B), color.A) : color;
    }

    /// <inheritdoc/>
    public void Draw(RenderDrawContext context, Texture depthStencil)
    {
        var renderContext = context.RenderContext;
        var view = renderContext.RenderView;
        if (!Enabled || view == null || !decalViews.Views.TryGetValue(view, out var state) || state.Frame != renderContext.RenderSystem.FrameCounter
            || state.Count == 0 || state.Albedo == null)
            return;

        var commandList = context.CommandList;
        using var profile = context.QueryManager.BeginProfile(Color.Green, DrawKey);

        // The channels in use start empty (the surfaces read nothing from the others)
        if ((state.Channels & DecalChannels.Albedo) != 0)
            commandList.Clear(state.Albedo, new Color4(0, 0, 0, 1));
        if ((state.Channels & DecalChannels.Normal) != 0)
            commandList.Clear(state.Normal, new Color4(0.5f, 0.5f, 0.5f, 1));
        if ((state.Channels & DecalChannels.Surface) != 0)
            commandList.Clear(state.Surface, new Color4(0, 0, 0, 1));
        if ((state.Channels & DecalChannels.Emissive) != 0)
            commandList.Clear(state.Emissive, new Color4(0, 0, 0, 0));

        // (a depth of another size than the view's: nothing to project onto this frame)
        if (depthStencil == null || depthStencil.ViewWidth != state.Albedo.Width || depthStencil.ViewHeight != state.Albedo.Height)
            return;

        // The decals of the view
        var elementCount = state.Count * InstanceStride;
        if (state.Instances == null || state.Instances.ElementCount < elementCount)
        {
            state.Instances?.Dispose();
            state.Instances = Buffer.New<Vector4>(GraphicsDevice, state.InstanceData.Length, BufferFlags.ShaderResource | BufferFlags.StructuredBuffer, GraphicsResourceUsage.Dynamic);
            state.Instances.Name = "DecalInstances";
        }
        state.Instances.SetData(commandList, (ReadOnlySpan<Vector4>)state.InstanceData.AsSpan(0, elementCount));

        Matrix.Invert(ref view.View, out var viewInverse);
        Matrix.Invert(ref view.ViewProjection, out var inverseViewProjection);
        var width = state.Albedo.Width;
        var height = state.Albedo.Height;
        var parameters = effect.Parameters;
        parameters.Set(DecalProjectionKeys.DecalViewProjection, view.ViewProjection);
        parameters.Set(DecalProjectionKeys.DecalInverseViewProjection, inverseViewProjection);
        parameters.Set(DecalProjectionKeys.DecalEye, viewInverse.TranslationVector);
        parameters.Set(DecalProjectionKeys.DecalViewSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
        parameters.Set(DecalProjectionKeys.DecalDepth, depthStencil);
        parameters.Set(DecalProjectionKeys.DecalInstances, state.Instances);

        using (context.PushRenderTargetsAndRestore())
        {
            commandList.SetRenderTargets(null, state.Albedo, state.Normal, state.Surface, state.Emissive);
            commandList.SetViewport(new Viewport(0, 0, width, height));
            var white = GraphicsDevice.GetSharedWhiteTexture();
            foreach (var batch in state.Batches)
            {
                parameters.Set(DecalProjectionKeys.DecalAlbedoMap, batch.Albedo ?? white);
                parameters.Set(DecalProjectionKeys.DecalNormalMap, batch.Normal ?? flatNormal);
                parameters.Set(DecalProjectionKeys.DecalSurfaceMap, batch.Surface ?? white);
                parameters.Set(DecalProjectionKeys.DecalEmissiveMap, batch.Emissive ?? white);
                parameters.Set(DecalProjectionKeys.DecalFirstInstance, (uint)batch.First);
                if (!Apply(context))
                    break;
                commandList.DrawInstanced(36, batch.Count);
            }
        }
    }

    private bool Apply(RenderDrawContext context)
    {
        if (effect.UpdateEffect(GraphicsDevice) || previousBytecode != effect.Effect?.Bytecode)
        {
            if (effect.Effect == null)
                return false;
            previousBytecode = effect.Effect.Bytecode;
            pipelineState.State.RootSignature = effect.RootSignature;
            pipelineState.State.EffectBytecode = effect.Effect.Bytecode;
        }
        pipelineState.State.Output.CaptureState(context.CommandList);
        pipelineState.Update();
        context.CommandList.SetPipelineState(pipelineState.CurrentState);
        effect.Apply(context.GraphicsContext);
        return true;
    }
}

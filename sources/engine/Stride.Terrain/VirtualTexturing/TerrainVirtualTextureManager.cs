// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Stride.Terrain.VirtualTexturing;

/// <summary>
/// Drives the active terrain virtual texture of a game: renders the feedback pass of camera views, reads it back
/// asynchronously and renders the requested pages before the view is drawn.
/// </summary>
public sealed class TerrainVirtualTextureManager : IRenderViewExtension, IDisposable
{
    public const string FeedbackEffectName = "TerrainVTFeedbackEffect";

    private const int ReadbackLatency = 3;

    private readonly GraphicsDevice device;
    private readonly Readback[] readbacks = new Readback[ReadbackLatency];
    private Texture feedbackTarget;
    private Texture feedbackDepth;
    private int writeIndex;
    private long lastDrawFrame = -1;
    private uint[] feedbackData = Array.Empty<uint>();

    private struct Readback
    {
        public Texture Staging;
        public bool Pending;
    }

    public TerrainVirtualTextureManager(GraphicsDevice device)
    {
        this.device = device;
        FeedbackStage = new RenderStage("TerrainVTFeedback", "TerrainVTFeedback");
    }

    /// <summary>
    /// The render stage of the feedback pass (objects sampling the virtual texture are drawn in it).
    /// </summary>
    public RenderStage FeedbackStage { get; }

    /// <summary>
    /// The virtual texture currently sampled by the terrain and the objects blending with it.
    /// </summary>
    public TerrainVirtualTexture Active { get; set; }

    /// <summary>
    /// Whether the active virtual texture can be sampled.
    /// </summary>
    public bool IsActive => Active is { IsReady: true };

    public void Dispose()
    {
        ReleaseFeedbackResources();
    }

    /// <summary>
    /// Registers the feedback stage, its stage selector and this extension with a render system (idempotent).
    /// </summary>
    public void Register(RenderSystem renderSystem)
    {
        if (!renderSystem.RenderStages.Contains(FeedbackStage))
            renderSystem.RenderStages.Add(FeedbackStage);
        if (!renderSystem.ViewExtensions.Contains(this))
            renderSystem.ViewExtensions.Add(this);

        foreach (var meshRenderFeature in renderSystem.RenderFeatures.OfType<MeshRenderFeature>())
        {
            if (!meshRenderFeature.RenderStageSelectors.OfType<TerrainVirtualTextureStageSelector>().Any(x => x.Manager == this))
                meshRenderFeature.RenderStageSelectors.Add(new TerrainVirtualTextureStageSelector { Manager = this, EffectName = FeedbackEffectName });
            if (!meshRenderFeature.RenderFeatures.OfType<TerrainVirtualTextureRenderFeature>().Any())
                meshRenderFeature.RenderFeatures.Add(new TerrainVirtualTextureRenderFeature { Manager = this });
        }
    }

    public void Unregister(RenderSystem renderSystem)
    {
        renderSystem.ViewExtensions.Remove(this);
    }

    void IRenderViewExtension.Collect(RenderContext context)
    {
        if (Active == null)
            return;
        FeedbackStage.Output = new RenderOutputDescription(PixelFormat.R8G8B8A8_UNorm, PixelFormat.D32_Float);
        context.RenderView.RenderStages.Add(FeedbackStage);
    }

    void IRenderViewExtension.Draw(RenderContext context, RenderDrawContext drawContext)
    {
        var vt = Active;
        if (vt == null)
            return;

        // Several views can be drawn per frame (VR, previews): update once, from the first view
        var frame = context.Time?.FrameCount ?? 0;
        if (frame == lastDrawFrame)
            return;
        lastDrawFrame = frame;

        var commandList = drawContext.CommandList;
        ReadFeedback(commandList, vt);
        vt.Update(drawContext);
        RenderFeedback(context, drawContext, vt);
    }

    private void ReadFeedback(CommandList commandList, TerrainVirtualTexture vt)
    {
        // The oldest copy (made ReadbackLatency - 1 frames ago) is the slot after the last written one
        ref var readback = ref readbacks[(writeIndex + 1) % ReadbackLatency];
        if (!readback.Pending || readback.Staging == null)
            return;

        var staging = readback.Staging;
        var mapped = commandList.MapSubResource(staging, 0, MapMode.Read, doNotWait: true);
        if (mapped.DataBox.IsEmpty)
            return;

        try
        {
            var width = staging.Width;
            var height = staging.Height;
            if (feedbackData.Length != width * height)
                feedbackData = new uint[width * height];
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<uint>((byte*)mapped.DataBox.DataPointer + y * mapped.DataBox.RowPitch, width);
                    row.CopyTo(feedbackData.AsSpan(y * width, width));
                }
            }
        }
        finally
        {
            commandList.UnmapSubResource(mapped);
        }
        readback.Pending = false;
        vt.ProcessFeedback(feedbackData);
    }

    private void RenderFeedback(RenderContext context, RenderDrawContext drawContext, TerrainVirtualTexture vt)
    {
        if (!vt.IsReady || context.RenderView == null)
            return;

        var commandList = drawContext.CommandList;
        var viewport = commandList.Viewport;
        var width = Math.Max(1, (int)viewport.Width / vt.FeedbackDownscale);
        var height = Math.Max(1, (int)viewport.Height / vt.FeedbackDownscale);
        EnsureFeedbackResources(width, height);

        using (drawContext.PushRenderTargetsAndRestore())
        {
            commandList.ResourceBarrierTransition(feedbackTarget, BarrierLayout.RenderTarget);
            commandList.ResourceBarrierTransition(feedbackDepth, BarrierLayout.DepthStencilWrite);
            commandList.SetRenderTargetAndViewport(feedbackDepth, feedbackTarget);
            commandList.Clear(feedbackTarget, new Color4(0, 0, 0, 0));
            // Reversed depth: far is 0
            commandList.Clear(feedbackDepth, DepthStencilClearOptions.DepthBuffer, 0.0f);
            context.RenderSystem.Draw(drawContext, context.RenderView, FeedbackStage);
        }

        writeIndex = (writeIndex + 1) % ReadbackLatency;
        ref var readback = ref readbacks[writeIndex];
        commandList.CopyRegion(feedbackTarget, 0, null, readback.Staging, 0);
        readback.Pending = true;
    }

    private void EnsureFeedbackResources(int width, int height)
    {
        if (feedbackTarget != null && feedbackTarget.Width == width && feedbackTarget.Height == height)
            return;

        ReleaseFeedbackResources();
        feedbackTarget = Texture.New2D(device, width, height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        feedbackTarget.Name = "TerrainVTFeedback";
        feedbackDepth = Texture.New2D(device, width, height, PixelFormat.D32_Float, TextureFlags.DepthStencil);
        for (int i = 0; i < readbacks.Length; i++)
            readbacks[i] = new Readback { Staging = feedbackTarget.ToStaging() };
    }

    private void ReleaseFeedbackResources()
    {
        feedbackTarget?.Dispose();
        feedbackTarget = null;
        feedbackDepth?.Dispose();
        feedbackDepth = null;
        for (int i = 0; i < readbacks.Length; i++)
        {
            readbacks[i].Staging?.Dispose();
            readbacks[i] = default;
        }
    }
}

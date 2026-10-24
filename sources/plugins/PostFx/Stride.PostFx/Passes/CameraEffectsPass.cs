// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Rendering.Images;

namespace Stride.PostFx.Passes;

/// <summary>
/// The camera effects' targets of a view, kept between frames (their size follows the view's).
/// </summary>
internal sealed class CameraEffectsTargets : IDisposable
{
    public const int MotionTileSize = 16;
    public const int DofTileSize = 8;

    public int Width;
    public int Height;
    public Texture Motion;
    public Texture MotionTiles;
    public Texture MotionNeighbors;
    public Texture DofHalf;
    public Texture DofTiles;
    public Texture DofTilesDilated;
    public Texture DofFar;
    public Texture DofNear;

    public void Ensure(GraphicsDevice device, int width, int height)
    {
        if (Motion != null && Width == width && Height == height)
            return;
        Dispose();
        Width = width;
        Height = height;
        const TextureFlags flags = TextureFlags.ShaderResource | TextureFlags.UnorderedAccess;
        Motion = Texture.New2D(device, width, height, PixelFormat.R16G16_Float, flags);
        var motionTilesX = (width + MotionTileSize - 1) / MotionTileSize;
        var motionTilesY = (height + MotionTileSize - 1) / MotionTileSize;
        MotionTiles = Texture.New2D(device, motionTilesX, motionTilesY, PixelFormat.R16G16_Float, flags);
        MotionNeighbors = Texture.New2D(device, motionTilesX, motionTilesY, PixelFormat.R16G16_Float, flags);

        var halfWidth = Math.Max(1, width / 2);
        var halfHeight = Math.Max(1, height / 2);
        DofHalf = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, flags);
        var dofTilesX = (halfWidth + DofTileSize - 1) / DofTileSize;
        var dofTilesY = (halfHeight + DofTileSize - 1) / DofTileSize;
        DofTiles = Texture.New2D(device, dofTilesX, dofTilesY, PixelFormat.R16G16_Float, flags);
        DofTilesDilated = Texture.New2D(device, dofTilesX, dofTilesY, PixelFormat.R16G16_Float, flags);
        DofFar = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, flags);
        DofNear = Texture.New2D(device, halfWidth, halfHeight, PixelFormat.R16G16B16A16_Float, flags);
    }

    public void Dispose()
    {
        Motion?.Dispose();
        MotionTiles?.Dispose();
        MotionNeighbors?.Dispose();
        DofHalf?.Dispose();
        DofTiles?.Dispose();
        DofTilesDilated?.Dispose();
        DofFar?.Dispose();
        DofNear?.Dispose();
        Motion = MotionTiles = MotionNeighbors = DofHalf = DofTiles = DofTilesDilated = DofFar = DofNear = null;
    }
}

/// <summary>
/// Depth of field (half resolution gather, near and far fields) and motion blur (tiled reconstruction).
/// </summary>
internal sealed class CameraEffectsPass
{
    private readonly ComputeEffectShader motionTiles;
    private readonly ComputeEffectShader motionNeighbors;
    private readonly ImageEffectShader motionBlur;
    private readonly ComputeEffectShader dofPrepare;
    private readonly ComputeEffectShader dofTiles;
    private readonly ComputeEffectShader dofGather;
    private readonly ImageEffectShader dofComposite;

    public CameraEffectsPass(RenderContext context, ImageEffectShader motionBlur, ImageEffectShader dofComposite)
    {
        motionTiles = new ComputeEffectShader(context) { ShaderSourceName = "PostFxMotionTiles", ThreadNumbers = new Int3(16, 16, 1) };
        motionNeighbors = new ComputeEffectShader(context) { ShaderSourceName = "PostFxMotionNeighbors", ThreadNumbers = new Int3(8, 8, 1) };
        dofPrepare = new ComputeEffectShader(context) { ShaderSourceName = "PostFxDepthOfFieldPrepare", ThreadNumbers = new Int3(8, 8, 1) };
        dofTiles = new ComputeEffectShader(context) { ShaderSourceName = "PostFxDepthOfFieldTiles", ThreadNumbers = new Int3(8, 8, 1) };
        dofGather = new ComputeEffectShader(context) { ShaderSourceName = "PostFxDepthOfFieldGather", ThreadNumbers = new Int3(8, 8, 1) };
        this.motionBlur = motionBlur;
        this.dofComposite = dofComposite;
    }

    private static Int3 Groups(int width, int height, int size) => new((width + size - 1) / size, (height + size - 1) / size, 1);

    /// <summary>
    /// The lens's circle of confusion: focus distance, radius at infinity and largest radius (half resolution pixels),
    /// near field scale.
    /// </summary>
    private static Vector4 Lens(RenderView view, PostFxDepthOfFieldSettings settings, int height)
    {
        const float SensorHeight = 0.024f;
        var focalLength = settings.FocalLength * 0.001f;
        var focus = Math.Max(settings.FocusDistance, focalLength * 1.01f);
        var aperture = focalLength / Math.Max(settings.FStop, 0.1f);
        // Diameter on the sensor at infinity, A f / (s - f), as a radius in half resolution pixels
        var infinity = aperture * focalLength / (focus - focalLength) / SensorHeight * height * 0.25f * settings.Amount;
        var largest = Math.Max(settings.MaxBlur * height * 0.5f, 0.5f);
        return new Vector4(focus, infinity, largest, settings.NearBlur);
    }

    /// <summary>
    /// Whether the depth of field blurs anything noticeably.
    /// </summary>
    public static bool HasDepthOfField(RenderView view, PostFxDepthOfFieldSettings settings, int height)
        => settings.Amount > 0.0f && Lens(view, settings, height).Y > 0.25f;

    public void DrawDepthOfField(RenderDrawContext context, RenderView view, CameraEffectsTargets targets, PostFxDepthOfFieldSettings settings, Texture scene, Texture depth, Texture output, int frame)
    {
        var lens = Lens(view, settings, targets.Height);
        var depthInfo = new Vector2(view.Projection.M33, view.Projection.M43);
        var halfSize = new Int2(targets.DofHalf.Width, targets.DofHalf.Height);
        var tileSize = new Int2(targets.DofTiles.Width, targets.DofTiles.Height);

        var prepare = dofPrepare.Parameters;
        prepare.Set(PostFxDepthOfFieldCommonKeys.DofDepthInfo, depthInfo);
        prepare.Set(PostFxDepthOfFieldCommonKeys.DofLens, lens);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofScene, scene);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofDepth, depth);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofHalfOutput, targets.DofHalf);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofTileOutput, targets.DofTiles);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofHalfSize, halfSize);
        prepare.Set(PostFxDepthOfFieldPrepareKeys.DofFullSize, new Int2(targets.Width, targets.Height));
        dofPrepare.ThreadGroupCounts = Groups(halfSize.X, halfSize.Y, CameraEffectsTargets.DofTileSize);
        dofPrepare.Draw(context, "PostFx.DofPrepare");

        var tiles = dofTiles.Parameters;
        tiles.Set(PostFxDepthOfFieldTilesKeys.DofTileSource, targets.DofTiles);
        tiles.Set(PostFxDepthOfFieldTilesKeys.DofTileDilated, targets.DofTilesDilated);
        tiles.Set(PostFxDepthOfFieldTilesKeys.DofTileSize, tileSize);
        tiles.Set(PostFxDepthOfFieldTilesKeys.DofTileReach, Math.Clamp((int)MathF.Ceiling(lens.Z / CameraEffectsTargets.DofTileSize), 1, 4));
        dofTiles.ThreadGroupCounts = Groups(tileSize.X, tileSize.Y, 8);
        dofTiles.Draw(context, "PostFx.DofTiles");

        var gather = dofGather.Parameters;
        gather.Set(PostFxDepthOfFieldGatherKeys.DofHalf, targets.DofHalf);
        gather.Set(PostFxDepthOfFieldGatherKeys.DofTiles, targets.DofTilesDilated);
        gather.Set(PostFxDepthOfFieldGatherKeys.DofFarOutput, targets.DofFar);
        gather.Set(PostFxDepthOfFieldGatherKeys.DofNearOutput, targets.DofNear);
        gather.Set(PostFxDepthOfFieldGatherKeys.DofGatherSize, halfSize);
        gather.Set(PostFxDepthOfFieldGatherKeys.DofGatherFrame, (float)(frame % 1024));
        dofGather.ThreadGroupCounts = Groups(halfSize.X, halfSize.Y, 8);
        dofGather.Draw(context, "PostFx.DofGather");

        dofComposite.Parameters.Set(PostFxDepthOfFieldCommonKeys.DofDepthInfo, depthInfo);
        dofComposite.Parameters.Set(PostFxDepthOfFieldCommonKeys.DofLens, lens);
        dofComposite.Parameters.Set(PostFxDepthOfFieldCompositeKeys.DofHalfTexel, new Vector2(1.0f / halfSize.X, 1.0f / halfSize.Y));
        dofComposite.SetInput(0, scene);
        dofComposite.SetInput(1, depth);
        dofComposite.SetInput(2, targets.DofFar);
        dofComposite.SetInput(3, targets.DofNear);
        dofComposite.SetOutput(output);
        dofComposite.Draw(context, "PostFx.DofComposite");
    }

    public void DrawMotionBlur(RenderDrawContext context, RenderView view, CameraEffectsTargets targets, TemporalHistory history, PostFxMotionBlurSettings settings,
        Texture scene, Texture depth, Texture velocity, Texture output, int samples, int frame)
    {
        var size = new Int2(targets.Width, targets.Height);
        var viewProjection = history.ViewProjection;
        Matrix.Invert(ref viewProjection, out var inverseViewProjection);

        var tiles = motionTiles.Parameters;
        tiles.Set(PostFxMotionTilesKeys.MotionDepth, depth);
        tiles.Set(PostFxMotionTilesKeys.MotionVelocity, velocity);
        tiles.Set(PostFxMotionTilesKeys.MotionOutput, targets.Motion);
        tiles.Set(PostFxMotionTilesKeys.MotionTileOutput, targets.MotionTiles);
        tiles.Set(PostFxMotionTilesKeys.MotionSize, size);
        tiles.Set(PostFxMotionTilesKeys.MotionJitterDelta, history.JitterNdc - history.PreviousJitterNdc);
        tiles.Set(PostFxMotionTilesKeys.MotionJitterNdc, history.JitterNdc);
        tiles.Set(PostFxMotionTilesKeys.MotionInverseViewProjection, inverseViewProjection);
        tiles.Set(PostFxMotionTilesKeys.MotionPreviousViewProjection, history.HasPrevious ? history.PreviousUnjitteredViewProjection : history.UnjitteredViewProjection);
        tiles.Set(PostFxMotionTilesKeys.MotionShutter, Math.Clamp(settings.Shutter, 0.0f, 1.0f));
        tiles.Set(PostFxMotionTilesKeys.MotionMaxLength, Math.Max(settings.MaxBlur * size.Y, 1.0f));
        motionTiles.ThreadGroupCounts = Groups(size.X, size.Y, CameraEffectsTargets.MotionTileSize);
        motionTiles.Draw(context, "PostFx.MotionTiles");

        var tileSize = new Int2(targets.MotionTiles.Width, targets.MotionTiles.Height);
        var neighbors = motionNeighbors.Parameters;
        neighbors.Set(PostFxMotionNeighborsKeys.NeighborSource, targets.MotionTiles);
        neighbors.Set(PostFxMotionNeighborsKeys.NeighborOutput, targets.MotionNeighbors);
        neighbors.Set(PostFxMotionNeighborsKeys.NeighborSize, tileSize);
        motionNeighbors.ThreadGroupCounts = Groups(tileSize.X, tileSize.Y, 8);
        motionNeighbors.Draw(context, "PostFx.MotionNeighbors");

        motionBlur.Parameters.Set(PostFxMotionBlurKeys.MotionBlurSamples, samples);
        motionBlur.Parameters.Set(PostFxMotionBlurKeys.MotionBlurSize, new Vector2(size.X, size.Y));
        motionBlur.Parameters.Set(PostFxMotionBlurKeys.MotionBlurDepthInfo, new Vector2(view.Projection.M33, view.Projection.M43));
        motionBlur.Parameters.Set(PostFxMotionBlurKeys.MotionBlurFrame, (float)(frame % 1024));
        motionBlur.SetInput(0, scene);
        motionBlur.SetInput(1, targets.Motion);
        motionBlur.SetInput(2, targets.MotionNeighbors);
        motionBlur.SetInput(3, depth);
        motionBlur.SetOutput(output);
        motionBlur.Draw(context, "PostFx.MotionBlur");
    }
}

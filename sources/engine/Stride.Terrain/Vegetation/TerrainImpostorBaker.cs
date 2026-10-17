// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#nullable enable
using Stride.Core;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Shaders;
using System;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// Bakes a hemi-octahedral impostor atlas from a Model at runtime.
///
/// Each frame is rendered with an orthographic camera looking at the model's bounds from one of
/// GridSize x GridSize directions, into a small staging pair of targets, then copied into its
/// tile of the atlas. Rendering per frame into staging rather than straight into the atlas with a
/// viewport keeps depth clearing simple - a shared depth buffer would have to be cleared per tile
/// anyway, and a tile sized one is cheaper to clear than a full atlas sized one.
/// </summary>
public sealed class TerrainImpostorBaker : IDisposable
{
    // Smallest frame a mip level is rendered at. Below this a tree silhouette is a couple of
    // pixels and the extra levels buy nothing.
    private const int MinMipResolution = 8;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly DynamicEffectInstance _effect;
    private readonly ParameterCollection _parameters;
    private readonly MutablePipelineState _pipelineState;

    private VertexDeclaration? _currentLayout;

    public TerrainImpostorBaker(IServiceRegistry services, GraphicsDevice graphicsDevice)
    {
        _graphicsDevice = graphicsDevice;

        _parameters = new ParameterCollection();
        _effect = new DynamicEffectInstance("TerrainImpostorBake", _parameters);
        _effect.Initialize(services);

        _pipelineState = new MutablePipelineState(graphicsDevice);
        _pipelineState.State.SetDefaults();
    }

    /// <summary>
    /// Bakes <paramref name="model"/> into a new atlas. Must be called from the render thread with
    /// a valid draw context. Returns null if the model has nothing renderable.
    /// </summary>
    public TerrainImpostorAtlas? Bake(RenderDrawContext context, Model model, int gridSize, int frameResolution)
    {
        if (model.Meshes == null || model.Meshes.Count == 0)
            return null;

        var bounds = GetBounds(model);
        var center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        var extent = (bounds.Maximum - bounds.Minimum) * 0.5f;

        // One square frame size for every direction. Using the bounding sphere radius means the
        // model never clips out of frame no matter which way we look at it, at the cost of some
        // wasted border - which is the right trade, a clipped impostor is instantly obvious.
        var radius = extent.Length();
        if (radius <= 0)
            return null;

        var atlasSize = gridSize * frameResolution;

        // Mipped, and every level is rendered rather than filtered down from the one above.
        //
        // Without mips the atlas is sampled at its top level however far away the billboard is, and
        // impostors only ever draw past the handover distance - a 256 pixel frame lands on a few
        // dozen screen pixels, so each one grabs a single arbitrary texel out of sparse alpha
        // tested foliage. Gaps get sampled, the cutoff throws them away, and the canopy reads thin
        // and dark next to the real mesh, which samples properly mipmapped leaf textures.
        //
        // Rasterising each level separately beats box filtering the level above it: the silhouette
        // stays anchored to the geometry instead of dissolving a bit more on every halving.
        var mipCount = 1;
        while ((frameResolution >> mipCount) >= MinMipResolution)
            mipCount++;

        var diffuseAtlas = Texture.New2D(_graphicsDevice, atlasSize, atlasSize, mipCount, PixelFormat.R8G8B8A8_UNorm_SRgb, TextureFlags.ShaderResource);
        var normalAtlas = Texture.New2D(_graphicsDevice, atlasSize, atlasSize, mipCount, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource);

        var commandList = context.CommandList;

        using (context.PushRenderTargetsAndRestore())
        {
            for (var mip = 0; mip < mipCount; mip++)
            {
                var mipResolution = frameResolution >> mip;

                var stagingDiffuse = Texture.New2D(_graphicsDevice, mipResolution, mipResolution, PixelFormat.R8G8B8A8_UNorm_SRgb, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                var stagingNormal = Texture.New2D(_graphicsDevice, mipResolution, mipResolution, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
                var stagingDepth = Texture.New2D(_graphicsDevice, mipResolution, mipResolution, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);

                try
                {
                    for (var y = 0; y < gridSize; y++)
                    {
                        for (var x = 0; x < gridSize; x++)
                        {
                            var direction = TerrainImpostorOctahedral.GetFrameDirection(x, y, gridSize);
                            TerrainImpostorOctahedral.GetFrameBasis(direction, out _, out var up);

                            // Camera sits off the bounds along the frame direction looking back at
                            // the centre. Near/far bracket the sphere with a small margin.
                            var eye = center + direction * (radius * 2.0f);
                            var view = Matrix.LookAtRH(eye, center, up);
                            var projection = Matrix.OrthoRH(radius * 2.0f, radius * 2.0f, radius * 0.5f, radius * 3.5f);

                            commandList.SetRenderTargets(stagingDepth, [stagingDiffuse, stagingNormal]);
                            commandList.SetViewport(new Viewport(0, 0, mipResolution, mipResolution));

                            // Transparent black: anything not covered by geometry must stay fully
                            // unoccupied so the billboard's alpha test rejects it.
                            commandList.Clear(stagingDiffuse, new Color4(0, 0, 0, 0));
                            commandList.Clear(stagingNormal, new Color4(0.5f, 0.5f, 0.5f, 0));
                            commandList.Clear(stagingDepth, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil);

                            DrawModel(context, model, view, projection);

                            var destX = x * mipResolution;
                            var destY = y * mipResolution;

                            commandList.CopyRegion(stagingDiffuse, 0, null, diffuseAtlas, mip, destX, destY, 0);
                            commandList.CopyRegion(stagingNormal, 0, null, normalAtlas, mip, destX, destY, 0);
                        }
                    }
                }
                finally
                {
                    stagingDiffuse.Dispose();
                    stagingNormal.Dispose();
                    stagingDepth.Dispose();
                }
            }
        }

        DumpAtlas(context, diffuseAtlas, model, "diffuse");
        DumpAtlas(context, normalAtlas, model, "normal");

        return new TerrainImpostorAtlas
        {
            Diffuse = diffuseAtlas,
            Normal = normalAtlas,
            GridSize = gridSize,
            FrameResolution = frameResolution,
            // The captured quad is the bounding sphere's diameter on both axes.
            WorldSize = new Vector2(radius * 2.0f, radius * 2.0f),
            CenterOffset = center,
        };
    }

    /// <summary>
    /// Writes the baked diffuse atlas to disk when DumpPath is set. This exists because the bake
    /// sits between the source model and the impostor shading, and without seeing it there is no
    /// way to tell which side a problem is on - the atlas is either right or it is not.
    /// </summary>
    public static string? DumpPath;

    private static int _dumpIndex;

    private static void DumpAtlas(RenderDrawContext context, Texture atlas, Model model, string kind)
    {
        if (string.IsNullOrEmpty(DumpPath))
            return;

        try
        {
            System.IO.Directory.CreateDirectory(DumpPath);
            var file = System.IO.Path.Combine(DumpPath, $"impostor_{_dumpIndex++}_{model.Meshes.Count}meshes_{kind}.png");

            using var stream = System.IO.File.Create(file);
            atlas.Save(context.CommandList, stream, ImageFileType.Png);
        }
        catch (Exception e)
        {
            GlobalLogger.GetLogger("TerrainImpostorBaker").Error($"atlas dump failed: {e.Message}");
        }
    }

    private void DrawModel(RenderDrawContext context, Model model, Matrix view, Matrix projection)
    {
        var commandList = context.CommandList;

        Matrix.Multiply(ref view, ref projection, out var viewProjection);

        foreach (var mesh in model.Meshes)
        {
            var draw = mesh.Draw;
            if (draw == null || draw.VertexBuffers == null || draw.VertexBuffers.Length == 0)
                continue;

            var material = GetMaterial(model, mesh);
            var diffuseMap = material?.Passes.Count > 0 ? material.Passes[0].Parameters.Get(MaterialKeys.DiffuseMap) : null;
            if (diffuseMap == null)
                continue; // Nothing sensible to bake for an untextured mesh.

            // Meshes carry their own node transform; models here are authored at the origin but
            // respect it anyway so multi part trees bake in the right place.
            var world = mesh.Parameters?.Get(TransformationKeys.World) ?? Matrix.Identity;

            Matrix.Multiply(ref world, ref view, out var worldView);
            Matrix.Multiply(ref world, ref viewProjection, out var worldViewProjection);

            _parameters.Set(TerrainImpostorBakeKeys.ImpostorWorldViewProjection, worldViewProjection);
            _parameters.Set(TerrainImpostorBakeKeys.ImpostorWorldView, worldView);
            _parameters.Set(TerrainImpostorBakeKeys.ImpostorAlphaCutoff, GetAlphaCutoff(material));
            _parameters.Set(TerrainImpostorBakeKeys.ImpostorDiffuseMap, diffuseMap);

            _effect.UpdateEffect(_graphicsDevice);

            var layout = draw.VertexBuffers[0].Declaration;
            if (_currentLayout != layout)
            {
                _currentLayout = layout;
                _pipelineState.State.InputElements = draw.VertexBuffers.CreateInputElements();
            }

            _pipelineState.State.PrimitiveType = draw.PrimitiveType;
            _pipelineState.State.RootSignature = _effect.RootSignature;
            _pipelineState.State.EffectBytecode = _effect.Effect.Bytecode;
            _pipelineState.State.RasterizerState = RasterizerStates.CullNone;
            _pipelineState.State.DepthStencilState = DepthStencilStates.Default;
            _pipelineState.State.BlendState = BlendStates.Opaque;
            _pipelineState.State.Output.RenderTargetCount = 2;
            _pipelineState.State.Output.RenderTargetFormat0 = PixelFormat.R8G8B8A8_UNorm_SRgb;
            _pipelineState.State.Output.RenderTargetFormat1 = PixelFormat.R8G8B8A8_UNorm;
            _pipelineState.State.Output.DepthStencilFormat = PixelFormat.D24_UNorm_S8_UInt;
            _pipelineState.Update();

            commandList.SetPipelineState(_pipelineState.CurrentState);

            _effect.Apply(context.GraphicsContext);

            for (var i = 0; i < draw.VertexBuffers.Length; i++)
            {
                var vb = draw.VertexBuffers[i];
                commandList.SetVertexBuffer(i, vb.Buffer, vb.Offset, vb.Stride);
            }

            if (draw.IndexBuffer != null)
            {
                commandList.SetIndexBuffer(draw.IndexBuffer.Buffer, draw.IndexBuffer.Offset, draw.IndexBuffer.Is32Bit);
                commandList.DrawIndexed(draw.DrawCount, draw.StartLocation);
            }
            else
            {
                commandList.Draw(draw.DrawCount, draw.StartLocation);
            }
        }
    }

    private static Material? GetMaterial(Model model, Mesh mesh)
    {
        if (model.Materials == null || mesh.MaterialIndex < 0 || mesh.MaterialIndex >= model.Materials.Count)
            return null;

        return model.Materials[mesh.MaterialIndex].Material;
    }

    private static float GetAlphaCutoff(Material? material)
    {
        if (material == null || material.Passes.Count == 0)
            return 0.5f;

        // MaterialTransparencyCutoffFeature routes its threshold into the alpha discard value;
        // anything without one is opaque, and the default never rejects a fully opaque texel.
        var alpha = material.Passes[0].Parameters.Get(MaterialKeys.AlphaDiscardValue);
        return alpha > 0 ? alpha : 0.5f;
    }

    private static BoundingBox GetBounds(Model model)
    {
        var bounds = model.BoundingBox;

        // A model straight off the importer usually has valid bounds, but procedurally built ones
        // and the impostor placeholder meshes in this project do not - fall back to the meshes.
        if (bounds.Extent.LengthSquared() > 0)
            return bounds;

        var result = BoundingBox.Empty;
        foreach (var mesh in model.Meshes)
        {
            BoundingBox.Merge(ref result, ref mesh.BoundingBox, out result);
        }

        return result;
    }

    public void Dispose()
    {
        _effect.Dispose();
    }
}

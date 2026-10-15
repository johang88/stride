// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Terrain.Decals;

namespace Stride.Terrain.VirtualTexturing
{
    /// <summary>
    /// Draws the terrain decals into the pages of a virtual texture, after the terrain layers.
    /// </summary>
    /// <remarks>
    /// The blend of each channel depends on the decal opacity while the alpha of the targets holds data (height, occlusion), so
    /// the decals can't use fixed-function blending: before a decal overlapping an earlier one, the page is copied to base textures
    /// that the decal shader reads and blends over.
    /// </remarks>
    internal sealed class TerrainVirtualTextureDecals : IDisposable
    {
        private readonly GraphicsDevice device;
        private readonly TerrainDecalRegistry registry;
        private readonly List<TerrainDecal> pageDecals = new();
        private readonly List<(Vector2 Min, Vector2 Max)> drawnSinceCopy = new();
        private DynamicEffectInstance effect;
        private MutablePipelineState pipelineState;
        private EffectBytecode previousBytecode;
        private Texture baseAlbedo;
        private Texture baseNormal;
        private Texture white;
        private Texture flatNormal;

        public TerrainVirtualTextureDecals(GraphicsDevice device, TerrainDecalRegistry registry)
        {
            this.device = device;
            this.registry = registry;
        }

        public void Dispose()
        {
            effect?.Dispose();
            baseAlbedo?.Dispose();
            baseNormal?.Dispose();
            white?.Dispose();
            flatNormal?.Dispose();
        }

        /// <summary>
        /// Draws the decals overlapping a page rendered into an atlas slot.
        /// </summary>
        /// <param name="pageMin">World XZ of the page corner (border included).</param>
        /// <param name="pageSize">World size of the page (border included).</param>
        /// <param name="texelSize">World size of a texel of the page.</param>
        /// <param name="slot">Pixel rectangle of the page in the atlas.</param>
        public void Draw(RenderDrawContext context, Vector2 pageMin, float pageSize, float texelSize, Rectangle slot, Texture albedoAtlas, Texture normalAtlas)
        {
            if (registry == null || registry.Decals.Count == 0)
                return;
            registry.Query(pageMin, pageMin + new Vector2(pageSize), pageDecals);
            // Decals smaller than a couple of texels don't show at this mip
            pageDecals.RemoveAll(x => MathF.Max(x.BoundsMax.X - x.BoundsMin.X, x.BoundsMax.Y - x.BoundsMin.Y) < texelSize * 2);
            if (pageDecals.Count == 0)
                return;

            EnsureResources(context, slot.Width, albedoAtlas, normalAtlas);
            var commandList = context.CommandList;
            var parameters = effect.Parameters;
            parameters.Set(TerrainVTDecalShaderKeys.DecalPage, new Vector4(pageMin, 1.0f / pageSize, 0));
            parameters.Set(TerrainVTDecalShaderKeys.DecalSlot, new Vector4(slot.X, slot.Y, 0, 0));
            parameters.Set(TerrainVTDecalShaderKeys.DecalBaseAlbedo, baseAlbedo);
            parameters.Set(TerrainVTDecalShaderKeys.DecalBaseNormal, baseNormal);

            drawnSinceCopy.Clear();
            var copied = false;
            using (context.PushRenderTargetsAndRestore())
            {
                foreach (var decal in pageDecals)
                {
                    // Copy the page when this decal blends over one drawn since the last copy
                    if (!copied || Overlaps(decal))
                    {
                        CopyPage(commandList, slot, albedoAtlas, normalAtlas);
                        drawnSinceCopy.Clear();
                        copied = true;
                    }
                    drawnSinceCopy.Add((decal.BoundsMin, decal.BoundsMax));

                    commandList.SetRenderTargets(null, albedoAtlas, normalAtlas);
                    commandList.SetViewport(new Viewport(slot.X, slot.Y, slot.Width, slot.Height));
                    SetSurface(parameters, decal);
                    if (!Apply(context))
                        return;
                    decal.Draw(device, commandList);
                }
            }
        }

        private bool Overlaps(TerrainDecal decal)
        {
            foreach (var (min, max) in drawnSinceCopy)
            {
                if (decal.BoundsMax.X >= min.X && decal.BoundsMin.X <= max.X && decal.BoundsMax.Y >= min.Y && decal.BoundsMin.Y <= max.Y)
                    return true;
            }
            return false;
        }

        private void CopyPage(CommandList commandList, Rectangle slot, Texture albedoAtlas, Texture normalAtlas)
        {
            var region = new ResourceRegion(slot.X, slot.Y, 0, slot.Right, slot.Bottom, 1);
            commandList.CopyRegion(albedoAtlas, 0, region, baseAlbedo, 0);
            commandList.CopyRegion(normalAtlas, 0, region, baseNormal, 0);
            commandList.ResourceBarrierTransition(baseAlbedo, BarrierLayout.ShaderResource);
            commandList.ResourceBarrierTransition(baseNormal, BarrierLayout.ShaderResource);
            commandList.ResourceBarrierTransition(albedoAtlas, BarrierLayout.RenderTarget);
            commandList.ResourceBarrierTransition(normalAtlas, BarrierLayout.RenderTarget);
        }

        private void SetSurface(ParameterCollection parameters, TerrainDecal decal)
        {
            var surface = decal.Surface;
            var tint = surface.Tint;
            parameters.Set(TerrainVTDecalShaderKeys.DecalTint, new Vector4(tint.R, tint.G, tint.B, tint.A * surface.Opacity));
            parameters.Set(TerrainVTDecalShaderKeys.DecalParams, new Vector4(surface.NormalStrength, (float)surface.NormalBlend, surface.Roughness, surface.Height));
            parameters.Set(TerrainVTDecalShaderKeys.DecalFlags, new Vector4(surface.AffectAlbedo ? 1 : 0, surface.AffectRoughness ? 1 : 0, surface.AffectHeight ? 1 : 0, decal.Shape == TerrainDecalShape.Ellipse ? 1 : 0));
            parameters.Set(TerrainVTDecalShaderKeys.DecalFade, new Vector4(surface.EdgeFade, 0, 0, 0));
            parameters.Set(TerrainVTDecalShaderKeys.DecalAlbedo, surface.Albedo ?? white);
            parameters.Set(TerrainVTDecalShaderKeys.DecalNormal, surface.Normal ?? flatNormal);
            parameters.Set(TerrainVTDecalShaderKeys.DecalRoughness, surface.RoughnessMap ?? white);
        }

        private bool Apply(RenderDrawContext context)
        {
            if (effect.UpdateEffect(device) || previousBytecode != effect.Effect?.Bytecode)
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

        private void EnsureResources(RenderDrawContext context, int pageSize, Texture albedoAtlas, Texture normalAtlas)
        {
            if (effect == null)
            {
                effect = new DynamicEffectInstance("TerrainVTDecalShader");
                effect.Initialize(context.RenderContext.Services);
                effect.EffectCompilerParameters.TaskPriority = -1;

                pipelineState = new MutablePipelineState(device);
                pipelineState.State.SetDefaults();
                pipelineState.State.InputElements = TerrainDecalVertex.Layout.CreateInputElements();
                pipelineState.State.PrimitiveType = PrimitiveType.TriangleList;
                pipelineState.State.BlendState = BlendStates.Opaque;
                pipelineState.State.DepthStencilState = DepthStencilStates.None;
                pipelineState.State.RasterizerState = RasterizerStates.CullNone;

                white = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });
                flatNormal = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(128, 128, 255, 255) });
            }

            if (baseAlbedo == null || baseAlbedo.Width != pageSize)
            {
                baseAlbedo?.Dispose();
                baseNormal?.Dispose();
                baseAlbedo = Texture.New2D(device, pageSize, pageSize, albedoAtlas.Format, TextureFlags.ShaderResource);
                baseAlbedo.Name = "TerrainVTDecalBaseAlbedo";
                baseNormal = Texture.New2D(device, pageSize, pageSize, normalAtlas.Format, TextureFlags.ShaderResource);
                baseNormal.Name = "TerrainVTDecalBaseNormal";
            }
        }
    }
}

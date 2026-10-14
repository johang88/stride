// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain.Rendering;

namespace Stride.Terrain.VirtualTexturing
{
    /// <summary>
    /// Material parameter keys related to the terrain virtual texture.
    /// </summary>
    public static class TerrainVirtualTextureKeys
    {
        /// <summary>
        /// Set by material features sampling the terrain virtual texture: their objects are drawn in the feedback pass.
        /// </summary>
        public static readonly PermutationParameterKey<bool> SamplesVirtualTexture = ParameterKeys.NewPermutation(false);
    }

    /// <summary>
    /// Puts the objects sampling the terrain virtual texture in its feedback render stage.
    /// </summary>
    [DataContract("TerrainVirtualTextureStageSelector")]
    public sealed class TerrainVirtualTextureStageSelector : RenderStageSelector
    {
        [DataMemberIgnore]
        public TerrainVirtualTextureManager Manager { get; set; }

        public string EffectName { get; set; }

        public override void Process(RenderObject renderObject)
        {
            var stage = Manager?.FeedbackStage;
            if (stage == null || stage.Index < 0)
                return;

            var samples = renderObject switch
            {
                TerrainRenderObject terrain => terrain.UsesVirtualTexture,
                RenderMesh mesh => mesh.MaterialPass?.Parameters.Get(TerrainVirtualTextureKeys.SamplesVirtualTexture) ?? false,
                _ => false,
            };
            if (samples)
                renderObject.ActiveRenderStages[stage.Index] = new ActiveRenderStage(EffectName);
        }
    }

    /// <summary>
    /// Binds the active terrain virtual texture (per frame) to the shaders sampling it.
    /// </summary>
    [DataContract("TerrainVirtualTextureRenderFeature")]
    public sealed class TerrainVirtualTextureRenderFeature : SubRenderFeature
    {
        private LogicalGroupReference groupKey;
        private ConstantBufferOffsetReference regionOffset;
        private ConstantBufferOffsetReference atlasOffset;
        private ConstantBufferOffsetReference paramsOffset;
        private ConstantBufferOffsetReference heightOffset;
        private ConstantBufferOffsetReference originOffset;
        private Texture dummyPageTable;
        private Texture dummyColor;
        private Texture dummyHeight;

        [DataMemberIgnore]
        public TerrainVirtualTextureManager Manager { get; set; }

        protected override void InitializeCore()
        {
            groupKey = ((RootEffectRenderFeature)RootRenderFeature).CreateFrameLogicalGroup("TerrainVirtualTexture");
            var root = (RootEffectRenderFeature)RootRenderFeature;
            regionOffset = root.CreateFrameCBufferOffsetSlot(TerrainVirtualTextureBaseKeys.TerrainVTRegion.Name);
            atlasOffset = root.CreateFrameCBufferOffsetSlot(TerrainVirtualTextureBaseKeys.TerrainVTAtlas.Name);
            paramsOffset = root.CreateFrameCBufferOffsetSlot(TerrainVirtualTextureBaseKeys.TerrainVTParams.Name);
            heightOffset = root.CreateFrameCBufferOffsetSlot(TerrainVirtualTextureBaseKeys.TerrainVTHeight.Name);
            originOffset = root.CreateFrameCBufferOffsetSlot(TerrainVirtualTextureBaseKeys.TerrainVTOrigin.Name);
            dummyPageTable = Texture.New2D(Context.GraphicsDevice, 1, 1, PixelFormat.R16_UInt, new ushort[] { 0 });
            dummyColor = Texture.New2D(Context.GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { new Color(128, 128, 128, 255) });
            dummyHeight = Texture.New2D(Context.GraphicsDevice, 1, 1, PixelFormat.R16_UNorm, new ushort[] { 0 });
        }

        protected override void Destroy()
        {
            dummyPageTable?.Dispose();
            dummyColor?.Dispose();
            dummyHeight?.Dispose();
            base.Destroy();
        }

        private static unsafe void Write(byte* data, int offset, Vector4 value)
        {
            if (offset >= 0 && data != null)
                *(Vector4*)(data + offset) = value;
        }

        public override unsafe void Prepare(RenderDrawContext context)
        {
            var vt = Manager is { IsActive: true } ? Manager.Active : null;
            var terrain = vt?.Terrain;
            var data = terrain?.Data;

            var region = Vector4.Zero;
            var atlas = Vector4.Zero;
            var parameters = Vector4.Zero;
            var height = new Vector4(0, 0, 1, 1);
            var origin = Vector4.Zero;
            if (vt != null && data != null && terrain.Gpu != null)
            {
                var position = terrain.World.TranslationVector;
                region = new Vector4(position.X, position.Z, 1.0f / vt.VirtualWorldSize, vt.PagesAtMip0);
                var atlasSize = vt.CachePagesPerSide * TerrainVirtualTexture.PhysicalPageSize;
                atlas = new Vector4(TerrainVirtualTexture.PhysicalPageSize, TerrainVirtualTexture.PageBorder, TerrainVirtualTexture.PageContentSize, 1.0f / atlasSize);
                parameters = new Vector4(vt.MipCount - 1, vt.MipBias, 1, vt.PagesAtMip0 * TerrainVirtualTexture.PageContentSize);
                height = new Vector4(data.MinHeight, data.MaxHeight - data.MinHeight, 1.0f / data.CellSize, data.Resolution);
                origin = new Vector4(position, MathF.Log2(vt.FeedbackDownscale));
            }

            foreach (var frameLayout in ((RootEffectRenderFeature)RootRenderFeature).FrameLayouts)
            {
                var group = frameLayout.GetLogicalGroup(groupKey);
                if (group.Hash == ObjectId.Empty)
                    continue;

                var resources = frameLayout.Entry.Resources;
                if (group.DescriptorEntryStart != -1)
                {
                    var builder = frameLayout.DescriptorSetLayoutBuilder;
                    for (int i = group.DescriptorEntryStart; i < group.DescriptorEntryStart + group.DescriptorEntryCount; i++)
                    {
                        var key = builder.GetEntryKey(i);
                        Texture texture = null;
                        if (key == TerrainVirtualTextureBaseKeys.TerrainVTPageTable) texture = vt?.PageTable ?? dummyPageTable;
                        else if (key == TerrainVirtualTextureBaseKeys.TerrainVTAlbedo) texture = vt?.AlbedoAtlas ?? dummyColor;
                        else if (key == TerrainVirtualTextureBaseKeys.TerrainVTNormal) texture = vt?.NormalAtlas ?? dummyColor;
                        else if (key == TerrainVirtualTextureBaseKeys.TerrainVTHeightmap) texture = terrain?.Gpu?.Heightmap ?? dummyHeight;
                        if (texture != null)
                            resources.DescriptorSet.SetShaderResourceView(i, texture);
                    }
                }

                var data0 = (byte*)resources.ConstantBuffer.Data;
                Write(data0, frameLayout.GetConstantBufferOffset(regionOffset), region);
                Write(data0, frameLayout.GetConstantBufferOffset(atlasOffset), atlas);
                Write(data0, frameLayout.GetConstantBufferOffset(paramsOffset), parameters);
                Write(data0, frameLayout.GetConstantBufferOffset(heightOffset), height);
                Write(data0, frameLayout.GetConstantBufferOffset(originOffset), origin);
            }
        }
    }
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Terrain.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain;

/// <summary>
/// Creates and updates the render objects of <see cref="TerrainComponent"/>s.
/// </summary>
public sealed class TerrainProcessor : EntityProcessor<TerrainComponent, TerrainRenderObject>, IEntityComponentRenderProcessor
{
    /// <summary>
    /// Number of quads along each side of the patch drawn for each terrain node.
    /// </summary>
    public const int PatchQuads = 32;

    private static readonly Stride.Core.Diagnostics.Logger Log = Stride.Core.Diagnostics.GlobalLogger.GetLogger("Terrain");

    private GraphicsDevice graphicsDevice;
    private Mesh patchMesh;
    private VirtualTexturing.TerrainVirtualTextureManager virtualTextures;
    private Shadows.TerrainShadowManager shadows;

    public TerrainProcessor() : base(typeof(TransformComponent))
    {
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    /// <summary>
    /// Raised (from <see cref="Draw"/>) when a terrain gets new GPU resources, e.g. to let the virtual texture follow it.
    /// </summary>
    public event Action<TerrainComponent, TerrainRenderObject> TerrainResourcesChanged;

    protected override void OnSystemAdd()
    {
        graphicsDevice = Services.GetSafeServiceAs<IGraphicsDeviceService>().GraphicsDevice;
        patchMesh = CreatePatchMesh(graphicsDevice, PatchQuads);
        virtualTextures = new VirtualTexturing.TerrainVirtualTextureManager(graphicsDevice);
        Services.AddService(virtualTextures);
        shadows = new Shadows.TerrainShadowManager(graphicsDevice);
        Services.AddService(shadows);
    }

    protected override void OnSystemRemove()
    {
        foreach (var renderObject in ComponentDatas.Values)
            ReleaseResources(renderObject);
        patchMesh?.Draw.VertexBuffers[0].Buffer.Dispose();
        patchMesh?.Draw.IndexBuffer.Buffer.Dispose();
        patchMesh = null;
        VisibilityGroup?.RenderSystem.ViewExtensions.Remove(virtualTextures);
        Services.RemoveService<VirtualTexturing.TerrainVirtualTextureManager>();
        virtualTextures?.Dispose();
        virtualTextures = null;
        VisibilityGroup?.RenderSystem.ViewExtensions.Remove(shadows);
        Services.RemoveService<Shadows.TerrainShadowManager>();
        shadows?.Dispose();
        shadows = null;
    }

    protected override TerrainRenderObject GenerateComponentData(Entity entity, TerrainComponent component)
    {
        return new TerrainRenderObject
        {
            Source = component,
            Component = component,
            Mesh = patchMesh,
        };
    }

    protected override bool IsAssociatedDataValid(Entity entity, TerrainComponent component, TerrainRenderObject associatedData)
    {
        return associatedData.Component == component;
    }

    protected override void OnEntityComponentRemoved(Entity entity, TerrainComponent component, TerrainRenderObject renderObject)
    {
        lock (VisibilityGroup.RenderObjects)
            VisibilityGroup.RenderObjects.Remove(renderObject);
        ReleaseResources(renderObject);
    }

    /// <summary>
    /// Gets the render object of a terrain (null if the terrain isn't processed).
    /// </summary>
    /// <summary>
    /// The terrains of the scene.
    /// </summary>
    public IEnumerable<TerrainComponent> Terrains => ComponentDatas.Keys;

    public TerrainRenderObject GetRenderObject(TerrainComponent component) => ComponentDatas.TryGetValue(component, out var renderObject) ? renderObject : null;

    /// <summary>
    /// Shows a brush outline on a terrain (editor tools). A radius of 0 hides it.
    /// </summary>
    /// <param name="component">The terrain.</param>
    /// <param name="localCenter">Brush center in terrain local XZ.</param>
    /// <param name="radius">Outer radius in meters.</param>
    /// <param name="innerRadius">Radius where the brush starts fading out.</param>
    /// <param name="color">Outline color (alpha is the opacity).</param>
    /// <param name="mask">Shape of a custom brush (red channel, see <see cref="Editing.TerrainBrushMask"/>), or null for a round brush.</param>
    /// <param name="rotation">Rotation of the mask in radians.</param>
    public void SetBrushOverlay(TerrainComponent component, Vector2 localCenter, float radius, float innerRadius, Color4 color, Texture mask = null, float rotation = 0)
    {
        var pass = GetRenderObject(component)?.MaterialPass;
        if (pass == null)
            return;
        pass.Parameters.Set(TerrainBrushOverlayKeys.TerrainBrush, new Vector4(localCenter, radius, innerRadius));
        pass.Parameters.Set(TerrainBrushOverlayKeys.TerrainBrushColor, (Vector4)color);
        pass.Parameters.Set(TerrainBrushOverlayKeys.TerrainBrushShape, new Vector4(mask != null ? 1 : 0, MathF.Cos(rotation), MathF.Sin(rotation), 0));
        if (mask != null)
            pass.Parameters.Set(TerrainBrushOverlayKeys.TerrainBrushMask, mask);
    }

    public override void Draw(RenderContext context)
    {
        EnsureRenderFeature(VisibilityGroup.RenderSystem);
        virtualTextures.Register(VisibilityGroup.RenderSystem);
        shadows.Register(VisibilityGroup.RenderSystem);
        VirtualTexturing.TerrainVirtualTexture activeVirtualTexture = null;
        TerrainRenderObject activeShadowCaster = null;

        foreach (var (component, renderObject) in ComponentDatas)
        {
            var data = component.Terrain;
            var vtSettings = component.VirtualTexture ?? new VirtualTexturing.TerrainVirtualTextureSettings { Enabled = false };
            var vtConfig = (vtSettings.Enabled && component.Shading == TerrainShadingMode.Automatic, (int)vtSettings.Density, vtSettings.CachePagesPerSide, vtSettings.FeedbackDownscale);
            if (data != renderObject.Data || data?.Material != renderObject.TerrainMaterial || vtConfig != renderObject.VirtualTextureConfig)
                RebuildResources(component, renderObject, vtConfig);
            if (renderObject.VirtualTexture != null)
            {
                renderObject.VirtualTexture.MaxPageUpdatesPerFrame = vtSettings.MaxPageUpdatesPerFrame;
                renderObject.VirtualTexture.MipBias = vtSettings.MipBias;
                if (component.Enabled && activeVirtualTexture == null)
                    activeVirtualTexture = renderObject.VirtualTexture;
            }

            var visible = component.Enabled && renderObject.QuadTree != null && renderObject.MaterialPass != null;
            lock (VisibilityGroup.RenderObjects)
            {
                var registered = VisibilityGroup.RenderObjects.Contains(renderObject);
                if (visible && !registered)
                    VisibilityGroup.RenderObjects.Add(renderObject);
                else if (!visible && registered)
                    VisibilityGroup.RenderObjects.Remove(renderObject);
            }
            if (!visible)
                continue;
            if (component.CastShadows)
                activeShadowCaster ??= renderObject;

            if (Math.Abs(renderObject.QuadTree.LodDistance - component.LodDistance) > 1e-3f)
                UpdateLodDistance(renderObject, component.LodDistance);

            // Terrains are only translated
            var position = component.Entity.Transform.WorldMatrix.TranslationVector;
            renderObject.World = Matrix.Translation(position);
            renderObject.RenderGroup = component.RenderGroup;
            if (renderObject.IsShadowCaster != component.CastShadows)
            {
                renderObject.IsShadowCaster = component.CastShadows;
                VisibilityGroup.NeedActiveRenderStageReevaluation = true;
            }

            var bounds = renderObject.QuadTree.GetBounds();
            renderObject.BoundingBox = new BoundingBoxExt(new BoundingBox(bounds.Minimum + position, bounds.Maximum + position));
        }

        virtualTextures.Active = activeVirtualTexture;
        shadows.Active = activeShadowCaster;
        foreach (var renderObject in ComponentDatas.Values)
        {
            if (renderObject.UsesVirtualTexture && renderObject.MaterialPass != null)
                renderObject.MaterialPass.Parameters.Set(TerrainMaterialSurfaceVirtualTextureKeys.TerrainVTIsOwn, renderObject.VirtualTexture == activeVirtualTexture && activeVirtualTexture.IsReady ? 1.0f : 0.0f);
        }
    }

    private void RebuildResources(TerrainComponent component, TerrainRenderObject renderObject, (bool Enabled, int Density, int Cache, int Feedback) vtConfig)
    {
        ReleaseResources(renderObject);
        renderObject.VirtualTextureConfig = vtConfig;
        var data = component.Terrain;
        renderObject.Data = data;
        renderObject.TerrainMaterial = data?.Material;
        // No samples: an empty placeholder of the asset while it's not loaded yet (editor)
        if (data == null || data.Resolution < 2)
            return;

        try
        {
            renderObject.QuadTree = new TerrainQuadTree(data, PatchQuads);
            renderObject.QuadTree.SetLodDistance(component.LodDistance);
        }
        catch (ArgumentException e)
        {
            renderObject.QuadTree = null;
            Log.Error($"Terrain [{component.Entity?.Name}] can't be rendered: {e.Message}");
            return;
        }

        renderObject.Gpu = new TerrainGpuResources(graphicsDevice, data);
        data.Changed += renderObject.OnDataChanged;

        renderObject.UsesVirtualTexture = vtConfig.Enabled;
        if (vtConfig.Enabled)
            renderObject.VirtualTexture = new VirtualTexturing.TerrainVirtualTexture(graphicsDevice, renderObject, component.VirtualTexture, Services.GetOrCreate<Decals.TerrainDecalRegistry>());

        var surfaceShader = vtConfig.Enabled ? "TerrainMaterialSurfaceVirtualTexture" : "TerrainMaterialSurface";
        renderObject.Material = Material.New(graphicsDevice, TerrainMaterialFactory.CreateDescriptor(surfaceShader), Services.GetService<Stride.Core.Serialization.Contents.ContentManager>());
        renderObject.MaterialPass = renderObject.Material.Passes[0];
        renderObject.Mesh = patchMesh;
        renderObject.IsShadowCaster = component.CastShadows;
        TerrainMaterialParameters.Apply(renderObject.MaterialPass.Parameters, renderObject);
        VisibilityGroup.NeedActiveRenderStageReevaluation = true;

        TerrainResourcesChanged?.Invoke(component, renderObject);
    }

    private static void UpdateLodDistance(TerrainRenderObject renderObject, float lodDistance)
    {
        renderObject.QuadTree.SetLodDistance(lodDistance);
        TerrainMaterialParameters.ApplyMorph(renderObject.MaterialPass.Parameters, renderObject.QuadTree);
    }

    private static void ReleaseResources(TerrainRenderObject renderObject)
    {
        if (renderObject.Data != null)
            renderObject.Data.Changed -= renderObject.OnDataChanged;
        renderObject.VirtualTexture?.Dispose();
        renderObject.VirtualTexture = null;
        renderObject.UsesVirtualTexture = false;
        renderObject.Gpu?.Dispose();
        renderObject.Gpu = null;
        renderObject.QuadTree = null;
        renderObject.Material = null;
        renderObject.MaterialPass = null;
        renderObject.Data = null;
        renderObject.TerrainMaterial = null;
    }

    private static void EnsureRenderFeature(RenderSystem renderSystem)
    {
        foreach (var feature in renderSystem.RenderFeatures)
        {
            if (feature is MeshRenderFeature meshRenderFeature && !meshRenderFeature.RenderFeatures.Any(x => x is TerrainRenderFeature))
                meshRenderFeature.RenderFeatures.Add(new TerrainRenderFeature());
        }
    }

    private static Mesh CreatePatchMesh(GraphicsDevice device, int quads)
    {
        var side = quads + 1;
        var vertices = new Vector3[side * side];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
                vertices[z * side + x] = new Vector3(x / (float)quads, 0, z / (float)quads);
        }

        // Same diagonal as TerrainData.SampleHeight and the colliders: (0,0)-(1,1)
        var indices = new ushort[quads * quads * 6];
        var i = 0;
        for (int z = 0; z < quads; z++)
        {
            for (int x = 0; x < quads; x++)
            {
                var i00 = (ushort)(z * side + x);
                var i10 = (ushort)(i00 + 1);
                var i01 = (ushort)(i00 + side);
                var i11 = (ushort)(i01 + 1);
                indices[i++] = i00; indices[i++] = i10; indices[i++] = i11;
                indices[i++] = i00; indices[i++] = i11; indices[i++] = i01;
            }
        }

        var vertexBuffer = Buffer.Vertex.New(device, vertices, GraphicsResourceUsage.Immutable);
        var indexBuffer = Buffer.Index.New(device, indices);
        var layout = new VertexDeclaration(VertexElement.Position<Vector3>());

        return new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                IndexBuffer = new IndexBufferBinding(indexBuffer, false, indices.Length),
                VertexBuffers = [new VertexBufferBinding(vertexBuffer, layout, vertices.Length)],
            },
            BoundingBox = new BoundingBox(Vector3.Zero, Vector3.One),
        };
    }
}

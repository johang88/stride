// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Water.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Water;

/// <summary>
/// Draws the <see cref="WaterSurfaceComponent"/>s (a flat mesh each, in a model component it adds to their entity), answers
/// where the water is (<see cref="TryGetSurface"/>), and runs the dynamic ripples of the scene: disturbances added by
/// <see cref="AddRipple"/> during the update (see <see cref="WaterRippleEmitterProcessor"/>) are handed to the
/// <see cref="WaterRippleRenderFeature"/>, which simulates them on the GPU around the camera.
/// </summary>
public sealed class WaterSurfaceProcessor : EntityProcessor<WaterSurfaceComponent, WaterSurfaceProcessor.SurfaceData>, IEntityComponentRenderProcessor
{
    public sealed class SurfaceData
    {
        internal ModelComponent Model;
        internal Buffer VertexBuffer;
        internal Buffer IndexBuffer;
        internal Vector2 BuiltSize;
        internal Material Material;
    }

    private readonly List<Vector4> ripples = new();
    private IGraphicsDeviceService graphicsDeviceService;
    private float rippleTime;
    private float rippleLevel = float.NaN;

    public WaterSurfaceProcessor() : base(typeof(TransformComponent))
    {
        Order = 100;
    }

    public VisibilityGroup VisibilityGroup { get; set; }

    /// <summary>
    /// The water surfaces of the scene.
    /// </summary>
    public IEnumerable<WaterSurfaceComponent> Surfaces => ComponentDatas.Keys;

    protected override void OnSystemAdd()
    {
        graphicsDeviceService = Services.GetService<IGraphicsDeviceService>();
    }

    protected override SurfaceData GenerateComponentData(Entity entity, WaterSurfaceComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, WaterSurfaceComponent component, SurfaceData associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, WaterSurfaceComponent component, SurfaceData data)
    {
        if (data.Model != null)
        {
            entity.Remove(data.Model);
            data.Model = null;
        }
        ReleaseBuffers(data);
    }

    /// <summary>
    /// The water at a position: the height of the highest active surface over it whose volume (down to its depth) contains it.
    /// </summary>
    /// <param name="position">A world position (e.g. a character's feet).</param>
    /// <param name="height">The world height of the water's surface there.</param>
    /// <param name="surface">The surface.</param>
    /// <returns>Whether the position is in (or above) a body of water.</returns>
    public bool TryGetSurface(Vector3 position, out float height, out WaterSurfaceComponent surface)
    {
        height = float.NegativeInfinity;
        surface = null;
        foreach (var component in ComponentDatas.Keys)
        {
            if (!component.Enabled)
                continue;
            ref readonly var world = ref component.Entity.Transform.WorldMatrix;
            Matrix.Invert(in world, out var inverse);
            var local = Vector3.TransformCoordinate(position, inverse);
            var half = component.Size * 0.5f;
            if (MathF.Abs(local.X) > half.X || MathF.Abs(local.Z) > half.Y || local.Y < -component.Depth)
                continue;
            var surfaceHeight = Vector3.TransformCoordinate(new Vector3(local.X, 0, local.Z), world).Y;
            if (surfaceHeight > height)
            {
                height = surfaceHeight;
                surface = component;
            }
        }
        return surface != null;
    }

    /// <summary>
    /// Disturbs the water (dynamic ripples): adds a smooth bump to the water's height for the next simulation step.
    /// </summary>
    /// <param name="position">World position of the center.</param>
    /// <param name="surfaceHeight">Height of the water's surface there (where the ripple simulation follows the camera).</param>
    /// <param name="radius">Radius of the bump (m).</param>
    /// <param name="height">Height added at the center (m): negative pushes the water down.</param>
    public void AddRipple(Vector3 position, float surfaceHeight, float radius, float height)
    {
        ripples.Add(new Vector4(position.X, position.Z, radius, height));
        rippleLevel = float.IsNaN(rippleLevel) ? surfaceHeight : MathUtil.Lerp(rippleLevel, surfaceHeight, 0.1f);
    }

    public override void Update(GameTime time)
    {
        rippleTime += (float)time.WarpElapsed.TotalSeconds;

        foreach (var (component, data) in ComponentDatas)
        {
            if (!component.Enabled || graphicsDeviceService?.GraphicsDevice == null || component.Material == null)
            {
                if (data.Model != null)
                    data.Model.Enabled = false;
                continue;
            }

            var size = new Vector2(MathF.Max(component.Size.X, 0.01f), MathF.Max(component.Size.Y, 0.01f));
            if (data.Model == null || size != data.BuiltSize)
                BuildModel(component, data, size);
            if (data.Material != component.Material)
            {
                data.Material = component.Material;
                data.Model.Model.Materials[0] = new MaterialInstance(component.Material);
            }
            data.Model.Enabled = true;
            data.Model.IsShadowCaster = false;
            data.Model.RenderGroup = component.RenderGroup;
        }
    }

    public override void Draw(RenderContext context)
    {
        var renderSystem = VisibilityGroup?.RenderSystem;
        if (renderSystem == null)
            return;

        var rippling = ComponentDatas.Keys.Any(x => x.Enabled && x.Ripples);
        foreach (var feature in renderSystem.RenderFeatures)
        {
            if (feature is not MeshRenderFeature meshRenderFeature)
                continue;
            var ripplesFeature = meshRenderFeature.RenderFeatures.OfType<WaterRippleRenderFeature>().FirstOrDefault();
            if (ripplesFeature == null)
            {
                ripplesFeature = new WaterRippleRenderFeature();
                meshRenderFeature.RenderFeatures.Add(ripplesFeature);
            }

            if (float.IsNaN(rippleLevel))
                rippleLevel = ComponentDatas.Keys.FirstOrDefault(x => x.Enabled)?.Entity.Transform.WorldMatrix.TranslationVector.Y ?? 0.0f;
            ripplesFeature.Submit(rippling, rippleTime, rippleLevel, CollectionsMarshal.AsSpan(ripples));
        }
        ripples.Clear();
        rippleTime = 0.0f;
    }

    private void BuildModel(WaterSurfaceComponent component, SurfaceData data, Vector2 size)
    {
        ReleaseBuffers(data);
        var device = graphicsDeviceService.GraphicsDevice;
        var half = size * 0.5f;
        var tangent = new Vector4(1, 0, 0, 1);
        var vertices = new[]
        {
            new WaterVertex(new Vector3(-half.X, 0, -half.Y), tangent, new Vector2(-half.X, -half.Y)),
            new WaterVertex(new Vector3(half.X, 0, -half.Y), tangent, new Vector2(half.X, -half.Y)),
            new WaterVertex(new Vector3(half.X, 0, half.Y), tangent, new Vector2(half.X, half.Y)),
            new WaterVertex(new Vector3(-half.X, 0, half.Y), tangent, new Vector2(-half.X, half.Y)),
        };
        // (the winding of Stride's up-facing planes)
        var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };
        data.VertexBuffer = Buffer.Vertex.New(device, vertices);
        data.IndexBuffer = Buffer.Index.New(device, indices);
        data.BuiltSize = size;

        var bounds = new BoundingBox(new Vector3(-half.X, -0.05f, -half.Y), new Vector3(half.X, 0.05f, half.Y));
        var model = new Model();
        model.Materials.Add(new MaterialInstance(component.Material));
        model.Meshes.Add(new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                IndexBuffer = new IndexBufferBinding(data.IndexBuffer, false, indices.Length),
                VertexBuffers = new[] { new VertexBufferBinding(data.VertexBuffer, WaterVertex.Layout, vertices.Length) },
            },
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds),
            MaterialIndex = 0,
        });
        model.BoundingBox = bounds;
        model.BoundingSphere = BoundingSphere.FromBox(bounds);
        data.Material = component.Material;

        if (data.Model == null)
        {
            data.Model = new ModelComponent();
            component.Entity.Add(data.Model);
        }
        data.Model.Model = model;
    }

    private static void ReleaseBuffers(SurfaceData data)
    {
        data.VertexBuffer?.Dispose();
        data.IndexBuffer?.Dispose();
        data.VertexBuffer = null;
        data.IndexBuffer = null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct WaterVertex(Vector3 position, Vector4 tangent, Vector2 texCoord)
    {
        public static readonly VertexDeclaration Layout = new(
            VertexElement.Position<Vector3>(),
            VertexElement.Normal<Vector3>(),
            VertexElement.Tangent<Vector4>(),
            VertexElement.TextureCoordinate<Vector2>());

        public readonly Vector3 Position = position;
        public readonly Vector3 Normal = Vector3.UnitY;
        public readonly Vector4 Tangent = tangent;
        public readonly Vector2 TexCoord = texCoord;
    }
}

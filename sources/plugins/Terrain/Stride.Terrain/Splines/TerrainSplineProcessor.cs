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
using Stride.Terrain.Decals;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain.Splines;

/// <summary>
/// Generates the meshes of the terrain splines that have a material, following the curve and the terrain, and the decals
/// of the splines baked into the terrain virtual texture. Rebuilt when the control points, the settings or the terrain change.
/// </summary>
public sealed class TerrainSplineProcessor : EntityProcessor<TerrainSplineComponent, TerrainSplineProcessor.SplineData>
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 TexCoord;

        public static readonly VertexDeclaration Layout = new(
            VertexElement.Position<Vector3>(),
            VertexElement.Normal<Vector3>(),
            VertexElement.Tangent<Vector4>(),
            VertexElement.TextureCoordinate<Vector2>());
    }

    public sealed class SplineData
    {
        internal ModelComponent Model;
        internal Buffer VertexBuffer;
        internal Buffer IndexBuffer;
        internal int Hash;
        internal int TerrainVersion = -1;
        internal TerrainDecal Decal;
        internal int DecalHash;
    }

    private readonly HashSet<TerrainData> watchedTerrains = new();
    private int terrainVersion;
    private float terrainChangeDelay;
    private IGraphicsDeviceService graphicsDeviceService;
    private TerrainDecalRegistry decalRegistry;

    public TerrainSplineProcessor() : base(typeof(TransformComponent))
    {
        Order = 100;
    }

    /// <summary>
    /// The terrain splines of the scene.
    /// </summary>
    public IEnumerable<TerrainSplineComponent> Splines => ComponentDatas.Keys;

    protected override void OnSystemAdd()
    {
        graphicsDeviceService = Services.GetService<IGraphicsDeviceService>();
        decalRegistry = Services.GetOrCreate<TerrainDecalRegistry>();
    }

    protected override SplineData GenerateComponentData(Entity entity, TerrainSplineComponent component) => new();

    protected override bool IsAssociatedDataValid(Entity entity, TerrainSplineComponent component, SplineData associatedData) => true;

    protected override void OnEntityComponentRemoved(Entity entity, TerrainSplineComponent component, SplineData data)
    {
        RemoveMesh(entity, data);
        RemoveDecal(data);
    }

    protected override void OnSystemRemove()
    {
        foreach (var terrain in watchedTerrains)
            terrain.Changed -= OnTerrainChanged;
        watchedTerrains.Clear();
    }

    public override void Update(GameTime time)
    {
        // Height edits move the conforming meshes; rebuild a little after the edits settle
        var terrains = EntityManager.GetProcessor<TerrainProcessor>()?.Terrains.ToList() ?? new List<TerrainComponent>();
        foreach (var terrain in terrains)
        {
            if (terrain.Terrain != null && watchedTerrains.Add(terrain.Terrain))
            {
                terrain.Terrain.Changed += OnTerrainChanged;
                terrainVersion++;
            }
        }
        if (terrainChangeDelay > 0)
            terrainChangeDelay -= (float)time.Elapsed.TotalSeconds;

        foreach (var (spline, data) in ComponentDatas)
        {
            UpdateDecal(spline, data);

            if (spline.MeshMaterial == null || spline.Entity.Transform.Children.Count < 2)
            {
                RemoveMesh(spline.Entity, data);
                continue;
            }

            var hash = ComputeHash(spline);
            var terrainDirty = data.TerrainVersion != terrainVersion && spline.ConformToTerrain && terrainChangeDelay <= 0;
            if (hash == data.Hash && !terrainDirty && data.Model != null)
                continue;
            data.Hash = hash;
            data.TerrainVersion = terrainVersion;
            BuildMesh(spline, data, terrains);
        }
    }

    private void OnTerrainChanged(object sender, TerrainDataChangedEventArgs e)
    {
        // Only the heights move the meshes (painting and grass don't)
        if ((e.Flags & TerrainChangeFlags.Heights) == 0)
            return;
        terrainVersion++;
        terrainChangeDelay = 0.25f;
    }

    private static void AddCurveHash(ref HashCode hash, TerrainSplineComponent spline)
    {
        hash.Add(spline.Entity.Transform.WorldMatrix);
        foreach (var child in spline.Entity.Transform.Children)
        {
            hash.Add(child.Position);
            hash.Add(child.Scale.X);
        }
        hash.Add(spline.Width);
        hash.Add(spline.Closed);
        hash.Add(spline.SampleSpacing);
    }

    private void UpdateDecal(TerrainSplineComponent spline, SplineData data)
    {
        if (!spline.BakeDecal || spline.Decal == null || spline.Entity.Transform.Children.Count < 2)
        {
            RemoveDecal(data);
            return;
        }

        var hash = new HashCode();
        AddCurveHash(ref hash, spline);
        hash.Add(spline.Decal);
        hash.Add(spline.Decal.ComputeHash());
        hash.Add(spline.DecalWidthScale);
        hash.Add(spline.DecalTextureLength);
        hash.Add(spline.DecalOrder);
        var decalHash = hash.ToHashCode();
        if (data.Decal != null && decalHash == data.DecalHash)
            return;
        data.DecalHash = decalHash;

        data.Decal ??= new TerrainDecal(spline.Decal);
        data.Decal.Surface = spline.Decal;
        data.Decal.Order = spline.DecalOrder;
        decalRegistry.Add(data.Decal);
        spline.Entity.Transform.UpdateWorldMatrix();
        BuildDecal(spline, data.Decal);
    }

    /// <summary>
    /// A ribbon along the curve: U goes from the left edge (0) to the right edge (1), V along the curve.
    /// </summary>
    private static void BuildDecal(TerrainSplineComponent spline, TerrainDecal decal)
    {
        var samples = spline.Evaluate().Samples;
        if (samples.Count < 2)
        {
            decal.SetGeometry(null, null);
            return;
        }

        var length = samples[^1].Distance;
        var textureLength = Math.Max(spline.DecalTextureLength, 0.01f);
        var vertices = new TerrainDecalVertex[samples.Count * 2];
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var forward = new Vector2(sample.Tangent.X, sample.Tangent.Z);
            forward = forward.LengthSquared() > 1e-8f ? Vector2.Normalize(forward) : Vector2.UnitY;
            // Right of the direction of travel, seen from above (Y up, right-handed)
            var right = new Vector2(-forward.Y, forward.X);
            var width = sample.Width * spline.DecalWidthScale;
            var center = new Vector2(sample.Position.X, sample.Position.Z);
            // Closed curves have no ends to fade
            var fromStart = spline.Closed ? 1e6f : sample.Distance;
            var toEnd = spline.Closed ? 1e6f : length - sample.Distance;
            var frame = new Vector4(right.X, right.Y, forward.X, forward.Y);
            for (int c = 0; c < 2; c++)
            {
                vertices[i * 2 + c] = new TerrainDecalVertex
                {
                    Position = center + right * ((c - 0.5f) * width),
                    TexCoord = new Vector2(c, sample.Distance / textureLength),
                    Edge = new Vector4(c, width, fromStart, toEnd),
                    Frame = frame,
                };
            }
        }

        var indices = new int[(samples.Count - 1) * 6];
        for (int i = 0; i < samples.Count - 1; i++)
        {
            var a = i * 2;
            indices[i * 6 + 0] = a;
            indices[i * 6 + 1] = a + 2;
            indices[i * 6 + 2] = a + 1;
            indices[i * 6 + 3] = a + 1;
            indices[i * 6 + 4] = a + 2;
            indices[i * 6 + 5] = a + 3;
        }
        decal.SetGeometry(vertices, indices);
    }

    private void RemoveDecal(SplineData data)
    {
        if (data.Decal == null)
            return;
        decalRegistry.Remove(data.Decal);
        data.Decal.Dispose();
        data.Decal = null;
        data.DecalHash = 0;
    }

    private static int ComputeHash(TerrainSplineComponent spline)
    {
        var hash = new HashCode();
        AddCurveHash(ref hash, spline);
        hash.Add(spline.MeshMaterial);
        hash.Add(spline.MeshWidthScale);
        hash.Add(spline.MeshOffset);
        hash.Add(spline.ConformToTerrain);
        hash.Add(spline.TextureLength);
        hash.Add(spline.CastShadows);
        hash.Add(spline.RenderGroup);
        return hash.ToHashCode();
    }

    private void BuildMesh(TerrainSplineComponent spline, SplineData data, List<TerrainComponent> terrains)
    {
        var device = graphicsDeviceService?.GraphicsDevice;
        if (device == null)
            return;

        spline.Entity.Transform.UpdateWorldMatrix();
        var curve = spline.Evaluate();
        var samples = curve.Samples;
        if (samples.Count < 2)
        {
            RemoveMesh(spline.Entity, data);
            return;
        }

        var maxWidth = samples.Max(x => x.Width) * spline.MeshWidthScale;
        var columns = spline.ConformToTerrain ? Math.Clamp((int)MathF.Ceiling(maxWidth / 1.0f) + 1, 2, 64) : 2;
        var invWorld = Matrix.Invert(spline.Entity.Transform.WorldMatrix);
        var vertices = new Vertex[samples.Count * columns];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            var right = Vector3.Cross(Vector3.UnitY, sample.Tangent);
            right.Y = 0;
            if (right.LengthSquared() < 1e-8f)
                right = Vector3.UnitX;
            right.Normalize();
            // Cross(up, forward) points left in a right-handed system: flip so that U increases to the right
            right = -right;
            var width = sample.Width * spline.MeshWidthScale;

            for (int c = 0; c < columns; c++)
            {
                var u = c / (float)(columns - 1);
                var world = sample.Position + right * ((u - 0.5f) * width);
                var normal = Vector3.UnitY;
                if (spline.ConformToTerrain)
                {
                    var terrain = FindTerrain(terrains, world, out var height);
                    world.Y = (terrain != null ? height : sample.Position.Y) + spline.MeshOffset;
                    if (terrain != null)
                        normal = terrain.GetNormal(world);
                }
                else
                {
                    world.Y = sample.Position.Y + spline.MeshOffset;
                }

                var local = Vector3.TransformCoordinate(world, invWorld);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
                vertices[i * columns + c] = new Vertex
                {
                    Position = local,
                    Normal = Vector3.Normalize(Vector3.TransformNormal(normal, invWorld)),
                    Tangent = new Vector4(Vector3.Normalize(Vector3.TransformNormal(right, invWorld)), 1),
                    TexCoord = new Vector2(u, sample.Distance / Math.Max(spline.TextureLength, 0.01f)),
                };
            }
        }

        var indices = new int[(samples.Count - 1) * (columns - 1) * 6];
        var index = 0;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            for (int c = 0; c < columns - 1; c++)
            {
                var a = i * columns + c;
                var b = a + 1;
                var d = a + columns;
                var e = d + 1;
                // Clockwise seen from above (front faces)
                indices[index++] = a;
                indices[index++] = d;
                indices[index++] = b;
                indices[index++] = b;
                indices[index++] = d;
                indices[index++] = e;
            }
        }

        ReleaseBuffers(data);
        data.VertexBuffer = Buffer.Vertex.New(device, vertices);
        data.IndexBuffer = Buffer.Index.New(device, indices);

        var bounds = new BoundingBox(min - new Vector3(0.1f), max + new Vector3(0.1f));
        var mesh = new Mesh
        {
            Draw = new MeshDraw
            {
                PrimitiveType = PrimitiveType.TriangleList,
                DrawCount = indices.Length,
                IndexBuffer = new IndexBufferBinding(data.IndexBuffer, true, indices.Length),
                VertexBuffers = new[] { new VertexBufferBinding(data.VertexBuffer, Vertex.Layout, vertices.Length) },
            },
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds),
            MaterialIndex = 0,
        };
        var model = new Model { BoundingBox = bounds, BoundingSphere = BoundingSphere.FromBox(bounds) };
        model.Materials.Add(new MaterialInstance(spline.MeshMaterial));
        model.Meshes.Add(mesh);

        if (data.Model == null)
        {
            data.Model = new ModelComponent();
            spline.Entity.Add(data.Model);
        }
        data.Model.Model = model;
        data.Model.IsShadowCaster = spline.CastShadows;
        data.Model.RenderGroup = spline.RenderGroup;
    }

    private static TerrainComponent FindTerrain(List<TerrainComponent> terrains, Vector3 world, out float height)
    {
        foreach (var terrain in terrains)
        {
            if (terrain.Enabled && terrain.GetHeight(world) is { } h)
            {
                height = h;
                return terrain;
            }
        }
        height = 0;
        return null;
    }

    private static void RemoveMesh(Entity entity, SplineData data)
    {
        if (data.Model != null)
        {
            entity.Remove(data.Model);
            data.Model = null;
        }
        ReleaseBuffers(data);
        data.Hash = 0;
    }

    private static void ReleaseBuffers(SplineData data)
    {
        data.VertexBuffer?.Dispose();
        data.IndexBuffer?.Dispose();
        data.VertexBuffer = null;
        data.IndexBuffer = null;
    }
}

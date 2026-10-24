// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain.Decals;

/// <summary>
/// A vertex of the geometry of a terrain decal, in world XZ.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TerrainDecalVertex
{
    /// <summary>
    /// World XZ position.
    /// </summary>
    public Vector2 Position;

    /// <summary>
    /// Texture coordinates of the decal textures.
    /// </summary>
    public Vector2 TexCoord;

    /// <summary>
    /// Distances to the edges, for the edge fade: x = position across (0-1), y = width across (meters),
    /// z = distance from the start (meters), w = distance to the end (meters).
    /// </summary>
    public Vector4 Edge;

    /// <summary>
    /// World XZ directions in which the texture coordinates U (xy) and V (zw) increase, to orient the normal map.
    /// </summary>
    public Vector4 Frame;

    public static readonly VertexDeclaration Layout = new(
        VertexElement.Position<Vector2>(),
        VertexElement.TextureCoordinate<Vector2>(0),
        VertexElement.TextureCoordinate<Vector4>(1),
        VertexElement.TextureCoordinate<Vector4>(2));
}

/// <summary>
/// A decal drawn into the terrain virtual textures (see <see cref="TerrainDecalRegistry"/>): triangles in world XZ,
/// projected from above, with a <see cref="TerrainDecalSurface"/>.
/// </summary>
public sealed class TerrainDecal : IDisposable
{
    private TerrainDecalVertex[] vertices = Array.Empty<TerrainDecalVertex>();
    private int[] indices = Array.Empty<int>();
    private Buffer vertexBuffer;
    private Buffer indexBuffer;
    private bool buffersDirty;

    public TerrainDecal(TerrainDecalSurface surface)
    {
        Surface = surface;
    }

    /// <summary>
    /// What the decal draws. Call <see cref="MarkChanged"/> after changing it.
    /// </summary>
    public TerrainDecalSurface Surface { get; set; }

    /// <summary>
    /// Decals with a higher order are drawn over the others.
    /// </summary>
    public int Order { get; set; }

    public TerrainDecalShape Shape { get; set; }

    /// <summary>
    /// Minimum of the world XZ bounds of the geometry.
    /// </summary>
    public Vector2 BoundsMin { get; private set; }

    /// <summary>
    /// Maximum of the world XZ bounds of the geometry.
    /// </summary>
    public Vector2 BoundsMax { get; private set; }

    public bool HasGeometry => indices.Length > 0;

    /// <summary>
    /// The registry the decal is added to.
    /// </summary>
    public TerrainDecalRegistry Registry { get; internal set; }

    /// <summary>
    /// Position in the registry, among decals of the same order.
    /// </summary>
    internal long Sequence { get; set; }

    /// <summary>
    /// Replaces the geometry (triangle list) and renders the virtual texture again where the decal was and is now.
    /// </summary>
    public void SetGeometry(TerrainDecalVertex[] newVertices, int[] newIndices)
    {
        var oldMin = BoundsMin;
        var oldMax = BoundsMax;
        var hadGeometry = HasGeometry;

        vertices = newVertices ?? Array.Empty<TerrainDecalVertex>();
        indices = newIndices ?? Array.Empty<int>();
        buffersDirty = true;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var vertex in vertices)
        {
            min = Vector2.Min(min, vertex.Position);
            max = Vector2.Max(max, vertex.Position);
        }
        BoundsMin = HasGeometry ? min : Vector2.Zero;
        BoundsMax = HasGeometry ? max : Vector2.Zero;

        if (hadGeometry)
            Registry?.Invalidate(oldMin, oldMax);
        MarkChanged();
    }

    /// <summary>
    /// Renders the virtual texture again under the decal (e.g. the surface changed).
    /// </summary>
    public void MarkChanged()
    {
        if (HasGeometry)
            Registry?.Invalidate(BoundsMin, BoundsMax);
    }

    public void Dispose()
    {
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        vertexBuffer = null;
        indexBuffer = null;
    }

    /// <summary>
    /// Draws the geometry (the effect and pipeline state must be set).
    /// </summary>
    internal void Draw(GraphicsDevice device, CommandList commandList)
    {
        if (!HasGeometry)
            return;
        if (buffersDirty || vertexBuffer == null)
        {
            Dispose();
            vertexBuffer = Buffer.Vertex.New(device, vertices);
            vertexBuffer.Name = "TerrainDecalVertices";
            indexBuffer = Buffer.Index.New(device, indices);
            indexBuffer.Name = "TerrainDecalIndices";
            buffersDirty = false;
        }
        commandList.SetVertexBuffer(0, vertexBuffer, 0, TerrainDecalVertex.Layout.VertexStride);
        commandList.SetIndexBuffer(indexBuffer, 0, true);
        commandList.DrawIndexed(indices.Length);
    }

    /// <summary>
    /// Builds the geometry of a rectangle in world space.
    /// </summary>
    /// <param name="world">Placement of the rectangle (its local X and Z axes; the height is ignored).</param>
    /// <param name="size">Size along the local X and Z axes.</param>
    /// <param name="tiling">Repetitions of the textures over the size.</param>
    public void SetRectangle(Matrix world, Vector2 size, Vector2 tiling)
    {
        var center = new Vector2(world.TranslationVector.X, world.TranslationVector.Z);
        var axisX = new Vector2(world.M11, world.M13) * size.X;
        var axisZ = new Vector2(world.M31, world.M33) * size.Y;
        var width = axisX.Length();
        var length = axisZ.Length();
        if (width < 1e-4f || length < 1e-4f)
        {
            SetGeometry(null, null);
            return;
        }

        var frame = new Vector4(axisX.X / width, axisX.Y / width, axisZ.X / length, axisZ.Y / length);
        var corners = new TerrainDecalVertex[4];
        for (int i = 0; i < 4; i++)
        {
            var u = i is 1 or 2 ? 1.0f : 0.0f;
            var v = i >= 2 ? 1.0f : 0.0f;
            corners[i] = new TerrainDecalVertex
            {
                Position = center + axisX * (u - 0.5f) + axisZ * (v - 0.5f),
                TexCoord = new Vector2(u * tiling.X, v * tiling.Y),
                Edge = new Vector4(u, width, v * length, (1 - v) * length),
                Frame = frame,
            };
        }
        SetGeometry(corners, [0, 1, 2, 0, 2, 3]);
    }
}

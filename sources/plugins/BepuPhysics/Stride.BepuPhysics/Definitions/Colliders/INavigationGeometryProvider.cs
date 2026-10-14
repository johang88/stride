// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;

namespace Stride.BepuPhysics.Definitions.Colliders;

/// <summary>
/// Implemented by colliders able to provide their triangles for a region only (e.g. terrains),
/// so navigation meshes can be built tile by tile without collecting the whole shape.
/// </summary>
public interface INavigationGeometryProvider
{
    /// <summary>
    /// Appends the world space triangles overlapping a world space box, wound counter-clockwise seen from their front
    /// (the Recast convention: cross(v1 - v0, v2 - v0) points up for walkable ground).
    /// </summary>
    /// <param name="collidable">The collidable using this collider.</param>
    /// <param name="bounds">World space region.</param>
    /// <param name="vertices">Receives the vertices.</param>
    /// <param name="indices">Receives three indices per triangle (into <paramref name="vertices"/>, absolute).</param>
    void GetNavigationTriangles(CollidableComponent collidable, BoundingBox bounds, List<Vector3> vertices, List<int> indices);

    /// <summary>
    /// Gets the world space bounds of the collider's navigation geometry.
    /// </summary>
    BoundingBox GetNavigationBounds(CollidableComponent collidable);
}

// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

#nullable enable
using Stride.Core.Mathematics;
using System;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// C# mirror of TerrainImpostorOctahedral.sdsl. The baker uses this to place frames, the shader uses the
/// sdsl copy to look them up again - if the two ever disagree the impostor will sample the wrong
/// frame and appear to snap to a neighbouring angle, so keep them in lockstep.
/// </summary>
public static class TerrainImpostorOctahedral
{
    /// <summary>Normalised direction (Y >= 0) to [0,1]^2.</summary>
    public static Vector2 HemiOctEncode(Vector3 dir)
    {
        var d = dir / (Math.Abs(dir.X) + Math.Abs(dir.Y) + Math.Abs(dir.Z));
        return new Vector2(d.X + d.Z, d.X - d.Z) * 0.5f + new Vector2(0.5f);
    }

    /// <summary>[0,1]^2 to a normalised direction on the upper hemisphere.</summary>
    public static Vector3 HemiOctDecode(Vector2 uv)
    {
        var e = uv * 2.0f - new Vector2(1.0f);
        var t = new Vector2(e.X + e.Y, e.X - e.Y) * 0.5f;
        var d = new Vector3(t.X, 1.0f - Math.Abs(t.X) - Math.Abs(t.Y), t.Y);
        d.Normalize();
        return d;
    }

    /// <summary>
    /// Direction for frame (x, y) of a gridSize x gridSize atlas. Frames sit on the grid's corners
    /// rather than cell centres so that the four frames around any view direction bracket it
    /// exactly, which is what makes the runtime blend continuous.
    /// </summary>
    public static Vector3 GetFrameDirection(int x, int y, int gridSize)
        => HemiOctDecode(new Vector2(x / (float)(gridSize - 1), y / (float)(gridSize - 1)));

    /// <summary>Orthonormal basis for a frame captured looking from 'dir' towards the origin.</summary>
    public static void GetFrameBasis(Vector3 dir, out Vector3 right, out Vector3 up)
    {
        var refUp = Math.Abs(dir.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;

        right = Vector3.Cross(refUp, dir);
        right.Normalize();

        up = Vector3.Cross(dir, right);
    }
}

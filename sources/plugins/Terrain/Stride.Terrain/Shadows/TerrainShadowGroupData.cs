// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.InteropServices;

// The namespace of the shader declaring it (TerrainShadowGenerator), for its generated parameter key
namespace Stride.Terrain;

/// <summary>
/// Per thread group data of the terrain shadow line sweep (TerrainShadowGenerator).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TerrainShadowGroupData
{
    public int Iterations;
    public float DeltaErrorStart;
    public float Padding0;
    public float Padding1;
}

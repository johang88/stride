// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;

namespace Stride.Terrain.VirtualTexturing;

public enum TerrainVirtualTextureDensity
{
    [Display("32 texels / m")] Texels32 = 32,
    [Display("64 texels / m")] Texels64 = 64,
    [Display("128 texels / m")] Texels128 = 128,
    [Display("256 texels / m")] Texels256 = 256,
    [Display("512 texels / m")] Texels512 = 512,
}

/// <summary>
/// Settings of the runtime virtual texture caching the blended terrain layers (also sampled by objects blending with the terrain).
/// </summary>
[DataContract("TerrainVirtualTextureSettings")]
public sealed class TerrainVirtualTextureSettings
{
    [DataMember(10)]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Texel density of the most detailed mip. Should be close to the density of the layer textures (texture size / tile size).
    /// </summary>
    [DataMember(20)]
    [DefaultValue(TerrainVirtualTextureDensity.Texels128)]
    public TerrainVirtualTextureDensity Density { get; set; } = TerrainVirtualTextureDensity.Texels128;

    /// <summary>
    /// Number of physical pages along each side of the page cache (the cache holds this squared, 128x128 texels each).
    /// </summary>
    [DataMember(30)]
    [DefaultValue(24)]
    [DataMemberRange(8, 64, 1, 4, 0)]
    public int CachePagesPerSide { get; set; } = 24;

    /// <summary>
    /// Maximum number of pages rendered per frame.
    /// </summary>
    [DataMember(40)]
    [DefaultValue(12)]
    [DataMemberRange(1, 128, 1, 4, 0)]
    public int MaxPageUpdatesPerFrame { get; set; } = 12;

    /// <summary>
    /// Downscale factor of the feedback pass resolution (power of two).
    /// </summary>
    [DataMember(50)]
    [DefaultValue(8)]
    [DataMemberRange(2, 32, 1, 2, 0)]
    public int FeedbackDownscale { get; set; } = 8;

    /// <summary>
    /// Mip bias (positive values save memory and page updates at the cost of sharpness).
    /// </summary>
    [DataMember(60)]
    [DefaultValue(0.0f)]
    [DataMemberRange(-2, 4, 0.1, 0.5, 2)]
    public float MipBias { get; set; }
}

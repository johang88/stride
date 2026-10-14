// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;
using Stride.Graphics;

namespace Stride.Terrain
{
    /// <summary>
    /// The set of material layers a terrain can be painted with. Layer textures are packed into texture arrays.
    /// </summary>
    [DataContract("TerrainMaterial")]
    [ReferenceSerializer, DataSerializerGlobal(typeof(ReferenceSerializer<TerrainMaterial>), Profile = "Content")]
    [ContentSerializer(typeof(DataContentSerializer<TerrainMaterial>))]
    public sealed class TerrainMaterial
    {
        /// <summary>
        /// The layers, indexed by the layer ids stored in the terrain splat map.
        /// </summary>
        [DataMember(10)]
        public List<TerrainLayer> Layers { get; } = new();

        /// <summary>
        /// Texture array: albedo (sRGB) in RGB, height in A. One slice per layer.
        /// </summary>
        [DataMember(20)]
        public Texture AlbedoHeightArray { get; set; }

        /// <summary>
        /// Texture array: tangent-space normal XY in RG, roughness in B, ambient occlusion in A. One slice per layer.
        /// </summary>
        [DataMember(30)]
        public Texture NormalRoughnessArray { get; set; }

        /// <summary>
        /// Controls how sharp height-based transitions between layers are (in normalized height units). Smaller is sharper.
        /// </summary>
        [DataMember(40)]
        [DefaultValue(0.2f)]
        public float HeightBlendSharpness { get; set; } = 0.2f;

        /// <summary>
        /// The layer used on slopes steeper than <see cref="CliffSlopeStart"/>, projected tri-planar to avoid stretching. -1 to disable.
        /// </summary>
        [DataMember(50)]
        [DefaultValue(-1)]
        public int CliffLayer { get; set; } = -1;

        /// <summary>
        /// Slope (1 - normal.y) at which the cliff layer starts to blend in.
        /// </summary>
        [DataMember(60)]
        [DefaultValue(0.35f)]
        public float CliffSlopeStart { get; set; } = 0.35f;

        /// <summary>
        /// Slope (1 - normal.y) at which the cliff layer fully covers the terrain.
        /// </summary>
        [DataMember(70)]
        [DefaultValue(0.5f)]
        public float CliffSlopeEnd { get; set; } = 0.5f;

        /// <summary>
        /// Depth of the parallax occlusion mapping in meters (0 disables it). Uses the blended layer heights (virtual texture path).
        /// </summary>
        [DataMember(80)]
        [DefaultValue(0.05f)]
        public float ParallaxDepth { get; set; } = 0.05f;

        /// <summary>
        /// Distance from the camera over which the parallax effect fades out, in meters.
        /// </summary>
        [DataMember(90)]
        [DefaultValue(40.0f)]
        public float ParallaxFadeDistance { get; set; } = 40.0f;
    }

    /// <summary>
    /// Runtime parameters of a terrain material layer.
    /// </summary>
    [DataContract("TerrainLayer")]
    public sealed class TerrainLayer
    {
        [DataMember(10)]
        public string Name { get; set; }

        /// <summary>
        /// Slice of this layer in the texture arrays.
        /// </summary>
        [DataMember(20)]
        public int TextureIndex { get; set; }

        /// <summary>
        /// World size in meters covered by one repetition of the layer textures.
        /// </summary>
        [DataMember(30)]
        [DefaultValue(4.0f)]
        public float TileSize { get; set; } = 4.0f;

        /// <summary>
        /// Multiplies the albedo.
        /// </summary>
        [DataMember(40)]
        public Color3 Tint { get; set; } = new Color3(1.0f);

        /// <summary>
        /// Multiplies the roughness.
        /// </summary>
        [DataMember(50)]
        [DefaultValue(1.0f)]
        public float RoughnessScale { get; set; } = 1.0f;

        [DataMember(60)]
        [DefaultValue(1.0f)]
        public float NormalStrength { get; set; } = 1.0f;

        /// <summary>
        /// Scales the height map contribution to height blending (0 = linear blending).
        /// </summary>
        [DataMember(70)]
        [DefaultValue(1.0f)]
        public float HeightBlendScale { get; set; } = 1.0f;

        /// <summary>
        /// Biases the layer height in height blending, so it tends to cover (positive) or fill gaps (negative).
        /// </summary>
        [DataMember(80)]
        [DefaultValue(0.0f)]
        public float HeightBlendOffset { get; set; }

        /// <summary>
        /// Amount of the second, rotated and scaled sample mixed in to hide the texture tiling (0-1).
        /// </summary>
        [DataMember(85)]
        [DefaultValue(0.5f)]
        public float TilingVariation { get; set; } = 0.5f;

        /// <summary>
        /// A free-form tag (e.g. footstep sound or VFX surface type) available to gameplay through terrain queries.
        /// </summary>
        [DataMember(90)]
        public string SurfaceTag { get; set; }
    }
}

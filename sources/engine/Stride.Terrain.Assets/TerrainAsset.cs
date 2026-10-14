// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Terrain;

namespace Stride.Assets.Terrain
{
    /// <summary>
    /// A terrain height field with its splat map. The samples are stored in a binary source file (<see cref="TerrainDataFile"/>)
    /// edited with the terrain tools of the scene editor.
    /// </summary>
    [DataContract("TerrainAsset")]
    [AssetDescription(FileExtension)]
    [AssetContentType(typeof(TerrainData))]
    [AssetFormatVersion(StrideConfig.LogicalPackageName, CurrentVersion, "1.0.0.0")]
    [Display(1960, "Terrain")]
    public sealed class TerrainAsset : Asset
    {
        private const string CurrentVersion = "1.0.0.0";

        public const string FileExtension = ".sdterrain";

        /// <summary>
        /// The file storing the samples (relative to the asset). Created by the terrain editor when the terrain is first saved.
        /// </summary>
        [DataMember(5)]
        public UFile DataFile { get; set; }

        /// <summary>
        /// The material layers the terrain is painted with.
        /// </summary>
        [DataMember(10)]
        public TerrainMaterial Material { get; set; }

        /// <summary>
        /// Number of samples along each side. The terrain is (resolution - 1) * cell size meters wide.
        /// </summary>
        [DataMember(20)]
        [DefaultValue(TerrainResolution.Resolution4097)]
        public TerrainResolution Resolution { get; set; } = TerrainResolution.Resolution4097;

        /// <summary>
        /// Distance in meters between two samples.
        /// </summary>
        [DataMember(30)]
        [DefaultValue(0.5f)]
        [DataMemberRange(0.05, 64, 0.05, 0.5, 2)]
        public float CellSize { get; set; } = 0.5f;

        /// <summary>
        /// Lowest representable height (heights are stored with 16 bits over [min, max]).
        /// </summary>
        [DataMember(40)]
        [DefaultValue(-100.0f)]
        public float MinHeight { get; set; } = -100.0f;

        /// <summary>
        /// Highest representable height.
        /// </summary>
        [DataMember(50)]
        [DefaultValue(400.0f)]
        public float MaxHeight { get; set; } = 400.0f;

        /// <summary>
        /// Height of a new (flat) terrain, used when the source file doesn't exist yet.
        /// </summary>
        [DataMember(60)]
        [DefaultValue(0.0f)]
        public float InitialHeight { get; set; }

        /// <summary>
        /// Gets the terrain width in meters.
        /// </summary>
        [DataMemberIgnore]
        public float Size => ((int)Resolution - 1) * CellSize;

        /// <summary>
        /// Loads the source samples, adapted to the current settings (or a flat terrain when the source doesn't exist).
        /// </summary>
        public TerrainData LoadData(UFile absoluteSourcePath)
        {
            var resolution = (int)Resolution;
            var maxHeight = MaxHeight > MinHeight ? MaxHeight : MinHeight + 1;
            if (!UPath.IsNullOrEmpty(absoluteSourcePath) && System.IO.File.Exists(absoluteSourcePath))
            {
                var source = TerrainDataFile.Read(absoluteSourcePath);
                if (source.Resolution == resolution && source.CellSize == CellSize && source.MinHeight == MinHeight && source.MaxHeight == maxHeight)
                    return source;
                return TerrainDataFile.Resample(source, resolution, CellSize, MinHeight, maxHeight);
            }

            var data = new TerrainData(resolution, CellSize, MinHeight, maxHeight);
            var initial = data.Quantize(InitialHeight);
            System.Array.Fill(data.Heights, initial);
            return data;
        }
    }

    public enum TerrainResolution
    {
        [Display("257 (256 cells)")] Resolution257 = 257,
        [Display("513 (512 cells)")] Resolution513 = 513,
        [Display("1025 (1024 cells)")] Resolution1025 = 1025,
        [Display("2049 (2048 cells)")] Resolution2049 = 2049,
        [Display("4097 (4096 cells)")] Resolution4097 = 4097,
        [Display("8193 (8192 cells)")] Resolution8193 = 8193,
    }
}

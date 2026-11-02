// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Text.RegularExpressions;
using Stride.Core;
using Stride.Core.BuildEngine;
using Stride.Rendering;

namespace Stride.Assets.Models
{
    /// <summary>
    /// Keeps a range of the levels of detail of a source file's LOD group: the meshes under nodes named <c>*_LOD&lt;n&gt;</c>
    /// (as Unreal and most DCC tools export them) outside the range are dropped. Meshes outside any LOD node are kept.
    /// </summary>
    /// <remarks>
    /// Applied to the source hierarchy, before meshes are merged into the root (which loses the node names).
    /// </remarks>
    [DataContract("LevelOfDetailModifier")]
    [Display("Levels of detail")]
    public sealed partial class LevelOfDetailModifier : ISourceModelModifier
    {
        /// <summary>
        /// The first level kept.
        /// </summary>
        [DataMember(10)]
        [DefaultValue(0)]
        public int First { get; set; }

        /// <summary>
        /// The last level kept (inclusive), or -1 for all the levels from <see cref="First"/>.
        /// </summary>
        [DataMember(20)]
        [DefaultValue(-1)]
        public int Last { get; set; } = -1;

        /// <inheritdoc/>
        // (the range is part of the version: the command hashes modifiers by version)
        public int Version => 1 + 1000 * (First + 1) + 1000000 * (Last + 1);

        /// <inheritdoc/>
        public void ApplyToSource(ICommandContext commandContext, Model model, Skeleton sourceNodes)
        {
            if (sourceNodes == null)
                return;
            var removed = model.Meshes.RemoveAll(mesh =>
            {
                var level = LevelOf(sourceNodes, mesh.NodeIndex);
                return level >= 0 && (level < First || (Last >= 0 && level > Last));
            });
            commandContext.Logger.Verbose($"Levels of detail {First}..{(Last < 0 ? "last" : Last)}: {removed} meshes dropped, {model.Meshes.Count} kept.");
        }

        /// <inheritdoc/>
        public void Apply(ICommandContext commandContext, Model model)
        {
        }

        /// <summary>
        /// The level of detail of a node: the first <c>_LOD&lt;n&gt;</c> suffix up its parents, or -1.
        /// </summary>
        private static int LevelOf(Skeleton nodes, int index)
        {
            while (index >= 0 && index < nodes.Nodes.Length)
            {
                var match = LodSuffix().Match(nodes.Nodes[index].Name ?? string.Empty);
                if (match.Success)
                    return int.Parse(match.Groups[1].Value);
                index = nodes.Nodes[index].ParentIndex;
            }
            return -1;
        }

        [GeneratedRegex(@"_LOD(\d+)$", RegexOptions.IgnoreCase)]
        private static partial Regex LodSuffix();
    }
}

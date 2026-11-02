// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.BuildEngine;
using Stride.Rendering;

namespace Stride.Assets.Models
{
    /// <summary>
    /// A <see cref="IModelModifier"/> that also sees the model as imported, with the node hierarchy of the source file
    /// (before meshes are moved to the asset's skeleton or merged).
    /// </summary>
    public interface ISourceModelModifier : IModelModifier
    {
        /// <summary>
        /// Modifies the imported model, whose meshes still reference the nodes of <paramref name="sourceNodes"/>.
        /// </summary>
        void ApplyToSource(ICommandContext commandContext, Model model, Skeleton sourceNodes);
    }
}

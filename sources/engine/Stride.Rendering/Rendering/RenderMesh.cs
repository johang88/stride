// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Rendering.Materials;

namespace Stride.Rendering
{
    /// <summary>
    /// Used by <see cref="TransformRenderFeature"/> to apply transform data without
    /// depending on the entire RenderMesh, this allows <see cref="TransformRenderFeature"/> to be
    /// used outside of <see cref="MeshRenderFeature"/> 
    /// </summary>
    public abstract class TransformedRenderObject : RenderObject
    {
        public Matrix World = Matrix.Identity;
    }

    /// <summary>
    /// Used by <see cref="MeshRenderFeature"/> to render a <see cref="Rendering.Mesh"/>.
    /// </summary>
    public class RenderMesh : TransformedRenderObject
    {
        public MeshDraw ActiveMeshDraw;

        public RenderModel RenderModel;

        /// <summary>
        /// Underlying mesh, can be accessed only during <see cref="RenderFeature.Extract"/> phase.
        /// </summary>
        public Mesh Mesh;

        // Material
        // TODO: Extract with MaterialRenderFeature
        public MaterialPass MaterialPass;

        // TODO GRAPHICS REFACTOR store that in RenderData (StaticObjectNode?)
        internal MaterialRenderFeature.MaterialInfo MaterialInfo;

        public bool IsShadowCaster;

        public bool IsScalingNegative;

        public bool IsPreviousScalingNegative;

        public Matrix[] BlendMatrices;

        public int InstanceCount;
    }
}

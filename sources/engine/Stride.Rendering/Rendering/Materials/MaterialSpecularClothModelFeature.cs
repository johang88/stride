// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using System.Globalization;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Shaders;

namespace Stride.Rendering.Materials
{
    /// <summary>
    /// Microfacet specular plus a sheen lobe for fabrics (Charlie distribution): the soft bright rim cloth gets at grazing angles.
    /// The sheen applies per pixel where the surface is a rough dielectric, so textures mixing cloth with metal or leather work.
    /// </summary>
    [DataContract("MaterialSpecularClothModelFeature")]
    [Display("Cloth (microfacet + sheen)")]
    public class MaterialSpecularClothModelFeature : MaterialSpecularMicrofacetModelFeature
    {
        /// <summary>
        /// Strength of the sheen (its colour is the square root of the albedo times this).
        /// </summary>
        /// <userdoc>How much light the fibres catch at grazing angles.</userdoc>
        [DataMember(50)]
        [DefaultValue(1.0f)]
        [DataMemberRange(0, 2, 0.01, 0.1, 2)]
        public float Sheen { get; set; } = 1.0f;

        /// <summary>
        /// Roughness from which a pixel counts as cloth (fully from 0.25 above).
        /// </summary>
        /// <userdoc>Smoother surfaces than this (polished leather, metal) get no sheen.</userdoc>
        [DataMember(60)]
        [DefaultValue(0.45f)]
        [DataMemberRange(0, 1, 0.01, 0.1, 2)]
        public float RoughnessStart { get; set; } = 0.45f;

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
            var shaderSource = new ShaderMixinSource();
            shaderSource.Mixins.Add(new ShaderClassSource("MaterialSurfaceShadingSpecularCloth", F(MathUtil.Clamp(Sheen, 0.0f, 4.0f)), F(MathUtil.Clamp(RoughnessStart, 0.0f, 1.0f))));
            GenerateShaderCompositions(context, shaderSource);
            context.AddShading(this).LightDependentSurface = shaderSource;
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj) && obj is MaterialSpecularClothModelFeature other && other.Sheen == Sheen && other.RoughnessStart == RoughnessStart;
        }

        public override int GetHashCode() => base.GetHashCode();
    }
}

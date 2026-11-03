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
    /// Skin shading without the screen space subsurface scattering pass: wrapped diffuse per color channel (light reaching past
    /// the shadow terminator, red the furthest) and a red glow through thin parts lit from behind. Cheap, no extra render targets.
    /// </summary>
    [DataContract("MaterialSkinScatteringFeature")]
    [Display("Skin scattering")]
    public sealed class MaterialSkinScatteringFeature : MaterialFeature, IMaterialSubsurfaceScatteringFeature
    {
        /// <summary>
        /// How far the light wraps around past the terminator, per channel (0: Lambert).
        /// </summary>
        /// <userdoc>How far light scattered under the surface reaches into the unlit side, per color channel (red the furthest for skin).</userdoc>
        [DataMember(10)]
        public Color3 Wrap { get; set; } = new Color3(0.2f, 0.1f, 0.07f);

        /// <summary>
        /// Strength of the glow through thin parts lit from behind.
        /// </summary>
        [DataMember(20)]
        [DefaultValue(0.1f)]
        [DataMemberRange(0, 2, 0.01, 0.1, 2)]
        public float Translucency { get; set; } = 0.1f;

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            static string F(float value) => MathUtil.Clamp(value, 0.0f, 4.0f).ToString("R", CultureInfo.InvariantCulture);
            var shading = new ShaderClassSource("MaterialSkinScatteringShading", F(Wrap.R), F(Wrap.G), F(Wrap.B), F(Translucency));
            var shaderBuilder = context.AddShading(this);
            shaderBuilder.LightDependentSurface = shading;
        }

        public bool Equals(IMaterialShadingModelFeature other)
        {
            return other is MaterialSkinScatteringFeature feature && feature.Wrap == Wrap && feature.Translucency == Translucency;
        }

        public override bool Equals(object obj) => obj is IMaterialShadingModelFeature feature && Equals(feature);

        public override int GetHashCode() => typeof(MaterialSkinScatteringFeature).GetHashCode();
    }
}

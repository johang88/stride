// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.ComponentModel;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Stride.Water.Materials;

/// <summary>
/// Water seen through its surface: the scene behind it refracted by the surface's normal, dimmed and tinted by the water on
/// the way (extinction over the actual path through the water), light scattered in the water where the scene's doesn't get
/// through (the material's diffuse color is the water's color, lit like a surface), reflections over it by Fresnel (the
/// material's specular model, e.g. metalness 0 for water's 2% reflectance), and a soft edge at the shoreline.
/// </summary>
/// <remarks>
/// The graphics compositor's forward renderer must bind the scene depth and color during the transparent stage
/// (Bind Depth/Opaque As Resource During Transparent Rendering). No vertex displacement: any flat or gently curved mesh works.
/// </remarks>
[DataContract("MaterialTransparencyWaterFeature")]
[Display("Water")]
public sealed class MaterialTransparencyWaterFeature : MaterialFeature, IMaterialTransparencyFeature
{
    private static readonly PropertyKey<bool> HasFinalCallback = new("MaterialTransparencyWaterFeature.HasFinalCallback", typeof(MaterialTransparencyWaterFeature));

    /// <userdoc>The color light keeps after travelling Transmittance Distance through the water (clear water: almost white; peaty bog water: brown).</userdoc>
    [DataMember(10)]
    [InlineProperty]
    public Color3 TransmittanceColor { get; set; } = new(0.55f, 0.75f, 0.7f);

    /// <userdoc>Distance (meters) through the water after which light keeps the transmittance color. Clarity: smaller is murkier.</userdoc>
    [DataMember(20)]
    [DefaultValue(1.5f)]
    [DataMemberRange(0.01, 1000, 0.05, 0.5, 2)]
    public float TransmittanceDistance { get; set; } = 1.5f;

    /// <userdoc>How far (meters) the ripples displace the scene seen through the water.</userdoc>
    [DataMember(30)]
    [DefaultValue(0.08f)]
    [DataMemberRange(0, 2, 0.005, 0.05, 3)]
    public float Refraction { get; set; } = 0.08f;

    /// <userdoc>Depth (meters) at which the refraction reaches its full displacement (less at the shoreline, where the floor is close).</userdoc>
    [DataMember(40)]
    [DefaultValue(0.4f)]
    [DataMemberRange(0.01, 100, 0.01, 0.1, 2)]
    public float RefractionDepth { get; set; } = 0.4f;

    /// <userdoc>Depth (meters) over which the water fades in from the shoreline, reflections included.</userdoc>
    [DataMember(50)]
    [DefaultValue(0.06f)]
    [DataMemberRange(0.001, 10, 0.005, 0.05, 3)]
    public float EdgeFade { get; set; } = 0.06f;

    public override void GenerateShader(MaterialGeneratorContext context)
    {
        if (context.MaterialPass.BlendState == null)
            context.MaterialPass.BlendState = BlendStates.AlphaBlend;
        context.MaterialPass.HasTransparency = true;
        context.MaterialPass.AlphaToCoverage = false;

        var distance = Math.Max(TransmittanceDistance, 1e-3f);
        var extinction = new Vector3(Extinction(TransmittanceColor.R), Extinction(TransmittanceColor.G), Extinction(TransmittanceColor.B)) / distance;
        context.Parameters.Set(MaterialWaterVolumeKeys.WaterExtinction, extinction);
        context.Parameters.Set(MaterialWaterVolumeKeys.WaterEdgeFade, 1.0f / Math.Max(EdgeFade, 1e-4f));
        context.Parameters.Set(MaterialWaterVolumeKeys.WaterRefraction, Refraction);
        context.Parameters.Set(MaterialWaterVolumeKeys.WaterRefractionDepth, 1.0f / Math.Max(RefractionDepth, 1e-3f));

        // After the surface attributes (normal, diffuse), before lighting
        context.AddShaderSource(MaterialShaderStage.Pixel, new ShaderClassSource("MaterialWaterVolume"));
        if (!context.Tags.Get(HasFinalCallback))
        {
            context.Tags.Set(HasFinalCallback, true);
            context.AddFinalCallback(MaterialShaderStage.Pixel, (_, c) => c.AddShaderSource(MaterialShaderStage.Pixel, new ShaderClassSource("MaterialWaterComposite")),
                MaterialTransparencyBlendFeature.ShadingColorAlphaFinalCallbackOrder);
        }
    }

    private static float Extinction(float transmittance) => -MathF.Log(Math.Clamp(transmittance, 1e-4f, 1.0f));
}

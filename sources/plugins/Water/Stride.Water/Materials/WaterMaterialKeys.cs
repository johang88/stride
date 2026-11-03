// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Globalization;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace Stride.Water.Materials;

/// <summary>
/// Base keys of the water material nodes' parameters; each node instance gets its own indexed copy
/// (<see cref="ShaderGeneratorContext.GetParameterKey"/>), so a material can use several.
/// </summary>
public static class WaterMaterialKeys
{
    public static readonly ObjectParameterKey<Texture> NormalMap = ParameterKeys.NewObject<Texture>();
    public static readonly ValueParameterKey<Vector4> NormalLayer0 = ParameterKeys.NewValue<Vector4>();
    public static readonly ValueParameterKey<Vector4> NormalLayer1 = ParameterKeys.NewValue<Vector4>();
    public static readonly ValueParameterKey<Vector4> NormalLayer2 = ParameterKeys.NewValue<Vector4>();
    public static readonly ValueParameterKey<Vector4> NormalGusts = ParameterKeys.NewValue<Vector4>();
    public static readonly ValueParameterKey<Vector4> NormalSettings = ParameterKeys.NewValue<Vector4>();
    public static readonly ValueParameterKey<Vector4> DepthFade = ParameterKeys.NewValue<Vector4>();

    internal static ValueParameterKey<Vector4> Set(ShaderGeneratorContext context, ValueParameterKey<Vector4> baseKey, Vector4 value)
    {
        var key = (ValueParameterKey<Vector4>)context.GetParameterKey(baseKey);
        context.Parameters.Set(key, value);
        return key;
    }

    internal static string Format(float value) => value.ToString("0.0#####", CultureInfo.InvariantCulture);
}

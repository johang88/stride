﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Stride Shader Mixin Code Generator.
// To generate it yourself, please install Stride.VisualStudio.Package .vsix
// and re-save the associated .xkfx.
// </auto-generated>

using System;
using Stride.Core;
using Stride.Rendering;
using Stride.Graphics;
using Stride.Shaders;
using Stride.Core.Mathematics;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.DebugRendering
{
    public static partial class PrimitiveShaderKeys
    {
        public static readonly ValueParameterKey<Matrix> ViewProjection = ParameterKeys.NewValue<Matrix>();
        public static readonly ObjectParameterKey<Buffer> Transforms = ParameterKeys.NewObject<Buffer>();
        public static readonly ObjectParameterKey<Buffer> Colors = ParameterKeys.NewObject<Buffer>();
        public static readonly ValueParameterKey<int> InstanceOffset = ParameterKeys.NewValue<int>();
        public static readonly ValueParameterKey<float> LineWidthMultiplier = ParameterKeys.NewValue<float>(1.0f);
    }
}

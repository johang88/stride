// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Stride.Graphics;

/// <summary>
///   Defines a set of built-in <see cref="DepthStencilStateDescription"/>s for common Depth and Stencil testing configurations.
/// </summary>
public static class DepthStencilStates
{
    /// <summary>
    ///   A built-in Depth-Stencil State object with default settings.
    /// </summary>
    /// <inheritdoc cref="DepthStencilStateDescription.Default" path="/remarks"/>
    public static readonly DepthStencilStateDescription Default = DepthStencilStateDescription.Default;

    /// <summary>
    ///   A built-in Depth-Stencil State object with default settings using <see cref="CompareFunction.LessEqual"/>
    ///   function when comparing depth values.
    /// </summary>
    /// <remarks>
    ///   This is the state to use for a view rendered with a conventional (non-reversed) projection, where the
    ///   near plane maps to a depth of 0. Shadow map casters are rendered this way.
    /// </remarks>
    public static readonly DepthStencilStateDescription DefaultInverse = new(depthEnable: true, depthWriteEnable: true)
    {
        DepthBufferFunction = CompareFunction.LessEqual
    };

    /// <summary>
    ///   A built-in Depth-Stencil State object with settings for enabling a read-only Depth-Stencil Buffer.
    /// </summary>
    public static readonly DepthStencilStateDescription DepthRead = new(depthEnable: true, depthWriteEnable: false);

    /// <summary>
    ///   A built-in Depth-Stencil State object with settings for not using a Depth-Stencil Buffer.
    /// </summary>
    public static readonly DepthStencilStateDescription None = new(depthEnable: false, depthWriteEnable: false);
}

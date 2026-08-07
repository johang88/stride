// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Mathematics;

namespace Stride.Rendering
{
    /// <summary>
    /// Keys used by transformations.
    /// </summary>
    public static partial class CameraKeys
    {
        /// <summary>
        /// Camera focus distance
        /// </summary>
        public static readonly ValueParameterKey<float> FocusDistance = ParameterKeys.NewValue(0.0f);

        /// <summary>
        /// Computes the retro-projection factors that turn a non-linear depth-buffer value back into a view space
        /// depth, for a view rendered with a conventional projection (near plane at depth 0, far plane at depth 1).
        /// </summary>
        /// <remarks>
        /// Camera views are rendered with a reversed depth range, so prefer the
        /// <see cref="ZProjectionACalculate(ref readonly Matrix)"/> overload wherever the projection matrix is at hand:
        /// it reads the convention off the matrix and therefore stays correct for shadow map views too, which keep
        /// the conventional range.
        /// </remarks>
        public static Vector2 ZProjectionACalculate(float nearClipPlane, float farClipPlane)
        {
            // Formuluas to retro project a non-linear zdepth in the range [0.0 - 1.0] to a linear-depth in view space.

            // (0) ZProjection: A + B / z = depth in the range [0.0-1.0]
            // (1) A + B / NearClipPlane = 0.0
            // (2) A + B / FarClipPlane = 1.0
            //
            // From (1): A = -B / NearClipPlane
            // From (2) and (1): B = (-FarClipPlane * NearClipPlane) / (FarClipPlane - NearClipPlane)
            //
            // From (0) z = B / (depth - A)
            return new Vector2(farClipPlane / (farClipPlane - nearClipPlane), (-farClipPlane * nearClipPlane) / (farClipPlane - nearClipPlane));
        }

        /// <summary>
        /// Computes the retro-projection factors that turn a non-linear depth-buffer value back into a view space
        /// depth, reading the depth convention straight off the projection matrix.
        /// </summary>
        /// <param name="projection">The projection matrix the depth buffer was rendered with.</param>
        /// <returns>
        /// For a perspective projection, the pair (A, B) of <c>ZInViewSpace = B / (depth - A)</c>.
        /// For an orthographic one, the pair (A, B) of the linear <c>ZInViewSpace = A + depth * B</c>.
        /// </returns>
        /// <remarks>
        /// Both factors are already encoded in the matrix, so this stays correct whichever depth range the view was
        /// rendered with: reversed for camera views, conventional for shadow map views. Prefer it over
        /// <see cref="ZProjectionACalculate(float, float)"/>, which can only speak for the conventional range.
        /// </remarks>
        public static Vector2 ZProjectionACalculate(ref readonly Matrix projection)
        {
            var isOrthographic = projection.M44 == 1.0f;

            return isOrthographic
                // depth = z * M33 + M43, and view space depth is reported as a positive distance, so -z
                ? new Vector2(projection.M43 / projection.M33, -1.0f / projection.M33)
                // depth = -M33 + M43 / z
                : new Vector2(-projection.M33, projection.M43);
        }
    }
}

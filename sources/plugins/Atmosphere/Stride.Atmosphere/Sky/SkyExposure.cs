// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Atmosphere.Clouds;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Atmosphere.Sky;

/// <summary>
/// Measures the light on the ground at the camera on the GPU (the sky and the key light through the clouds, see
/// SkyExposureMeter) and reads it back a few frames later without waiting.
/// </summary>
internal sealed class SkyExposure : IDisposable
{
    private const int ReadbackLatency = 3;

    private readonly Buffer[] staging = new Buffer[ReadbackLatency];
    private readonly bool[] pending = new bool[ReadbackLatency];
    private ComputeEffectShader meter;
    private Buffer output;
    private int writeIndex;

    /// <summary>
    /// Luminance of the illuminance from the sky, then from the key light, on a horizontal surface at the camera (the last
    /// measurement read back; NaN before the first).
    /// </summary>
    public Vector2 Illuminance { get; private set; } = new(float.NaN);

    public void Initialize(RenderContext context)
    {
        var device = context.GraphicsDevice;
        meter = new ComputeEffectShader(context) { ShaderSourceName = "SkyExposureMeter", ThreadNumbers = new Int3(1, 1, 1), ThreadGroupCounts = new Int3(1, 1, 1) };
        output = Buffer.New(device, 16, 0, BufferFlags.ShaderResource | BufferFlags.UnorderedAccess, PixelFormat.R32G32B32A32_Float);
        output.Name = "AtmosphereExposureMeter";
    }

    /// <summary>
    /// Reads the oldest pending measurement, then measures this frame.
    /// </summary>
    /// <param name="harmonics">The captured sky's harmonics and averages.</param>
    /// <param name="cloudShadows">The cloud shadows of the frame (no map: no clouds in the way).</param>
    /// <param name="lightDirection">Direction towards the key light.</param>
    /// <param name="lightLuminance">Luminance of the key light's illuminance.</param>
    public void Measure(RenderDrawContext context, Buffer harmonics, CloudShadows cloudShadows, Vector3 eye, Vector3 lightDirection, float lightLuminance)
    {
        var commandList = context.CommandList;
        Read(commandList);

        var parameters = meter.Parameters;
        parameters.Set(SkyExposureMeterKeys.SkyExposureHarmonics, harmonics);
        var shadows = cloudShadows.Texture;
        parameters.Set(SkyExposureMeterKeys.SkyExposureCloudShadow, shadows ?? context.GraphicsDevice.GetSharedWhiteTexture());
        parameters.Set(SkyExposureMeterKeys.SkyExposureShadowRegion, shadows != null ? new Vector4(cloudShadows.Region.X, cloudShadows.Region.Y, cloudShadows.Region.Z, 1) : Vector4.Zero);
        parameters.Set(SkyExposureMeterKeys.SkyExposureLight, new Vector4(lightDirection, lightLuminance));
        parameters.Set(SkyExposureMeterKeys.SkyExposureEye, eye);
        parameters.Set(SkyExposureMeterKeys.SkyExposureSeaLevel, cloudShadows.LightDirection.W);
        parameters.Set(SkyExposureMeterKeys.SkyExposureOutput, output);
        meter.Draw(context, "SkyExposureMeter");

        ref var target = ref staging[writeIndex];
        target ??= output.ToStaging();
        commandList.Copy(output, target);
        pending[writeIndex] = true;
        writeIndex = (writeIndex + 1) % ReadbackLatency;
    }

    private unsafe void Read(CommandList commandList)
    {
        // The oldest copy (made ReadbackLatency - 1 frames ago) is the slot after the last written one
        var index = writeIndex;
        if (!pending[index] || staging[index] == null)
            return;
        var mapped = commandList.MapSubResource(staging[index], 0, MapMode.Read, doNotWait: true);
        if (mapped.DataBox.IsEmpty)
            return;
        try
        {
            var value = *(Vector4*)mapped.DataBox.DataPointer;
            Illuminance = new Vector2(value.X, value.Y);
        }
        finally
        {
            commandList.UnmapSubResource(mapped);
        }
        pending[index] = false;
    }

    public void Dispose()
    {
        meter?.Dispose();
        output?.Dispose();
        for (int i = 0; i < staging.Length; i++)
        {
            staging[i]?.Dispose();
            staging[i] = null;
        }
    }
}

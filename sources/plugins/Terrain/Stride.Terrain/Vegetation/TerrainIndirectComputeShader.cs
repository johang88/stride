// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.ComputeEffect;
using Stride.Shaders;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Terrain.Vegetation;

/// <summary>
/// A compute shader dispatched with thread group counts read from a GPU buffer (<see cref="ComputeEffectShader"/> takes them
/// from the CPU).
/// </summary>
internal sealed class TerrainIndirectComputeShader : DrawEffect
{
    private readonly MutablePipelineState pipelineState;
    private EffectBytecode previousBytecode;

    public TerrainIndirectComputeShader(RenderContext context)
        : base(context, null)
    {
        pipelineState = new MutablePipelineState(context.GraphicsDevice);
        EffectInstance = new DynamicEffectInstance("ComputeEffectShader", Parameters);
        EffectInstance.Initialize(context.Services);
        // Usually executed serially and blocking, like ComputeEffectShader
        EffectInstance.EffectCompilerParameters.TaskPriority = -1;
    }

    public DynamicEffectInstance EffectInstance { get; }

    /// <summary>
    /// Argument buffer holding the thread group counts (x, y, z) at offset 0.
    /// </summary>
    public Buffer IndirectBuffer { get; set; }

    public Int3 ThreadNumbers { get; set; } = new(1);

    public string ShaderSourceName { get; set; }

    protected override void DrawCore(RenderDrawContext context)
    {
        if (string.IsNullOrEmpty(ShaderSourceName) || IndirectBuffer == null)
            return;

        Parameters.Set(ComputeEffectShaderKeys.ThreadNumbers, ThreadNumbers);
        Parameters.Set(ComputeEffectShaderKeys.ComputeShaderName, ShaderSourceName);

        if (EffectInstance.UpdateEffect(GraphicsDevice) || previousBytecode != EffectInstance.Effect.Bytecode)
        {
            previousBytecode = EffectInstance.Effect.Bytecode;
            pipelineState.State.SetDefaults();
            pipelineState.State.RootSignature = EffectInstance.RootSignature;
            pipelineState.State.EffectBytecode = EffectInstance.Effect.Bytecode;
            pipelineState.Update();
        }

        context.CommandList.SetPipelineState(pipelineState.CurrentState);
        EffectInstance.Apply(context.GraphicsContext);
        context.CommandList.Dispatch(IndirectBuffer, 0);
    }
}

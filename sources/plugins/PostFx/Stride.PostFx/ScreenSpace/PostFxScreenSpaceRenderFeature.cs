// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Core;
using Stride.Core.Collections;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Core.Threading;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Shaders;

namespace Stride.PostFx.ScreenSpace;

/// <summary>
/// The screen-space lighting terms of a view for its opaque surfaces.
/// </summary>
internal sealed class ScreenSpaceViewData
{
    public Texture AmbientOcclusion;
    public Texture ContactShadows;
    public Texture Reflections;
    public Texture Indirect;
    public Vector4 ScreenSize;
    public Matrix PreviousViewProjection;
    public float AmbientOcclusionStrength;
    public float ContactShadowStrength;
    public float ReflectionStrength;
    public float IndirectStrength;
    // The light the contact shadows were traced towards
    public RenderLight ContactLight;
    public int Frame = -1;
}

/// <summary>
/// The screen-space lighting of the views, from the PostFx renderer to the opaque surfaces and the directional shadows.
/// </summary>
internal sealed class PostFxScreenSpace
{
    public readonly Dictionary<RenderView, ScreenSpaceViewData> Views = new();

    /// <summary>
    /// The render stage of the opaque surfaces.
    /// </summary>
    public RenderStage OpaqueStage;

    /// <summary>
    /// Whether the opaque surfaces mix in the terms.
    /// </summary>
    public bool Enabled;
}

/// <summary>
/// Sub render feature of the <see cref="MeshRenderFeature"/> giving the opaque surfaces the screen-space lighting terms of
/// their view (PostFxScreenSpaceLighting, mixed into the forward shading). Added by the PostFx renderer.
/// </summary>
[DataContract("PostFxScreenSpaceRenderFeature")]
public sealed class PostFxScreenSpaceRenderFeature : SubRenderFeature
{
    private static readonly ShaderClassSource Shader = new("PostFxScreenSpaceLighting");

    private readonly Dictionary<ObjectId, ParameterCollection> layoutParameters = new();
    private LogicalGroupReference viewKey;

    internal PostFxScreenSpace ScreenSpace { get; set; }

    // A 1x1 texture holding nothing found (black, no confidence)
    private static Texture GetBlack(GraphicsDevice device)
    {
        return device.GetOrCreateSharedData("PostFxScreenSpaceBlack", static device =>
        {
            var texture = Texture.New2D(device, 1, 1, PixelFormat.R8G8B8A8_UNorm, new byte[] { 0, 0, 0, 0 });
            texture.Name = "PostFxScreenSpaceBlack";
            return texture;
        });
    }

    protected override void InitializeCore()
    {
        base.InitializeCore();
        viewKey = ((RootEffectRenderFeature)RootRenderFeature).CreateViewLogicalGroup("PostFxScreenSpace");
    }

    public override void PrepareEffectPermutations(RenderDrawContext context)
    {
        base.PrepareEffectPermutations(context);
        var stage = ScreenSpace?.OpaqueStage;
        if (stage == null || !ScreenSpace.Enabled)
            return;

        var rootFeature = (RootEffectRenderFeature)RootRenderFeature;
        var renderEffects = RootRenderFeature.RenderData.GetData(rootFeature.RenderEffectKey);
        var effectSlotCount = rootFeature.EffectPermutationSlotCount;
        var effectSlot = rootFeature.GetEffectPermutationSlot(stage);
        Dispatcher.ForEach(RootRenderFeature.ObjectNodeReferences, objectNodeReference =>
        {
            var renderObject = RootRenderFeature.GetObjectNode(objectNodeReference).RenderObject;
            if (stage.Index >= renderObject.ActiveRenderStages.Length || !renderObject.ActiveRenderStages[stage.Index].Active)
                return;
            var renderEffect = renderEffects[renderObject.StaticObjectNode * effectSlotCount + effectSlot.Index];
            renderEffect?.EffectValidator.ValidateParameter(ForwardShadingKeys.ScreenSpaceLighting, Shader);
        });
    }

    public override void Prepare(RenderDrawContext context)
    {
        base.Prepare(context);
        if (ScreenSpace == null)
            return;

        var white = context.GraphicsDevice.GetSharedWhiteTexture();
        var black = GetBlack(context.GraphicsDevice);
        foreach (var view in RenderSystem.Views)
        {
            var viewFeature = view.Features[RootRenderFeature.Index];
            if (viewFeature.Layouts.Count == 0)
                continue;
            // (a view without terms this frame: none applied)
            ScreenSpace.Views.TryGetValue(view, out var data);
            var current = data != null && data.Frame == RenderSystem.FrameCounter;

            foreach (var viewLayout in viewFeature.Layouts)
            {
                if (viewLayout.State != RenderEffectState.Normal)
                    continue;
                var group = viewLayout.GetLogicalGroup(viewKey);
                if (group.Hash == ObjectId.Empty)
                    continue;
                if (!layoutParameters.TryGetValue(group.Hash, out var parameters))
                {
                    var layout = new ParameterCollectionLayout();
                    layout.ProcessLogicalGroup(viewLayout, ref group);
                    parameters = new ParameterCollection();
                    parameters.UpdateLayout(layout);
                    layoutParameters.Add(group.Hash, parameters);
                }

                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxAmbientOcclusion, current && data.AmbientOcclusion != null ? data.AmbientOcclusion : white);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxReflections, current && data.Reflections != null ? data.Reflections : black);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxIndirect, current && data.Indirect != null ? data.Indirect : black);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxScreenSize, current ? data.ScreenSize : new Vector4(1, 1, 1, 1));
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxPreviousViewProjection, current ? data.PreviousViewProjection : Matrix.Identity);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxAmbientOcclusionStrength, current && data.AmbientOcclusion != null ? data.AmbientOcclusionStrength : 0.0f);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxReflectionStrength, current && data.Reflections != null ? data.ReflectionStrength : 0.0f);
                parameters.Set(PostFxScreenSpaceLightingKeys.PostFxIndirectStrength, current && data.Indirect != null ? data.IndirectStrength : 0.0f);
                viewLayout.Entries[view.Index].Resources.UpdateLogicalGroup(ref group, parameters);
            }
        }
    }
}

/// <summary>
/// Contact shadows on the surfaces lit by the light they were traced for (PostFxContactShadowReceiver).
/// </summary>
internal sealed class ContactShadowReceiverExtension : IDirectionalShadowReceiverExtension
{
    private static readonly ShaderClassSource Shader = new("PostFxContactShadowReceiver");

    public PostFxScreenSpace ScreenSpace { get; set; }

    public ShaderClassCode CreateShaderSource() => Shader;

    public IDirectionalShadowReceiverParameters CreateParameters() => new Parameters(this);

    private sealed class Parameters(ContactShadowReceiverExtension owner) : IDirectionalShadowReceiverParameters
    {
        private ObjectParameterKey<Texture> textureKey;
        private ValueParameterKey<Vector4> screenKey;
        private ValueParameterKey<int> lightIndexKey;

        public void UpdateLayout(string compositionKey)
        {
            textureKey = PostFxContactShadowReceiverKeys.ContactShadowTexture.ComposeWith(compositionKey);
            screenKey = PostFxContactShadowReceiverKeys.ContactShadowScreen.ComposeWith(compositionKey);
            lightIndexKey = PostFxContactShadowReceiverKeys.ContactShadowLightIndex.ComposeWith(compositionKey);
        }

        public void ApplyViewParameters(RenderDrawContext context, ParameterCollection parameters, FastListStruct<LightDynamicEntry> currentLights)
        {
            // (the lights' parameters are prepared for every view without telling which: the contact shadows traced this
            // frame, the camera's)
            ScreenSpaceViewData data = null;
            var frame = context.RenderContext.RenderSystem.FrameCounter;
            if (owner.ScreenSpace != null)
            {
                foreach (var candidate in owner.ScreenSpace.Views.Values)
                {
                    if (candidate.Frame == frame && candidate.ContactShadows != null && candidate.ContactLight != null)
                    {
                        data = candidate;
                        break;
                    }
                }
            }
            var lightIndex = -1;
            if (data != null)
            {
                for (int i = 0; i < currentLights.Count; i++)
                {
                    if (currentLights[i].Light == data.ContactLight)
                    {
                        lightIndex = i;
                        break;
                    }
                }
            }
            parameters.Set(textureKey, lightIndex >= 0 ? data.ContactShadows : context.GraphicsDevice.GetSharedWhiteTexture());
            parameters.Set(screenKey, lightIndex >= 0 ? new Vector4(data.ContactShadowStrength, 0, data.ScreenSize.Z, data.ScreenSize.W) : Vector4.Zero);
            parameters.Set(lightIndexKey, lightIndex);
        }
    }
}

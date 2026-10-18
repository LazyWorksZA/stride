// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Serialization;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;

namespace Stride.Assets.Rendering.Frame;

/// <summary>
/// Works out, from a <see cref="GraphicsCompositorAsset"/> alone, what an entry point runs: the steps in order, the features
/// routed into each stage, and when the temporaries the renderers allocate are in use.
/// </summary>
/// <remarks>
/// It mirrors what the renderers do at runtime (<see cref="ForwardRenderer"/>'s DrawCore and DrawView in particular),
/// so it follows them: a renderer it doesn't know becomes one <see cref="FrameStepKind.Unknown"/> step.
/// What depends on the device or on the objects drawn (the MSAA level the device supports, the stage a transparent selector picks
/// for an object) is reported as a condition, not resolved.
/// </remarks>
public static class CompositorFrameBuilder
{
    // Stride.Assets doesn't reference Stride.Particles; this selector only ever uses its transparent stage
    private const string ParticleSelectorTypeName = "Stride.Particles.Rendering.ParticleEmitterTransparentRenderStageSelector";

    public static CompositorFrame Build(GraphicsCompositorAsset asset, CompositorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var (root, camera, target, note) = entry switch
        {
            CompositorEntry.Game => (asset.Game, null, new FrameTarget("Back buffer", null), null),
            CompositorEntry.SingleView => (asset.SingleView, new FrameCamera(null, "Cubemap capture camera", null), new FrameTarget("Cubemap face", null),
                "Single View draws from the camera of whoever uses it, such as cubemap capture."),
            CompositorEntry.Editor => (asset.Editor, new FrameCamera(null, "Editor camera", null), new FrameTarget("Editor viewport", null),
                "The editor wraps this entry point and draws it from the editor camera."),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };

        var walker = new Walker(asset);
        walker.Walk(root, [], camera ?? new FrameCamera(null, "None", null), target);
        return new CompositorFrame { Entry = entry, EntryNote = note, Steps = walker.Steps, Resources = walker.Resources };
    }

    /// <summary>
    /// The features each stage receives, from every feature's <see cref="RootRenderFeature.RenderStageSelectors"/>.
    /// </summary>
    public static Dictionary<RenderStage, List<RoutedFeature>> Route(GraphicsCompositorAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var routes = new Dictionary<RenderStage, List<RoutedFeature>>();
        foreach (var feature in asset.RenderFeatures)
        {
            if (feature is null)
                continue;

            foreach (var selector in feature.RenderStageSelectors)
            {
                foreach (var (stage, condition) in StagesOf(selector))
                {
                    if (stage is null)
                        continue;

                    if (!routes.TryGetValue(stage, out var features))
                        routes.Add(stage, features = []);
                    features.Add(new RoutedFeature(feature, selector, condition));
                }
            }
        }
        return routes;
    }

    private static IEnumerable<(RenderStage Stage, string Condition)> StagesOf(RenderStageSelector selector)
    {
        switch (selector)
        {
            case SimpleGroupToRenderStageSelector simple:
                yield return (simple.RenderStage, "objects in its render groups");
                break;
            case TransparentRenderStageSelector transparent when transparent.GetType().FullName == ParticleSelectorTypeName:
                yield return (transparent.TransparentRenderStage, "every emitter in its render groups");
                break;
            case TransparentRenderStageSelector transparent:
                yield return (transparent.OpaqueRenderStage, "objects that aren't transparent");
                yield return (transparent.TransparentRenderStage, "transparent objects");
                break;
            case ShadowMapRenderStageSelector shadow:
                yield return (shadow.ShadowMapRenderStage, "shadow casters");
                break;
            case null:
                break;
            default:
                foreach (var property in selector.GetType().GetProperties().Where(p => p.PropertyType == typeof(RenderStage) && p.GetIndexParameters().Length == 0))
                    yield return ((RenderStage)property.GetValue(selector), $"{selector.GetType().Name}.{property.Name}");
                break;
        }
    }

    private sealed class Walker(GraphicsCompositorAsset asset)
    {
        private readonly Dictionary<RenderStage, List<RoutedFeature>> routes = Route(asset);
        private readonly Dictionary<RootRenderFeature, int> featureOrder = asset.RenderFeatures.Select((f, i) => (f, i)).Where(x => x.f is not null).ToDictionary(x => x.f, x => x.i);

        public List<FrameStep> Steps { get; } = [];

        public List<FrameResource> Resources { get; } = [];

        public void Walk(ISceneRenderer renderer, IReadOnlyList<ISceneRenderer> parents, FrameCamera camera, FrameTarget target)
        {
            if (renderer is null)
                return;

            IReadOnlyList<ISceneRenderer> path = [.. parents, renderer];
            switch (renderer)
            {
                case SceneExternalCameraRenderer external:
                    Walk(external.Child, path, new FrameCamera(null, "External camera", external), target);
                    break;
                case SceneCameraRenderer cameraRenderer:
                    var slot = cameraRenderer.Camera;
                    Walk(cameraRenderer.Child, path, new FrameCamera(slot, slot?.Name ?? "None (draws nothing)", cameraRenderer), target);
                    break;
                case SceneRendererCollection collection:
                    foreach (var child in collection.Children)
                        Walk(child, path, camera, target);
                    break;
                case ForceAspectRatioSceneRenderer aspect:
                    Walk(aspect.Child, path, camera, target);
                    break;
                case RenderTextureSceneRenderer renderTexture:
                    WalkRenderTexture(renderTexture, path, camera);
                    break;
                case ForwardRenderer forward:
                    WalkForward(forward, path, camera, target);
                    break;
                case ClearRenderer:
                    Add(FrameStepKind.Clear, "Clear", path, camera, target);
                    break;
                case SingleStageRenderer single when single.RenderStage is not null:
                    AddStage(FrameStepKind.Stage, single.RenderStage.Name, [single.RenderStage], path, camera, target);
                    break;
                case SingleStageRenderer:
                    break;
                case DebugRenderer debug:
                    foreach (var stage in debug.DebugRenderStages.Where(s => s is not null))
                        AddStage(FrameStepKind.Stage, stage.Name, [stage], path, camera, target);
                    break;
                default:
                    Add(FrameStepKind.Unknown, renderer.GetType().Name, path, camera, target, "Defined in code; its steps can't be read from the asset.");
                    break;
            }
        }

        private void WalkRenderTexture(RenderTextureSceneRenderer renderer, IReadOnlyList<ISceneRenderer> path, FrameCamera camera)
        {
            if (renderer.RenderTexture is null)
                return;

            var target = new FrameTarget(AttachedReferenceManager.GetUrl(renderer.RenderTexture) ?? "Render texture", renderer.RenderTexture);
            var first = Steps.Count;
            Walk(renderer.Child, path, camera, target);
            if (Steps.Count > first)
                Resources.Add(new FrameResource(FrameResourceKind.Depth, $"Depth ({target.Name})", renderer, first, Steps.Count - 1));
        }

        private void WalkForward(ForwardRenderer forward, IReadOnlyList<ISceneRenderer> path, FrameCamera camera, FrameTarget target)
        {
            var shadowMapRenderer = asset.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault()?
                .RenderFeatures.OfType<ForwardLightingRenderFeature>().FirstOrDefault()?.ShadowMapRenderer;
            var shadowStages = forward.ShadowMapRenderStages
                .Concat(shadowMapRenderer?.Renderers.Select(r => r?.ShadowCasterRenderStage) ?? [])
                .Where(s => s is not null).Distinct().ToList();

            var shadowStep = -1;
            if (shadowMapRenderer is not null && shadowStages.Count > 0)
            {
                shadowStep = Steps.Count;
                AddStage(FrameStepKind.ShadowMaps, "Shadow maps", shadowStages, path, camera, target, "Drawn from each light with shadows; directional cascades are fitted to the camera.");
            }

            var depthFirst = Steps.Count;
            if (forward.Clear is not null)
                Add(FrameStepKind.Clear, "Clear", path, camera, target);
            if (forward.LightProbes && forward.GBufferRenderStage is not null)
                AddStage(FrameStepKind.ZPrepass, "Z-prepass", [forward.GBufferRenderStage], path, camera, target, "Runs because Light probes is on and a GBuffer stage is set.");

            var colourFirst = Steps.Count;
            if (forward.OpaqueRenderStage is not null)
                AddStage(FrameStepKind.Stage, forward.OpaqueRenderStage.Name, [forward.OpaqueRenderStage], path, camera, target);
            if (forward.SubsurfaceScatteringBlurEffect is not null)
                Add(FrameStepKind.SubsurfaceScattering, "Subsurface scattering", path, camera, target, "Runs when the opaque stage writes a material index.");
            if (forward.TransparentRenderStage is not null)
                AddStage(FrameStepKind.Stage, forward.TransparentRenderStage.Name, [forward.TransparentRenderStage], path, camera, target,
                    forward.BindDepthAsResourceDuringTransparentRendering ? "Depth is readable as the \"Depth\" shader resource." : null);
            var lastStage = Steps.Count - 1;

            // DrawView stops after the transparent stage when there is no opaque stage to take the colour target from
            if (forward.OpaqueRenderStage is not null)
            {
                if (forward.MSAALevel != MultisampleCount.None)
                    Add(FrameStepKind.MsaaResolve, "MSAA resolve", path, camera, target, $"{forward.MSAALevel} requested; the device may support fewer samples.");
                if (forward.LightShafts is not null)
                    Add(FrameStepKind.LightShafts, "Light shafts", path, camera, target);
                if (forward.PostEffects is not null)
                    Add(FrameStepKind.PostEffects, "Post effects", path, camera, target);
            }

            var last = Steps.Count - 1;
            if (shadowStep >= 0 && lastStage > shadowStep)
                Resources.Add(new FrameResource(FrameResourceKind.ShadowAtlas, "Shadow atlas", shadowMapRenderer, shadowStep, lastStage));
            if (last >= depthFirst)
                Resources.Add(new FrameResource(FrameResourceKind.Depth, "Depth", forward, depthFirst, last));
            if (last >= colourFirst && forward.OpaqueRenderStage is not null)
                Resources.Add(new FrameResource(FrameResourceKind.Colour, forward.PostEffects is not null ? "HDR colour" : "Colour", forward, colourFirst, last));
        }

        private void AddStage(FrameStepKind kind, string title, IReadOnlyList<RenderStage> stages, IReadOnlyList<ISceneRenderer> path, FrameCamera camera, FrameTarget target, string condition = null)
        {
            var groups = stages
                .SelectMany(s => routes.TryGetValue(s, out var features) ? features : [])
                .GroupBy(r => r.Feature).Select(g => g.First())
                .GroupBy(r => r.Feature.SortKey)
                .OrderBy(g => g.Key)
                .Select(g => new FeatureGroup(g.Key, g.OrderBy(r => featureOrder.GetValueOrDefault(r.Feature)).ToList()))
                .ToList();
            Steps.Add(new FrameStep { Kind = kind, Title = title, Owner = path[^1], OwnerPath = path, Camera = camera, Target = target, Stages = stages, FeatureGroups = groups, Condition = condition });
        }

        private void Add(FrameStepKind kind, string title, IReadOnlyList<ISceneRenderer> path, FrameCamera camera, FrameTarget target, string condition = null)
        {
            Steps.Add(new FrameStep { Kind = kind, Title = title, Owner = path[^1], OwnerPath = path, Camera = camera, Target = target, Condition = condition });
        }
    }
}

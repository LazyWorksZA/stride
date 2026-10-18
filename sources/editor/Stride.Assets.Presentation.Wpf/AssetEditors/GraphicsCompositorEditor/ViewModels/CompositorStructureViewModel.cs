// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Stride.Assets.Rendering;
using Stride.Assets.Rendering.Frame;
using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Quantum;
using Stride.Core.Serialization;
using Stride.Rendering;
using Stride.Rendering.Compositing;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;

namespace Stride.Assets.Presentation.AssetEditors.GraphicsCompositorEditor.ViewModels
{
    /// <summary>
    /// Everything the compositor holds in one tree: its features with what they contain, its entry points' renderers, its stages and its cameras.
    /// </summary>
    public sealed class CompositorStructureViewModel : DispatcherViewModel
    {
        private readonly GraphicsCompositorEditorViewModel editor;

        public CompositorStructureViewModel([NotNull] GraphicsCompositorEditorViewModel editor) : base(editor.ServiceProvider)
        {
            this.editor = editor;
        }

        public ObservableList<StructureNodeViewModel> Roots { get; } = [];

        public void Rebuild()
        {
            var asset = (GraphicsCompositorAsset)editor.Asset.Asset;
            var expanded = Roots.Where(r => r.IsExpanded).Select(r => r.Title).ToHashSet();
            Roots.Clear();

            var features = Group("Features", asset.RenderFeatures.Count);
            var featuresNode = editor.NodeOf(asset)[nameof(GraphicsCompositorAsset.RenderFeatures)].Target;
            for (var i = 0; i < asset.RenderFeatures.Count; i++)
            {
                var feature = asset.RenderFeatures[i];
                if (feature is not null)
                    features.Children.Add(FeatureNode(feature, editor.ArchetypeMarker(featuresNode, i)));
            }
            Roots.Add(features);

            var renderers = Group("Renderers");
            renderers.Children.Add(EntryNode("Game", asset.Game));
            renderers.Children.Add(EntryNode("Single View", asset.SingleView));
            renderers.Children.Add(EntryNode("Editor", asset.Editor));
            Roots.Add(renderers);

            var stages = Group("Stages", asset.RenderStages.Count);
            foreach (var stage in asset.RenderStages.Where(s => s is not null))
                stages.Children.Add(Leaf(stage.Name, stage, $"effect slot {stage.EffectSlotName}"));
            Roots.Add(stages);

            var cameras = Group("Cameras", asset.Cameras.Count);
            foreach (var slot in asset.Cameras.Where(s => s is not null))
                cameras.Children.Add(Leaf(slot.Name, slot, "camera slot"));
            Roots.Add(cameras);

            foreach (var root in Roots)
                root.IsExpanded = expanded.Count == 0 ? root.Title is "Features" or "Renderers" : expanded.Contains(root.Title);
        }

        private StructureNodeViewModel FeatureNode(RootRenderFeature feature, string marker)
        {
            var sortKeyChanged = editor.HasArchetype && ((IAssetMemberNode)editor.NodeOf(feature)[nameof(RootRenderFeature.SortKey)]).IsContentOverridden();
            var node = Leaf(CompositorFrameViewModel.NameOf(feature), feature, $"sort key {feature.SortKey}{(sortKeyChanged ? " (changed here)" : "")}", marker);

            var selectors = Group("Stage selectors", feature.RenderStageSelectors.Count);
            foreach (var selector in feature.RenderStageSelectors.Where(s => s is not null))
            {
                var stages = string.Join(" / ", CompositorFrameBuilder.StagesOf(selector).Where(s => s.Stage is not null).Select(s => s.Stage.Name).Distinct());
                selectors.Children.Add(Leaf(CompositorFrameViewModel.NameOf(selector), selector, $"→ {stages}"));
            }
            node.Children.Add(selectors);

            if (feature is RootEffectRenderFeature effectFeature)
            {
                var processors = Group("Pipeline processors", effectFeature.PipelineProcessors.Count);
                foreach (var processor in effectFeature.PipelineProcessors.Where(p => p is not null))
                    processors.Children.Add(Leaf(CompositorFrameViewModel.NameOf(processor), processor));
                node.Children.Add(processors);
            }

            if (feature is MeshRenderFeature mesh)
            {
                var subFeatures = Group("Sub features, in order", mesh.RenderFeatures.Count);
                var subFeaturesNode = editor.NodeOf(mesh)[nameof(MeshRenderFeature.RenderFeatures)].Target;
                for (var i = 0; i < mesh.RenderFeatures.Count; i++)
                {
                    var subFeature = mesh.RenderFeatures[i];
                    if (subFeature is null)
                        continue;

                    var subNode = Leaf(CompositorFrameViewModel.NameOf(subFeature), subFeature, null, editor.ArchetypeMarker(subFeaturesNode, i));
                    if (subFeature is ForwardLightingRenderFeature lighting)
                        AddLighting(subNode, lighting);
                    subFeatures.Children.Add(subNode);
                }
                node.Children.Add(subFeatures);
            }
            return node;
        }

        private void AddLighting(StructureNodeViewModel node, ForwardLightingRenderFeature lighting)
        {
            var lightRenderers = Group("Light renderers", lighting.LightRenderers.Count);
            var lightRenderersNode = editor.NodeOf(lighting)[nameof(ForwardLightingRenderFeature.LightRenderers)].Target;
            for (var i = 0; i < lighting.LightRenderers.Count; i++)
            {
                if (lighting.LightRenderers[i] is { } lightRenderer)
                    lightRenderers.Children.Add(Leaf(CompositorFrameViewModel.NameOf(lightRenderer), lightRenderer, null, editor.ArchetypeMarker(lightRenderersNode, i)));
            }
            node.Children.Add(lightRenderers);

            if (lighting.ShadowMapRenderer is not { } shadowMapRenderer)
                return;

            var shadowRenderers = Group("Shadow map renderers", shadowMapRenderer.Renderers.Count);
            var shadowRenderersNode = editor.NodeOf(shadowMapRenderer)[nameof(IShadowMapRenderer.Renderers)].Target;
            for (var i = 0; i < shadowMapRenderer.Renderers.Count; i++)
            {
                if (shadowMapRenderer.Renderers[i] is { } shadowRenderer)
                    shadowRenderers.Children.Add(Leaf(CompositorFrameViewModel.NameOf(shadowRenderer), shadowRenderer, $"→ {shadowRenderer.ShadowCasterRenderStage?.Name ?? "no stage"}", editor.ArchetypeMarker(shadowRenderersNode, i)));
            }
            node.Children.Add(shadowRenderers);
        }

        private StructureNodeViewModel EntryNode(string title, ISceneRenderer renderer)
        {
            var node = Leaf(title, null, renderer is null ? "not set" : "entry point");
            node.IsExpanded = renderer is not null;
            if (renderer is not null)
                node.Children.Add(RendererNode(renderer, null));
            return node;
        }

        private StructureNodeViewModel RendererNode(ISceneRenderer renderer, string prefix)
        {
            var title = prefix + CompositorFrameViewModel.NameOf(renderer);
            switch (renderer)
            {
                case SceneCameraRenderer cameraRenderer:
                {
                    var node = Leaf(title, renderer, $"camera {(cameraRenderer is SceneExternalCameraRenderer ? "external" : cameraRenderer.Camera?.Name ?? "none")}");
                    AddChild(node, cameraRenderer.Child);
                    return node;
                }
                case SceneRendererCollection collection:
                {
                    var node = Leaf(title, renderer, "runs its children in order");
                    var index = 1;
                    foreach (var child in collection.Children.Where(c => c is not null))
                        node.Children.Add(RendererNode(child, $"{index++} · "));
                    return node;
                }
                case RenderTextureSceneRenderer renderTexture:
                {
                    var node = Leaf(title, renderer, $"into {(renderTexture.RenderTexture is null ? "nothing" : AttachedReferenceManager.GetUrl(renderTexture.RenderTexture) ?? "a texture")}");
                    AddChild(node, renderTexture.Child);
                    return node;
                }
                case ForceAspectRatioSceneRenderer aspect:
                {
                    var node = Leaf(title, renderer);
                    AddChild(node, aspect.Child);
                    return node;
                }
                case ForwardRenderer forward:
                {
                    var node = Leaf(title, renderer, $"draws {string.Join(", ", new[] { forward.OpaqueRenderStage, forward.TransparentRenderStage }.Where(s => s is not null).Select(s => s.Name))}");
                    if (forward.PostEffects is not null)
                        node.Children.Add(Leaf(CompositorFrameViewModel.NameOf(forward.PostEffects), forward.PostEffects, "post effects"));
                    return node;
                }
                default:
                    return Leaf(title, renderer);
            }
        }

        private void AddChild(StructureNodeViewModel node, ISceneRenderer child)
        {
            if (child is not null)
                node.Children.Add(RendererNode(child, null));
        }

        private StructureNodeViewModel Group(string title, int? count = null) => new(this, editor, title, null, count?.ToString(), null);

        private StructureNodeViewModel Leaf(string title, object value, string detail = null, string marker = null) => new(this, editor, title, value, detail, marker);
    }

    public sealed class StructureNodeViewModel : DispatcherViewModel
    {
        private bool isExpanded;

        public StructureNodeViewModel(CompositorStructureViewModel structure, GraphicsCompositorEditorViewModel editor, string title, object value, string detail, string marker)
            : base(structure.ServiceProvider)
        {
            Title = title;
            Value = value;
            Detail = detail;
            Marker = marker;
            Editor = editor;
        }

        public string Title { get; }

        public string Detail { get; }

        /// <summary>
        /// Whether the item comes from the archetype or was added by this compositor, when it has an archetype.
        /// </summary>
        public string Marker { get; }

        public bool IsAddedHere => Marker == GraphicsCompositorEditorViewModel.AddedHereMarker;

        /// <summary>
        /// The asset object this node stands for, or <c>null</c> for a group.
        /// </summary>
        public object Value { get; }

        public List<StructureNodeViewModel> Children { get; } = [];

        public bool IsExpanded { get => isExpanded; set => SetValue(ref isExpanded, value); }

        internal GraphicsCompositorEditorViewModel Editor { get; }
    }
}

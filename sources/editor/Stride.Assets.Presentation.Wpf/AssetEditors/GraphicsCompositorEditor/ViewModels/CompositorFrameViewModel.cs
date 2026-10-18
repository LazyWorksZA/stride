// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Assets.Rendering;
using Stride.Assets.Rendering.Frame;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.ViewModels;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Presentation.AssetEditors.GraphicsCompositorEditor.ViewModels
{
    /// <summary>
    /// The frame an entry point of the compositor runs: one lane per step, the features routed into each stage, and when the renderers' temporaries are in use.
    /// </summary>
    public sealed class CompositorFrameViewModel : DispatcherViewModel
    {
        private readonly GraphicsCompositorEditorViewModel editor;
        private CompositorEntry entry = CompositorEntry.Game;
        private string entryNote;
        private object highlighted;

        public CompositorFrameViewModel([NotNull] GraphicsCompositorEditorViewModel editor) : base(editor.ServiceProvider)
        {
            this.editor = editor;
        }

        public IReadOnlyList<CompositorEntry> Entries { get; } = Enum.GetValues<CompositorEntry>();

        public CompositorEntry Entry { get => entry; set => SetValue(ref entry, value, Rebuild); }

        public string EntryNote { get => entryNote; private set => SetValue(ref entryNote, value); }

        public ObservableList<FrameLaneViewModel> Lanes { get; } = [];

        /// <summary>
        /// The resources shown as bars beside the lanes, in column order.
        /// </summary>
        public IReadOnlyList<FrameResourceKind> ResourceColumns { get; } = [FrameResourceKind.ShadowAtlas, FrameResourceKind.Depth, FrameResourceKind.Colour];

        public void Rebuild()
        {
            var frame = CompositorFrameBuilder.Build((GraphicsCompositorAsset)editor.Asset.Asset, Entry);
            EntryNote = frame.EntryNote;
            Lanes.Clear();
            for (var i = 0; i < frame.Steps.Count; i++)
                Lanes.Add(new FrameLaneViewModel(this, editor, frame, i));
            Highlight(highlighted);
        }

        /// <summary>
        /// Shows which lanes and features relate to the object selected anywhere in the editor.
        /// </summary>
        public void Highlight(object selected)
        {
            highlighted = selected;
            var related = Lanes.Where(l => l.IsRelatedTo(selected)).ToHashSet();
            foreach (var lane in Lanes)
            {
                lane.IsHighlighted = related.Contains(lane);
                lane.IsDimmed = selected is not null && related.Count > 0 && !related.Contains(lane);
                lane.SettingsHint = selected is ForwardRenderer forward && lane.Step.Owner == forward ? SettingsHint(lane.Step, forward) : null;
                foreach (var chip in lane.Chips)
                    chip.IsHighlighted = selected is not null && chip.Routed.Feature == selected;
            }
        }

        // The renderer settings each step of a forward renderer depends on
        private static string SettingsHint(FrameStep step, ForwardRenderer forward) => step.Kind switch
        {
            FrameStepKind.ShadowMaps => "ShadowMapRenderStages",
            FrameStepKind.Clear => "Clear",
            FrameStepKind.ZPrepass => "LightProbes · GBufferRenderStage",
            FrameStepKind.Stage when step.Stages.Contains(forward.OpaqueRenderStage) => "OpaqueRenderStage",
            FrameStepKind.Stage when step.Stages.Contains(forward.TransparentRenderStage) => "TransparentRenderStage · BindDepthAsResourceDuringTransparentRendering",
            FrameStepKind.SubsurfaceScattering => "SubsurfaceScatteringBlurEffect",
            FrameStepKind.MsaaResolve => "MSAALevel",
            FrameStepKind.LightShafts => "LightShafts",
            FrameStepKind.PostEffects => "PostEffects",
            _ => null,
        };

        internal static string NameOf(object value)
        {
            var name = value switch
            {
                null => "None",
                RenderStage stage => stage.Name,
                SceneCameraSlot slot => slot.Name,
                ComponentBase { Name: { Length: > 0 } componentName } => componentName,
                _ => DisplayAttribute.GetDisplayName(value.GetType()) ?? value.GetType().Name,
            };
            return value is ISharedRenderer ? name + " ★" : name;
        }
    }

    public sealed class FrameLaneViewModel : DispatcherViewModel
    {
        private bool isHighlighted;
        private bool isDimmed;
        private string settingsHint;

        public FrameLaneViewModel(CompositorFrameViewModel frame, GraphicsCompositorEditorViewModel editor, CompositorFrame compositorFrame, int index) : base(frame.ServiceProvider)
        {
            Step = compositorFrame.Steps[index];
            Title = Step.Title;
            Detail = $"{CompositorFrameViewModel.NameOf(Step.Owner)} · camera {Step.Camera.Name} · into {Step.Target.Name}";
            Chips = Step.FeatureGroups.SelectMany(g => g.Features).Select(r => new FrameChipViewModel(this, editor, r)).ToList();
            ResourceCells = frame.ResourceColumns.Select(kind =>
            {
                var resource = compositorFrame.Resources.FirstOrDefault(r => r.Kind == kind && r.FirstStep <= index && index <= r.LastStep);
                return new FrameResourceCell(kind, resource, resource is not null && resource.FirstStep == index, resource is not null && resource.LastStep == index);
            }).ToList();
            var selectTarget = Step.Stages.Count == 1 ? Step.Stages[0] : (object)Step.Owner;
            SelectCommand = new AnonymousCommand(ServiceProvider, () => editor.SelectFromFrame(selectTarget, "Frame"));
        }

        public FrameStep Step { get; }

        public string Title { get; }

        public string Detail { get; }

        public string Condition => Step.Condition;

        /// <summary>
        /// What this kind of step is for, for readers new to rendering.
        /// </summary>
        public string Explanation => Step.Kind switch
        {
            FrameStepKind.ShadowMaps => "Each light with shadows renders the scene's depth from its own point of view into the shadow atlas; later stages sample it to know what is in shadow.",
            FrameStepKind.Clear => "Resets the colour and depth targets before anything is drawn.",
            FrameStepKind.ZPrepass => "Draws only depth first, so later passes can skip hidden pixels; the forward renderer also bakes light probes against it.",
            FrameStepKind.Stage => "A render stage: every feature routed here draws its objects, lowest sort key first, ordered within a key by the stage's sort mode.",
            FrameStepKind.SubsurfaceScattering => "Blurs light under the surface of materials such as skin.",
            FrameStepKind.MsaaResolve => "Averages the multisampled targets into normal ones so later effects can read them.",
            FrameStepKind.LightShafts => "Adds light shafts using the depth buffer.",
            FrameStepKind.PostEffects => "Image effects on the finished colour (and depth): tone mapping, bloom, anti-aliasing and so on; writes the final image.",
            _ => "A renderer defined in code: the editor can't see what it draws.",
        };

        public bool IsUnknown => Step.Kind == FrameStepKind.Unknown;

        public IReadOnlyList<FrameChipViewModel> Chips { get; }

        public IReadOnlyList<FrameResourceCell> ResourceCells { get; }

        public ICommandBase SelectCommand { get; }

        public bool IsHighlighted { get => isHighlighted; set => SetValue(ref isHighlighted, value); }

        public bool IsDimmed { get => isDimmed; set => SetValue(ref isDimmed, value); }

        /// <summary>
        /// When the renderer that runs this step is selected, the settings of it this step depends on.
        /// </summary>
        public string SettingsHint { get => settingsHint; set => SetValue(ref settingsHint, value); }

        internal bool IsRelatedTo(object selected) => selected switch
        {
            null => false,
            RenderStage stage => Step.Stages.Contains(stage),
            SceneCameraSlot slot => Step.Camera.Slot == slot,
            ISceneRenderer renderer => Step.OwnerPath.Contains(renderer),
            RootRenderFeature feature => Chips.Any(c => c.Routed.Feature == feature),
            _ => false,
        };
    }

    public sealed class FrameChipViewModel : DispatcherViewModel
    {
        private bool isHighlighted;

        public FrameChipViewModel(FrameLaneViewModel lane, GraphicsCompositorEditorViewModel editor, RoutedFeature routed) : base(lane.ServiceProvider)
        {
            Routed = routed;
            Name = CompositorFrameViewModel.NameOf(routed.Feature);
            Marker = editor.ArchetypeMarkerOf(routed.Feature);
            SelectCommand = new AnonymousCommand(ServiceProvider, () => editor.SelectFromFrame(routed.Feature, "Frame"));
        }

        public RoutedFeature Routed { get; }

        public byte SortKey => Routed.Feature.SortKey;

        public string Name { get; }

        public string Condition => Routed.Condition;

        /// <summary>
        /// Whether the feature comes from the archetype or was added by this compositor, when it has an archetype.
        /// </summary>
        public string Marker { get; }

        public bool IsAddedHere => Marker == GraphicsCompositorEditorViewModel.AddedHereMarker;

        public ICommandBase SelectCommand { get; }

        public bool IsHighlighted { get => isHighlighted; set => SetValue(ref isHighlighted, value); }
    }

    /// <param name="Resource">The resource in use during this step, or <c>null</c>.</param>
    public sealed record FrameResourceCell(FrameResourceKind Kind, FrameResource Resource, bool IsFirst, bool IsLast)
    {
        public bool IsActive => Resource is not null;

        public string ToolTip => Resource is null ? null : $"{Resource.Name}, allocated by {CompositorFrameViewModel.NameOf(Resource.Owner)}. " + Kind switch
        {
            FrameResourceKind.Depth => "For each pixel, how far the nearest surface is from the camera; released when its renderer finishes, so nothing after it can read it.",
            FrameResourceKind.Colour => "The image being drawn; high dynamic range when post effects tone-map it afterwards.",
            _ => "Shadow maps of every light, packed in one texture the lighting reads.",
        };
    }
}

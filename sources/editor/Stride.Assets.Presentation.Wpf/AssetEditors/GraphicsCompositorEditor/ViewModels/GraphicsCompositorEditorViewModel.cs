// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Collections;
using Stride.Core.Presentation.Commands;
using Stride.Core.Presentation.Quantum;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.ViewModels;
using Stride.Core.Quantum;
using Stride.Core.Translation;
using Stride.Assets.Presentation.ViewModel;
using Stride.Assets.Rendering;
using Stride.Core.Assets.Editor.Annotations;
using Stride.Core.Assets.Quantum;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Presentation.AssetEditors.GraphicsCompositorEditor.ViewModels
{
    [AssetEditorViewModel<GraphicsCompositorViewModel>]
    public class GraphicsCompositorEditorViewModel : AssetEditorViewModel
    {
        protected readonly GraphViewModelService ViewModelService;

        private readonly IObjectNode graphicsCompositorNode;
        private readonly IObjectNode renderStagesNode;
        private readonly IObjectNode renderFeaturesNode;
        private readonly IObjectNode cameraSlotsNode;
        private readonly IObjectNode sharedRenderersNode;
        private readonly Dictionary<object, CompositorObjectViewModel> objectItems = new(ReferenceEqualityComparer.Instance);
        private string frameSelectionSource;
        private string selectionBanner;
        private bool rebuildPending;

        public const string AddedHereMarker = "added here";
        public const string FromArchetypeMarker = "from archetype";

        public GraphicsCompositorEditorViewModel([NotNull] GraphicsCompositorViewModel graphicsCompositor) : base(graphicsCompositor)
        {
            // Create the service needed to manage observable view models
            ViewModelService = new GraphViewModelService(Session.AssetNodeContainer);

            // Update the service provider of this view model to contains the ObservableViewModelService we created.
            ServiceProvider = new ViewModelServiceProvider(ServiceProvider, ViewModelService.Yield());

            // Get some quantum nodes
            graphicsCompositorNode = Session.AssetNodeContainer.GetNode(graphicsCompositor.Asset);
            renderStagesNode = graphicsCompositorNode[nameof(GraphicsCompositorAsset.RenderStages)].Target;
            renderFeaturesNode = graphicsCompositorNode[nameof(GraphicsCompositorAsset.RenderFeatures)].Target;
            cameraSlotsNode = graphicsCompositorNode[nameof(GraphicsCompositorAsset.Cameras)].Target;
            sharedRenderersNode = graphicsCompositorNode[nameof(GraphicsCompositorAsset.SharedRenderers)].Target;

            // Setup commands
            DeleteSelectionCommand = new AnonymousCommand(ServiceProvider, DeleteSelection);

            AddNewRenderStageCommand = new AnonymousCommand(ServiceProvider, AddNewRenderStage);
            RemoveSelectedRenderStagesCommand = new AnonymousCommand(ServiceProvider, RemoveSelectedRenderStages);

            AddNewRenderFeatureCommand = new AnonymousCommand<AbstractNodeType>(ServiceProvider, AddNewRenderFeature);
            AddPackageRenderFeaturesCommand = new AnonymousTaskCommand(ServiceProvider, AddPackageRenderFeatures);
            RemoveSelectedRenderFeaturesCommand = new AnonymousCommand(ServiceProvider, RemoveSelectedRenderFeatures);

            AddNewCameraSlotCommand = new AnonymousCommand(ServiceProvider, AddNewCameraSlot);
            RemoveSelectedCameraSlotsCommand = new AnonymousCommand(ServiceProvider, RemoveSelectedCameraSlots);

            Frame = new CompositorFrameViewModel(this);
            Structure = new CompositorStructureViewModel(this);
        }

        public CompositorFrameViewModel Frame { get; }

        public CompositorStructureViewModel Structure { get; }

        /// <summary>
        /// What is selected, where it was selected and where it sits in the compositor.
        /// </summary>
        public string SelectionBanner { get => selectionBanner; private set => SetValue(ref selectionBanner, value); }

        /// <summary>
        /// The selection made in the Frame tab or the Structure tree.
        /// </summary>
        public ObservableList<object> SelectedFrameItems { get; } = new ObservableList<object>();

        internal bool HasArchetype => Asset.Asset.Archetype != null;

        internal IObjectNode NodeOf(object value) => Session.AssetNodeContainer.GetOrCreateNode(value);

        /// <summary>
        /// Whether an item of a collection comes from the archetype or was added by this compositor; <c>null</c> without an archetype.
        /// </summary>
        internal string ArchetypeMarker(IObjectNode collectionNode, int index)
        {
            if (!HasArchetype || collectionNode is not IAssetObjectNode assetNode)
                return null;
            return assetNode.IsItemInherited(new NodeIndex(index)) ? FromArchetypeMarker : AddedHereMarker;
        }

        internal string ArchetypeMarkerOf(RootRenderFeature feature)
        {
            var index = Asset.Asset.RenderFeatures.IndexOf(feature);
            return index < 0 ? null : ArchetypeMarker(renderFeaturesNode, index);
        }

        /// <summary>
        /// Selects an object of the compositor from the Frame tab or the Structure tree, so the property grid shows it.
        /// </summary>
        internal void SelectFromFrame(object value, string source)
        {
            if (value is null)
                return;

            var item = (GraphicsCompositorItemViewModel)(value switch
            {
                RootRenderFeature feature => RenderFeatures.FirstOrDefault(x => x.RenderFeature == feature),
                RenderStage stage => RenderStages.FirstOrDefault(x => x.RenderStage == stage),
                SceneCameraSlot slot => CameraSlots.FirstOrDefault(x => x.CameraSlot == slot),
                _ => null,
            }) ?? ObjectItem(value);

            frameSelectionSource = source;
            SelectedFrameItems.Clear();
            SelectedFrameItems.Add(item);
        }

        private CompositorObjectViewModel ObjectItem(object value)
        {
            if (!objectItems.TryGetValue(value, out var item))
                objectItems.Add(value, item = new CompositorObjectViewModel(this, value, CompositorFrameViewModel.NameOf(value)));
            return item;
        }

        public override GraphicsCompositorViewModel Asset => (GraphicsCompositorViewModel)base.Asset;

        public ObservableList<RenderStageViewModel> RenderStages { get; } = new ObservableList<RenderStageViewModel>();

        public ObservableList<RenderFeatureViewModel> RenderFeatures { get; } = new ObservableList<RenderFeatureViewModel>();
        public ObservableList<AbstractNodeType> RenderFeatureTypes { get; } = new ObservableList<AbstractNodeType>();

        public ObservableList<GraphicsCompositorCameraSlotsViewModel> CameraSlots { get; } = new ObservableList<GraphicsCompositorCameraSlotsViewModel>();

        public ObservableList<IGraphicsCompositorBlockViewModel> Blocks { get; } = new ObservableList<IGraphicsCompositorBlockViewModel>();

        public IObservableCollection<IGraphicsCompositorBlockViewModel> SelectedSharedRenderers { get; } = new ObservableList<IGraphicsCompositorBlockViewModel>();

        public IObservableCollection<IGraphicsCompositorLinkViewModel> SelectedRendererLinks { get; } = new ObservableList<IGraphicsCompositorLinkViewModel>();

        public ObservableList<object> SelectedRenderStages { get; } = new ObservableList<object>();

        public ObservableList<object> SelectedRenderFeatures { get; } = new ObservableList<object>();

        public ObservableList<object> SelectedCameraSlots { get; } = new ObservableList<object>();

        public ObservableList<SharedRendererFactoryViewModel> SharedRendererFactories { get; } = new ObservableList<SharedRendererFactoryViewModel>();

        public ICommandBase DeleteSelectionCommand { get; }

        public ICommandBase AddNewRenderStageCommand { get; }

        public ICommandBase RemoveSelectedRenderStagesCommand { get; }

        public ICommandBase AddNewRenderFeatureCommand { get; }

        /// <summary>
        /// Adds the render features that the packages of this compositor's project provide and that it lacks.
        /// </summary>
        public ICommandBase AddPackageRenderFeaturesCommand { get; }

        public ICommandBase RemoveSelectedRenderFeaturesCommand { get; }

        public ICommandBase AddNewCameraSlotCommand { get; }

        public ICommandBase RemoveSelectedCameraSlotsCommand { get; }

        public sealed override Task<bool> Initialize()
        {
            // Add a block that represents entry points
            var entryPointOutputs = new[]
            {
                nameof(GraphicsCompositorAsset.Game),
                nameof(GraphicsCompositorAsset.SingleView),
                nameof(GraphicsCompositorAsset.Editor)
            };
            var entryPoint = new EntryPointBlockViewModel(this, graphicsCompositorNode, entryPointOutputs);
            entryPoint.Initialize();
            entryPoint.UpdateSlots();
            Blocks.Add(entryPoint);

            // Add blocks
            sharedRenderersNode.ItemChanged += SharedRenderersChanged;
            foreach (var sharedRendererNode in sharedRenderersNode.ItemReferences)
            {
                var sharedRenderer = (ISharedRenderer)sharedRendererNode.TargetNode.Retrieve();
                AddSharedRendererViewModel(sharedRenderer);
            }

            // Now that we have all blocks with all slots, we can update all links
            foreach (var block in Blocks)
            {
                foreach (var slot in block.OutputSlots.Cast<GraphicsCompositorSlotViewModel>())
                {
                    slot.UpdateLink();
                }
            }

            // TODO: Relayout the graph?

            // Add render stages
            renderStagesNode.ItemChanged += RenderStagesChanged;
            foreach (var renderStage in (IEnumerable<RenderStage>)renderStagesNode.Retrieve())
                RenderStages.Add(new RenderStageViewModel(this, renderStage));

            // Add render features
            renderFeaturesNode.ItemChanged += RenderFeaturesChanged;
            foreach (var renderFeature in (IEnumerable<RenderFeature>)renderFeaturesNode.Retrieve())
                RenderFeatures.Add(new RenderFeatureViewModel(this, renderFeature));

            // Add camera slots
            cameraSlotsNode.ItemChanged += CamerasSlotsChanged;
            foreach (var cameraSlot in (IEnumerable<SceneCameraSlot>)cameraSlotsNode.Retrieve())
                CameraSlots.Add(new GraphicsCompositorCameraSlotsViewModel(this, cameraSlot));

            // Update property grid on selected events
            SelectedRenderStages.CollectionChanged += SelectionChanged;
            SelectedRenderFeatures.CollectionChanged += SelectionChanged;
            SelectedCameraSlots.CollectionChanged += SelectionChanged;
            SelectedSharedRenderers.CollectionChanged += SelectionChanged;
            SelectedRendererLinks.CollectionChanged += SelectionChanged;
            SelectedFrameItems.CollectionChanged += SelectionChanged;

            // Keep the Frame tab and the Structure tree in step with the asset
            Frame.Rebuild();
            Structure.Rebuild();
            Asset.PropertyGraph.Changed += AssetChanged;
            Asset.PropertyGraph.ItemChanged += AssetChanged;

            // Make selection scope exclusives
            ServiceProvider.Get<SelectionService>().RegisterSelectionScope(FindFrameItem, obj => (obj as GraphicsCompositorItemViewModel)?.Id, SelectedFrameItems);
            ServiceProvider.Get<SelectionService>().RegisterSelectionScope(id => RenderStages.FirstOrDefault(x => x.Id == id), obj => (obj as RenderStageViewModel)?.Id, SelectedRenderStages);
            ServiceProvider.Get<SelectionService>().RegisterSelectionScope(id => RenderFeatures.FirstOrDefault(x => x.Id == id), obj => (obj as RenderFeatureViewModel)?.Id, SelectedRenderFeatures);
            ServiceProvider.Get<SelectionService>().RegisterSelectionScope(id => CameraSlots.FirstOrDefault(x => x.Id == id), obj => (obj as GraphicsCompositorCameraSlotsViewModel)?.Id, SelectedCameraSlots);
            ServiceProvider.Get<SelectionService>().RegisterSelectionScope(id => Blocks.OfType<GraphicsCompositorBlockViewModel>().FirstOrDefault(x => x.Id == id), obj => (obj as GraphicsCompositorBlockViewModel)?.Id, SelectedSharedRenderers);

            // Update available types for factories
            UpdateAvailableTypes();
            AssemblyRegistry.AssemblyRegistered += AssembliesUpdated;
            AssemblyRegistry.AssemblyUnregistered += AssembliesUpdated;

            return Task.FromResult(true);
        }

        [NotNull]
        internal IGraphicsCompositorLinkViewModel CreateLink([NotNull] IGraphicsCompositorSlotViewModel source, [NotNull] IGraphicsCompositorSlotViewModel target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            return new GraphicsCompositorLinkViewModel(ServiceProvider, (GraphicsCompositorSlotViewModel)source, (GraphicsCompositorSlotViewModel)target);
        }

        public GraphicsCompositorBlockViewModel GetBlock(object value)
        {
            if (value == null)
                return null;

            foreach (var block in Blocks)
            {
                var sharedRenderedBlock = block as SharedRendererBlockViewModel;
                if (sharedRenderedBlock?.GetSharedRenderer() == value)
                {
                    return sharedRenderedBlock;
                }
            }

            return null;
        }

        private async void SelectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var selectedItems = ((IEnumerable<object>)sender).OfType<IPropertyProviderViewModel>().ToList();

            var selected = ValueOf(((IEnumerable<object>)sender).FirstOrDefault());
            if (selected is not null || sender != SelectedFrameItems)
            {
                var source = sender == SelectedFrameItems ? frameSelectionSource
                    : sender == SelectedSharedRenderers || sender == SelectedRendererLinks ? "Graph" : "Lists";
                Frame.Highlight(selected);
                SelectionBanner = selected is null ? null : $"Selected in {source}: {Describe(selected)}";
            }

            EditorProperties.UpdateTypeAndName(selectedItems, x => ComputeSelectionName(x, false), x => x.ToString(), ComputeSelectionName(selectedItems.FirstOrDefault(), true));
            await EditorProperties.GenerateSelectionPropertiesAsync(selectedItems);
        }

        private static object ValueOf(object item) => item switch
        {
            RenderFeatureViewModel feature => feature.RenderFeature,
            RenderStageViewModel stage => stage.RenderStage,
            GraphicsCompositorCameraSlotsViewModel slot => slot.CameraSlot,
            SharedRendererBlockViewModel block => block.GetSharedRenderer(),
            CompositorObjectViewModel value => value.Value,
            _ => null,
        };

        private object FindFrameItem(AbsoluteId id)
        {
            return RenderFeatures.FirstOrDefault(x => x.Id == id) ?? RenderStages.FirstOrDefault(x => x.Id == id)
                ?? CameraSlots.FirstOrDefault(x => x.Id == id) ?? (object)objectItems.Values.FirstOrDefault(x => x.Id == id);
        }

        // One line on where the selection sits in the frame of the shown entry point
        private string Describe(object selected)
        {
            var lanes = Frame.Lanes;
            var name = CompositorFrameViewModel.NameOf(selected);
            switch (selected)
            {
                case RootRenderFeature feature:
                {
                    var stages = lanes.Where(l => l.Chips.Any(c => c.Routed.Feature == feature)).SelectMany(l => l.Step.Stages).Select(s => s.Name).Distinct().ToList();
                    var where = stages.Count == 0 ? $"draws into no stage the {Frame.Entry} entry runs" : $"draws into {string.Join(", ", stages)}";
                    var shared = stages.Count > 1 ? "; its sort key orders it in each of them" : "";
                    return $"{name} · render feature · sort key {feature.SortKey} · {where}{shared}";
                }
                case RenderStage stage:
                {
                    var stageLanes = lanes.Where(l => l.Step.Stages.Contains(stage)).ToList();
                    var features = stageLanes.SelectMany(l => l.Chips).Select(c => c.Routed.Feature).Distinct().Count();
                    var drawnBy = stageLanes.Select(l => CompositorFrameViewModel.NameOf(l.Step.Owner)).Distinct().ToList();
                    return $"{name} · render stage · fed by {features} feature(s) · " + (drawnBy.Count == 0 ? $"not drawn by the {Frame.Entry} entry" : $"drawn by {string.Join(", ", drawnBy)}");
                }
                case SceneCameraSlot slot:
                {
                    var setBy = lanes.Where(l => l.Step.Camera.Slot == slot).Select(l => CompositorFrameViewModel.NameOf(l.Step.Camera.SetBy)).Distinct().ToList();
                    return $"{name} · camera slot · " + (setBy.Count == 0 ? $"not used by the {Frame.Entry} entry" : $"set by {string.Join(", ", setBy)}");
                }
                case ISceneRenderer renderer:
                {
                    var lane = lanes.FirstOrDefault(l => l.Step.OwnerPath.Contains(renderer));
                    var path = lane is null ? $"not run by the {Frame.Entry} entry" : string.Join(" › ", lane.Step.OwnerPath.TakeWhile(r => r != renderer).Append(renderer).Select(CompositorFrameViewModel.NameOf));
                    return $"{name} · renderer · {path}";
                }
                default:
                    return name;
            }
        }

        private void AssetChanged(object sender, EventArgs e)
        {
            // Many nodes change in one edit; rebuild once
            if (rebuildPending)
                return;

            rebuildPending = true;
            Dispatcher.LowPriorityInvokeAsync(() =>
            {
                rebuildPending = false;
                Frame.Rebuild();
                Structure.Rebuild();
            });
        }

        private static string ComputeSelectionName(object selectedObject, bool plural)
        {
            if (selectedObject is SharedRendererBlockViewModel)
                return !plural ? "Shared renderer" : "shared renderers";
            if (selectedObject is EntryPointBlockViewModel)
                return !plural ? "Entry points" : "entry points";
            if (selectedObject is IGraphicsCompositorLinkViewModel)
                return !plural ? "Renderer link" : "renderer links";
            if (selectedObject is RenderStageViewModel)
                return !plural ? "Render stage" : "render stages";
            if (selectedObject is RenderFeatureViewModel)
                return !plural ? "Render feature" : "render features";
            if (selectedObject is GraphicsCompositorCameraSlotsViewModel)
                return !plural ? "Camera slot" : "camera slots";
            if (selectedObject is CompositorObjectViewModel)
                return !plural ? "Compositor item" : "compositor items";

            return null;
        }


        public override void Destroy()
        {
            ServiceProvider.Get<SelectionService>().UnregisterSelectionScope(SelectedRenderStages);
            ServiceProvider.Get<SelectionService>().UnregisterSelectionScope(SelectedRenderFeatures);
            ServiceProvider.Get<SelectionService>().UnregisterSelectionScope(SelectedCameraSlots);
            ServiceProvider.Get<SelectionService>().UnregisterSelectionScope(SelectedSharedRenderers);
            ServiceProvider.Get<SelectionService>().UnregisterSelectionScope(SelectedFrameItems);
            SelectedFrameItems.CollectionChanged -= SelectionChanged;
            Asset.PropertyGraph.Changed -= AssetChanged;
            Asset.PropertyGraph.ItemChanged -= AssetChanged;

            SelectedRenderStages.CollectionChanged -= SelectionChanged;
            SelectedRenderFeatures.CollectionChanged -= SelectionChanged;
            SelectedCameraSlots.CollectionChanged -= SelectionChanged;
            SelectedSharedRenderers.CollectionChanged -= SelectionChanged;
            SelectedRendererLinks.CollectionChanged -= SelectionChanged;

            sharedRenderersNode.ItemChanged -= SharedRenderersChanged;
            renderStagesNode.ItemChanged -= RenderStagesChanged;
            renderFeaturesNode.ItemChanged -= RenderFeaturesChanged;
            cameraSlotsNode.ItemChanged -= CamerasSlotsChanged;

            AssemblyRegistry.AssemblyRegistered -= AssembliesUpdated;
            AssemblyRegistry.AssemblyUnregistered -= AssembliesUpdated;

            Blocks.Cast<GraphicsCompositorBlockViewModel>().ForEach(x => x.Destroy());
            base.Destroy();
        }

        private void DeleteSelection()
        {
            // Remove anything selected (should be only one type of elements at a given moment)
            RemoveSelectedSharedRenderers();
            RemoveSelectedRenderFeatures();
            RemoveSelectedRenderStages();
            RemoveSelectedCameraSlots();
            RemoveSelectedLinks();
        }

        private void RemoveSelectedLinks()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var toRemove = SelectedRendererLinks.Cast<GraphicsCompositorLinkViewModel>().ToList();
                // Clear selection first, so we don't try to unselect items that don't exist anymore later.
                SelectedRendererLinks.Clear();

                foreach (var viewModel in toRemove)
                {
                    viewModel.SourceSlot.ClearLink();
                    viewModel.Destroy();
                    viewModel.SourceSlot.Links.Remove(viewModel);
                    viewModel.TargetSlot.Links.Remove(viewModel);
                }

                UndoRedoService.SetName(transaction, "Delete link(s)");
            }
        }

        private void RemoveSelectedSharedRenderers()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var toRemove = SelectedSharedRenderers.OfType<SharedRendererBlockViewModel>().ToList();
                // Clear selection first, so we don't try to unselect items that don't exist anymore later.
                SelectedSharedRenderers.Clear();

                foreach (var viewModel in toRemove)
                {
                    // Find index
                    var index = Asset.Asset.SharedRenderers.IndexOf(viewModel.GetSharedRenderer());
                    if (index < 0)
                        continue;

                    // Remove
                    var itemIndex = new NodeIndex(index);
                    sharedRenderersNode.Remove(viewModel.GetSharedRenderer(), itemIndex);
                }

                UndoRedoService.SetName(transaction, "Delete renderer(s)");
            }
        }

        private void AddNewRenderStage()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var renderStage = new RenderStage();
                renderStagesNode.Add(renderStage);

                UndoRedoService.SetName(transaction, "Create new render stage");
            }
        }

        private void RemoveSelectedRenderStages()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var toRemove = SelectedRenderStages.Cast<RenderStageViewModel>().Select(x => x.RenderStage).ToList();
                // Clear selection first, so we don't try to unselect items that don't exist anymore later.
                SelectedRenderStages.Clear();

                foreach (var renderStage in toRemove)
                {
                    // Find index
                    var index = Asset.Asset.RenderStages.IndexOf(renderStage);
                    if (index < 0)
                        continue;

                    // Clear references to this object
                    Asset.PropertyGraph.ClearReferencesToObjects(renderStage.Id.Yield());

                    // Remove
                    var itemIndex = new NodeIndex(index);
                    renderStagesNode.Remove(renderStage, itemIndex);
                }

                UndoRedoService.SetName(transaction, "Delete render stage(s)");
            }
        }

        private void AddNewCameraSlot()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var cameraSlot = new SceneCameraSlot();
                cameraSlotsNode.Add(cameraSlot);

                UndoRedoService.SetName(transaction, "Create new camera slot");
            }
        }

        private void RemoveSelectedCameraSlots()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var toRemove = SelectedCameraSlots.Cast<GraphicsCompositorCameraSlotsViewModel>().Select(x => x.CameraSlot).ToList();
                // Clear selection first, so we don't try to unselect items that don't exist anymore later.
                SelectedCameraSlots.Clear();

                foreach (var viewModel in toRemove)
                {
                    // Find index
                    var index = Asset.Asset.Cameras.IndexOf(viewModel);
                    if (index < 0)
                        continue;

                    // Do not clear references to this object - camera slots in the scene asset should not change automatically!

                    // Remove
                    var itemIndex = new NodeIndex(index);
                    cameraSlotsNode.Remove(viewModel, itemIndex);
                }

                UndoRedoService.SetName(transaction, "Delete camera slot(s)");
            }
        }

        private void AssembliesUpdated(object sender, AssemblyRegisteredEventArgs e)
        {
            UpdateAvailableTypes();
        }

        private void AddNewRenderFeature(AbstractNodeType abstractNodeType)
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var renderFeature = abstractNodeType.GenerateValue(null);
                renderFeaturesNode.Add(renderFeature);

                UndoRedoService.SetName(transaction, "Create new render feature");
            }
        }

        private async Task AddPackageRenderFeatures()
        {
            var asset = (GraphicsCompositorAsset)Asset.Asset;
            var opaqueStage = asset.RenderStages.FirstOrDefault(x => x.Name == "Opaque");
            var transparentStage = asset.RenderStages.FirstOrDefault(x => x.Name == "Transparent");
            if (opaqueStage == null || transparentStage == null)
            {
                await ServiceProvider.Get<IDialogService>().MessageBoxAsync(Tr._p("Message", "The package render features need an Opaque and a Transparent render stage to render into."), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Through the property graph, so the view models, the undo stack and the dirty flag follow
            var scope = PackageTypeScope.For(Asset.AssetItem.Package);
            var renderFeatures = RenderFeatureProviders.CreateMissingRenderFeatures(asset, opaqueStage, transparentStage, scope).ToList();
            if (renderFeatures.Count == 0)
            {
                await ServiceProvider.Get<IDialogService>().MessageBoxAsync(Tr._p("Message", "The compositor already has every render feature the packages of its project provide."), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                foreach (var renderFeature in renderFeatures)
                    renderFeaturesNode.Add(renderFeature);

                UndoRedoService.SetName(transaction, "Add package render features");
            }
        }

        private void RemoveSelectedRenderFeatures()
        {
            using (var transaction = UndoRedoService.CreateTransaction())
            {
                var toRemove = SelectedRenderFeatures.Cast<RenderFeatureViewModel>().Select(x => x.RenderFeature).ToList();
                // Clear selection first, so we don't try to unselect items that don't exist anymore later.
                SelectedRenderFeatures.Clear();

                foreach (var renderFeature in toRemove)
                {
                    // Find index
                    var index = Asset.Asset.RenderFeatures.IndexOf(renderFeature);
                    if (index < 0)
                        continue;

                    // Remove
                    var itemIndex = new NodeIndex(index);
                    renderFeaturesNode.Remove(renderFeature, itemIndex);
                }

                UndoRedoService.SetName(transaction, "Delete render feature(s)");
            }
        }

        private void SharedRenderersChanged(object sender, ItemChangeEventArgs e)
        {
            switch (e.ChangeType)
            {
                case ContentChangeType.None:
                    break;

                case ContentChangeType.CollectionAdd:
                    {
                        var sharedRenderer = (ISharedRenderer)e.NewValue;
                        AddSharedRendererViewModel(sharedRenderer);
                        // Make sure to rebuild all outgoing links, if the new renderer is already connected to the rest of the graph
                        foreach (var block in Blocks.OfType< GraphicsCompositorBlockViewModel>())
                        {
                            block.UpdateOutgoingLinks();
                        }
                        break;
                    }
                case ContentChangeType.CollectionRemove:
                    {
                        var sharedRenderer = (ISharedRenderer)e.OldValue;
                        RemoveSharedRendererViewModel(sharedRenderer);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void RenderStagesChanged(object sender, ItemChangeEventArgs e)
        {
            switch (e.ChangeType)
            {
                case ContentChangeType.None:
                    break;

                case ContentChangeType.CollectionAdd:
                    {
                        RenderStages.Insert(e.Index.Int, new RenderStageViewModel(this, (RenderStage)e.NewValue));
                        break;
                    }
                case ContentChangeType.CollectionRemove:
                    {
                        RenderStages.RemoveAt(e.Index.Int);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void RenderFeaturesChanged(object sender, ItemChangeEventArgs e)
        {
            switch (e.ChangeType)
            {
                case ContentChangeType.None:
                    break;

                case ContentChangeType.CollectionAdd:
                    {
                        RenderFeatures.Insert(e.Index.Int, new RenderFeatureViewModel(this, (RenderFeature)e.NewValue));
                        break;
                    }
                case ContentChangeType.CollectionRemove:
                    {
                        RenderFeatures.RemoveAt(e.Index.Int);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void CamerasSlotsChanged(object sender, ItemChangeEventArgs e)
        {
            switch (e.ChangeType)
            {
                case ContentChangeType.None:
                    break;

                case ContentChangeType.CollectionAdd:
                    {
                        CameraSlots.Insert(e.Index.Int, new GraphicsCompositorCameraSlotsViewModel(this, (SceneCameraSlot)e.NewValue));
                        break;
                    }
                case ContentChangeType.CollectionRemove:
                    {
                        CameraSlots.ElementAt(e.Index.Int).Dispose();
                        CameraSlots.RemoveAt(e.Index.Int);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void AddSharedRendererViewModel(ISharedRenderer sharedRenderer)
        {
            var viewModel = new SharedRendererBlockViewModel(this, sharedRenderer);
            viewModel.Initialize();
            viewModel.UpdateSlots();
            Blocks.Add(viewModel);
        }

        private void RemoveSharedRendererViewModel(ISharedRenderer sharedRenderer)
        {
            // Clear references to this object
            Asset.PropertyGraph.ClearReferencesToObjects(sharedRenderer.Id.Yield());

            var block = GetBlock(sharedRenderer);
            Blocks.Remove(block);
            block.Destroy();
        }

        private void UpdateAvailableTypes()
        {
            RenderFeatureTypes.Clear();
            RenderFeatureTypes.AddRange(AbstractNodeType.GetInheritedInstantiableTypes(typeof(RootRenderFeature)));

            var sharedRendererFactories = new List<SharedRendererFactoryViewModel>();
            foreach (var rendererType in typeof(ISharedRenderer).GetInheritedInstantiableTypes())
            {
                if (TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(rendererType) != null)
                    continue;
                if (TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ObsoleteAttribute>(rendererType) != null)
                    continue;

                sharedRendererFactories.Add(new SharedRendererFactoryViewModel(ServiceProvider, sharedRenderersNode, rendererType));
            }
            sharedRendererFactories.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));

            SharedRendererFactories.Clear();
            SharedRendererFactories.AddRange(sharedRendererFactories);
        }
    }
}

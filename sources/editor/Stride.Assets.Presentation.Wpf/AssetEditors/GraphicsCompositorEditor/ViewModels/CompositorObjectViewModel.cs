// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Annotations;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Quantum;

namespace Stride.Assets.Presentation.AssetEditors.GraphicsCompositorEditor.ViewModels
{
    /// <summary>
    /// Any object of the compositor asset, such as an inline renderer or a sub render feature, shown in the property grid
    /// at its path in the asset.
    /// </summary>
    public sealed class CompositorObjectViewModel : GraphicsCompositorItemViewModel
    {
        private readonly IObjectNode node;

        public CompositorObjectViewModel([NotNull] GraphicsCompositorEditorViewModel editor, [NotNull] object value, string title) : base(editor)
        {
            Value = value;
            Title = title;
            node = editor.Session.AssetNodeContainer.GetOrCreateNode(value);
        }

        public object Value { get; }

        public string Title { get; }

        /// <inheritdoc/>
        public override IObjectNode GetRootNode() => node;

        /// <inheritdoc/>
        protected override GraphNodePath GetNodePath()
        {
            // The first path the asset reaches the object by without crossing an object reference, such as SharedRenderers[i] for a shared renderer
            var root = Editor.Session.AssetNodeContainer.GetNode(Editor.Asset.Asset);
            GraphNodePath found = null;
            var visitor = new AssetGraphVisitorBase(Editor.Asset.PropertyGraph.Definition);
            visitor.Visiting += (visited, path) =>
            {
                if (found is null && visited == node)
                    found = path.Clone();
            };
            visitor.Visit(root);
            return found ?? new GraphNodePath(root);
        }

        public override string ToString() => Title;
    }
}

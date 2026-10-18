// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.IO;
using System.Linq;
using System.Text;
using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Rendering;
using Stride.Rendering.Background;
using Xunit;

namespace Stride.Assets.Tests
{
    /// <summary>
    /// A compositor asset sets <see cref="RootRenderFeature.SortKey"/>; only keys that differ from their feature type's default are written.
    /// </summary>
    public class TestRootRenderFeatureSortKey
    {
        [Fact]
        public void DefaultSortKeysAreNotWrittenAndLoadBackAsTheTypeDefaults()
        {
            var (yaml, loaded) = RoundTrip(new MeshRenderFeature(), new BackgroundRenderFeature());

            Assert.DoesNotContain("SortKey", yaml);
            Assert.Equal(128, loaded.RenderFeatures.OfType<MeshRenderFeature>().Single().SortKey);
            Assert.Equal(192, loaded.RenderFeatures.OfType<BackgroundRenderFeature>().Single().SortKey);
        }

        [Fact]
        public void ChangedSortKeyIsWrittenAndLoadedBack()
        {
            var (yaml, loaded) = RoundTrip(new MeshRenderFeature { SortKey = 40 }, new BackgroundRenderFeature { SortKey = 128 });

            Assert.Contains("SortKey: 40", yaml);
            Assert.Contains("SortKey: 128", yaml);
            Assert.Equal(40, loaded.RenderFeatures.OfType<MeshRenderFeature>().Single().SortKey);
            Assert.Equal(128, loaded.RenderFeatures.OfType<BackgroundRenderFeature>().Single().SortKey);
        }

        private static (string Yaml, GraphicsCompositorAsset Loaded) RoundTrip(params RootRenderFeature[] renderFeatures)
        {
            var compositor = new GraphicsCompositorAsset();
            compositor.RenderFeatures.AddRange(renderFeatures);

            using var stream = new MemoryStream();
            AssetFileSerializer.Save(stream, compositor, null);
            var yaml = Encoding.UTF8.GetString(stream.ToArray());

            var loaded = AssetFileSerializer.Load<GraphicsCompositorAsset>(new MemoryStream(Encoding.UTF8.GetBytes(yaml)), "Compositor.sdgfxcomp").Asset;
            return (yaml, loaded);
        }
    }
}

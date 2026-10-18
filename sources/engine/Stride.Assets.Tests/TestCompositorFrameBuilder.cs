// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Linq;
using Stride.Assets.Rendering;
using Stride.Assets.Rendering.Frame;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Background;
using Stride.Rendering.Compositing;
using Stride.Rendering.Images;
using Stride.Rendering.Lights;
using Stride.Rendering.Shadows;
using Stride.Rendering.Sprites;
using Xunit;

namespace Stride.Assets.Tests
{
    /// <summary>
    /// The frame worked out from a compositor asset follows what its renderers run, in the order they run it.
    /// </summary>
    public class TestCompositorFrameBuilder
    {
        [Fact]
        public void GameEntryRunsTheForwardRendererStepsInOrderThenItsSibling()
        {
            var frame = CompositorFrameBuilder.Build(new Fixture().Asset, CompositorEntry.Game);

            Assert.Equal(["Shadow maps", "Clear", "Z-prepass", "Opaque", "Transparent", "Post effects", "Debug"], frame.Steps.Select(s => s.Title));
        }

        [Fact]
        public void ZPrepassNeedsLightProbesAndAGBufferStage()
        {
            var fixture = new Fixture();
            fixture.Forward.LightProbes = false;

            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);

            Assert.DoesNotContain(frame.Steps, s => s.Kind == FrameStepKind.ZPrepass);
        }

        [Fact]
        public void LowerSortKeyDrawsFirstWithinAStage()
        {
            var fixture = new Fixture();
            fixture.Sprite.SortKey = 90;

            var transparent = Step(CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game), "Transparent");

            Assert.Equal([(byte)90, (byte)128], transparent.FeatureGroups.Select(g => g.SortKey));
            Assert.Same(fixture.Sprite, Assert.Single(transparent.FeatureGroups[0].Features).Feature);
        }

        [Fact]
        public void TransparentSelectorRoutesIntoBothStagesAndBackgroundOnlyIntoOpaque()
        {
            var fixture = new Fixture();
            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);

            var opaque = Step(frame, "Opaque").FeatureGroups.SelectMany(g => g.Features).ToList();
            var transparent = Step(frame, "Transparent").FeatureGroups.SelectMany(g => g.Features).ToList();
            Assert.Contains(opaque, r => r.Feature == fixture.Mesh && r.Condition == "objects that aren't transparent");
            Assert.Contains(transparent, r => r.Feature == fixture.Mesh && r.Condition == "transparent objects");
            Assert.Contains(opaque, r => r.Feature is BackgroundRenderFeature);
            Assert.DoesNotContain(transparent, r => r.Feature is BackgroundRenderFeature);
            Assert.Equal([(byte)128, (byte)192], Step(frame, "Opaque").FeatureGroups.Select(g => g.SortKey));
        }

        [Fact]
        public void ShadowMapsDrawTheShadowCasterStage()
        {
            var fixture = new Fixture();
            var shadows = Assert.Single(CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game).Steps, s => s.Kind == FrameStepKind.ShadowMaps);

            Assert.Same(fixture.ShadowCaster, Assert.Single(shadows.Stages));
            Assert.Contains(shadows.FeatureGroups.SelectMany(g => g.Features), r => r.Feature == fixture.Mesh && r.Condition == "shadow casters");
        }

        [Fact]
        public void EveryStepUnderTheCameraRendererDrawsFromItsSlot()
        {
            var fixture = new Fixture();
            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);

            Assert.All(frame.Steps, s =>
            {
                Assert.Same(fixture.Slot, s.Camera.Slot);
                Assert.Same(fixture.Asset.Game, s.Camera.SetBy);
            });
        }

        [Fact]
        public void RenderTextureRendererSwitchesTheTargetOfItsChildren()
        {
            var fixture = new Fixture();
            var texture = AttachedReferenceManager.CreateProxyObject<Texture>(AssetId.New(), "Textures/Minimap");
            var collection = (SceneRendererCollection)((SceneCameraRenderer)fixture.Asset.Game).Child;
            collection.Children.Insert(0, new RenderTextureSceneRenderer { RenderTexture = texture, Child = new SingleStageRenderer { RenderStage = fixture.Opaque } });

            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);

            Assert.Equal("Textures/Minimap", frame.Steps[0].Target.Name);
            Assert.Same(texture, frame.Steps[0].Target.RenderTexture);
            Assert.All(frame.Steps.Skip(1), s => Assert.Equal("Back buffer", s.Target.Name));
        }

        [Fact]
        public void ForwardRendererTemporariesEndWithItSoTheRendererAfterItHasNoDepth()
        {
            var fixture = new Fixture();
            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);
            var titles = frame.Steps.Select(s => s.Title).ToList();

            var depth = Assert.Single(frame.Resources, r => r.Kind == FrameResourceKind.Depth);
            Assert.Same(fixture.Forward, depth.Owner);
            Assert.Equal(titles.IndexOf("Clear"), depth.FirstStep);
            Assert.Equal(titles.IndexOf("Post effects"), depth.LastStep);
            Assert.True(titles.IndexOf("Debug") > depth.LastStep);

            var colour = Assert.Single(frame.Resources, r => r.Kind == FrameResourceKind.Colour);
            Assert.Equal("HDR colour", colour.Name);
            Assert.Equal(titles.IndexOf("Opaque"), colour.FirstStep);

            var atlas = Assert.Single(frame.Resources, r => r.Kind == FrameResourceKind.ShadowAtlas);
            Assert.Equal((titles.IndexOf("Shadow maps"), titles.IndexOf("Transparent")), (atlas.FirstStep, atlas.LastStep));
        }

        [Fact]
        public void WithoutAnOpaqueStageTheForwardRendererStopsAfterTransparent()
        {
            var fixture = new Fixture();
            fixture.Forward.OpaqueRenderStage = null;

            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Game);

            Assert.DoesNotContain(frame.Steps, s => s.Kind == FrameStepKind.PostEffects);
            Assert.DoesNotContain(frame.Resources, r => r.Kind == FrameResourceKind.Colour);
        }

        [Fact]
        public void EditorEntryDrawsFromTheEditorCamera()
        {
            var fixture = new Fixture();
            fixture.Asset.Editor = fixture.Forward;

            var frame = CompositorFrameBuilder.Build(fixture.Asset, CompositorEntry.Editor);

            Assert.All(frame.Steps, s => Assert.Equal("Editor camera", s.Camera.Name));
            Assert.NotNull(frame.EntryNote);
        }

        private static FrameStep Step(CompositorFrame frame, string title) => Assert.Single(frame.Steps, s => s.Title == title);

        // In the shape of DefaultGraphicsCompositorLevel10, with one shadow stage and a sprite feature routed into Transparent
        private sealed class Fixture
        {
            public readonly GraphicsCompositorAsset Asset = new();
            public readonly RenderStage Opaque = new("Opaque", "Main");
            public readonly RenderStage Transparent = new("Transparent", "Main");
            public readonly RenderStage ShadowCaster = new("ShadowMapCaster", "ShadowMapCaster");
            public readonly RenderStage GBuffer = new("GBuffer", "GBuffer");
            public readonly RenderStage Debug = new("Debug", "Main");
            public readonly SceneCameraSlot Slot = new() { Name = "Main" };
            public readonly MeshRenderFeature Mesh = new();
            public readonly SpriteRenderFeature Sprite = new();
            public readonly ForwardRenderer Forward;

            public Fixture()
            {
                Asset.RenderStages.Add(Opaque);
                Asset.RenderStages.Add(Transparent);
                Asset.RenderStages.Add(ShadowCaster);
                Asset.RenderStages.Add(GBuffer);
                Asset.RenderStages.Add(Debug);
                Asset.Cameras.Add(Slot);

                Mesh.RenderStageSelectors.Add(new MeshTransparentRenderStageSelector { OpaqueRenderStage = Opaque, TransparentRenderStage = Transparent });
                Mesh.RenderStageSelectors.Add(new ShadowMapRenderStageSelector { ShadowMapRenderStage = ShadowCaster });
                Mesh.RenderStageSelectors.Add(new MeshTransparentRenderStageSelector { OpaqueRenderStage = GBuffer });
                var shadowMapRenderer = new ShadowMapRenderer();
                shadowMapRenderer.Renderers.Add(new LightDirectionalShadowMapRenderer { ShadowCasterRenderStage = ShadowCaster });
                Mesh.RenderFeatures.Add(new ForwardLightingRenderFeature { ShadowMapRenderer = shadowMapRenderer });
                Asset.RenderFeatures.Add(Mesh);

                var background = new BackgroundRenderFeature();
                background.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector { RenderStage = Opaque });
                Asset.RenderFeatures.Add(background);

                Sprite.RenderStageSelectors.Add(new SimpleGroupToRenderStageSelector { RenderStage = Transparent });
                Asset.RenderFeatures.Add(Sprite);

                Forward = new ForwardRenderer
                {
                    OpaqueRenderStage = Opaque,
                    TransparentRenderStage = Transparent,
                    GBufferRenderStage = GBuffer,
                    PostEffects = new PostProcessingEffects(),
                };
                Forward.ShadowMapRenderStages.Add(ShadowCaster);
                var debug = new DebugRenderer();
                debug.DebugRenderStages.Add(Debug);

                Asset.Game = new SceneCameraRenderer { Camera = Slot, Child = new SceneRendererCollection { Children = { Forward, debug } } };
            }
        }
    }
}

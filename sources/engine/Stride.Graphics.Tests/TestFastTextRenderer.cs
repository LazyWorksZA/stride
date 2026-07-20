// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Xunit;

using Stride.Graphics.Regression;

namespace Stride.Graphics.Tests
{
    public class TestFastTextRenderer : GameTestBase
    {
        public TestFastTextRenderer()
        {
            GraphicsDeviceManager.PreferredGraphicsProfile = [ GraphicsProfile.Level_10_0 ];
        }

        [Fact]
        public void IndexBufferIsCorrectlyPopulated()
        {
            PerformTest(game =>
            {
                const int maxCharacters = 10;
                using var renderer = new FastTextRenderer(game.GraphicsContext, maxCharacters);

                var indices = renderer.IndexBuffer.GetData<int>(game.GraphicsContext.CommandList);

                // 6 indices (two triangles) per character quad.
                Assert.Equal(maxCharacters * 6, indices.Length);
                for (var c = 0; c < maxCharacters; c++)
                {
                    var b = c * 6;
                    Assert.Equal(c * 4 + 0, indices[b + 0]);
                    Assert.Equal(c * 4 + 1, indices[b + 1]);
                    Assert.Equal(c * 4 + 2, indices[b + 2]);
                    Assert.Equal(c * 4 + 1, indices[b + 3]);
                    Assert.Equal(c * 4 + 3, indices[b + 4]);
                    Assert.Equal(c * 4 + 2, indices[b + 5]);
                }
            });
        }
    }
}

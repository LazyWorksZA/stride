// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Generic;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Compositing;

namespace Stride.Assets.Rendering.Frame;

/// <summary>
/// An entry point of a <see cref="GraphicsCompositorAsset"/>.
/// </summary>
public enum CompositorEntry
{
    Game,
    SingleView,
    Editor,
}

public enum FrameStepKind
{
    ShadowMaps,
    Clear,
    ZPrepass,
    Stage,
    SubsurfaceScattering,
    MsaaResolve,
    LightShafts,
    PostEffects,
    Unknown,
}

public enum FrameResourceKind
{
    ShadowAtlas,
    Depth,
    Colour,
}

/// <summary>
/// The camera a step draws from.
/// </summary>
/// <param name="Slot">The compositor camera slot, or <c>null</c> when the camera comes from outside the compositor.</param>
/// <param name="Name">What the camera is called in the editor.</param>
/// <param name="SetBy">The renderer that sets it, or <c>null</c> when the entry point supplies it.</param>
public sealed record FrameCamera(SceneCameraSlot Slot, string Name, ISceneRenderer SetBy);

/// <summary>
/// Where a step draws to.
/// </summary>
/// <param name="Name">What the target is called in the editor.</param>
/// <param name="RenderTexture">The texture a <see cref="RenderTextureSceneRenderer"/> draws into, or <c>null</c> for the entry point's output.</param>
public sealed record FrameTarget(string Name, Texture RenderTexture);

/// <summary>
/// A root render feature that one of its selectors routes into a stage.
/// </summary>
/// <param name="Condition">Which of the feature's render objects the selector sends there.</param>
public sealed record RoutedFeature(RootRenderFeature Feature, RenderStageSelector Selector, string Condition);

/// <summary>
/// Features that share a sort key. The engine orders their draws by distance and state, not by feature.
/// </summary>
public sealed record FeatureGroup(byte SortKey, IReadOnlyList<RoutedFeature> Features);

/// <summary>
/// One thing the compositor does, in the order it runs.
/// </summary>
public sealed class FrameStep
{
    public FrameStepKind Kind { get; init; }

    public string Title { get; init; }

    /// <summary>
    /// The renderer that runs this step.
    /// </summary>
    public ISceneRenderer Owner { get; init; }

    /// <summary>
    /// The renderers from the entry point down to <see cref="Owner"/>.
    /// </summary>
    public IReadOnlyList<ISceneRenderer> OwnerPath { get; init; }

    public FrameCamera Camera { get; init; }

    public FrameTarget Target { get; init; }

    /// <summary>
    /// The stages this step draws: one for a stage or Z-prepass, several for shadow maps.
    /// </summary>
    public IReadOnlyList<RenderStage> Stages { get; init; } = [];

    /// <summary>
    /// The features routed into <see cref="Stages"/>, lowest sort key first.
    /// </summary>
    public IReadOnlyList<FeatureGroup> FeatureGroups { get; init; } = [];

    /// <summary>
    /// When the step only runs under a condition, what it is.
    /// </summary>
    public string Condition { get; init; }
}

/// <summary>
/// A resource the frame uses over a range of steps.
/// </summary>
/// <param name="Owner">The object that allocates it.</param>
public sealed record FrameResource(FrameResourceKind Kind, string Name, object Owner, int FirstStep, int LastStep);

/// <summary>
/// What an entry point of a compositor runs, as far as the asset alone tells.
/// </summary>
public sealed class CompositorFrame
{
    public CompositorEntry Entry { get; init; }

    /// <summary>
    /// Where the entry point's camera comes from, when the compositor doesn't set it.
    /// </summary>
    public string EntryNote { get; init; }

    public IReadOnlyList<FrameStep> Steps { get; init; }

    public IReadOnlyList<FrameResource> Resources { get; init; }
}

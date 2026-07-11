namespace SplitGM.Core;

public enum RelationshipKind
{
    Calls,
    CalledBy,
    UsesGlobal,
    RoomTransition,
    EventCode,
    CreationCode,
    PreCreateCode,
    UsesAsset,
    ReferencedBy,
    InheritsFrom,
    InheritedBy,
    InstanceOf,
    RoomInstance,
    AssignedSprite,
    CollisionMask,
    LayerAsset,
    HeuristicReference,
    UnusedCandidate
}

public sealed record RelationshipEntry(
    RelationshipKind Kind,
    string Relationship,
    string TargetType,
    string TargetName,
    string Details,
    int? CodeIndex = null,
    ResourceKind? ResourceKind = null,
    int? ResourceIndex = null);

public sealed class RelationshipAnalysisResult
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<RelationshipEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ConnectedCodeInfo(
    int CodeIndex,
    string CodeName,
    CodeCategory Category,
    string Relationship,
    string Details);

public sealed record UnusedResourceCandidate(
    ResourceKind ResourceKind,
    int ResourceIndex,
    string Name,
    string Reason,
    string Confidence);

public sealed class UnusedResourceReport
{
    public required string Summary { get; init; }
    public IReadOnlyList<UnusedResourceCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

namespace SplitGM.Core;

public enum ResourcePreviewKind
{
    None,
    Image,
    Sprite,
    Room,
    Audio,
    Text,
    Object,
    Path,
    Generic
}

public sealed class ResourcePreviewData
{
    public required ResourceKind ResourceKind { get; init; }
    public required int ResourceIndex { get; init; }
    public required string Name { get; init; }
    public required ResourcePreviewKind PreviewKind { get; init; }
    public required string Details { get; init; }
    public string? Subtitle { get; init; }
    public byte[]? ImagePng { get; init; }
    public SpritePreviewInfo? Sprite { get; init; }
    public RoomPreviewInfo? Room { get; init; }
    public ObjectPreviewInfo? Object { get; init; }
    public AudioPreviewInfo? Audio { get; init; }
    public string? Text { get; init; }
}

public sealed record SpritePreviewInfo(
    int FrameIndex,
    int FrameCount,
    int Width,
    int Height,
    int OriginX,
    int OriginY,
    string SpriteType);

public sealed record AudioPreviewInfo(
    string Format,
    string Extension,
    string Source,
    string AudioGroup,
    int GroupId,
    int AudioId,
    long DataLength,
    bool DataAvailable,
    string? ExternalPath);

public sealed record AudioPayload(
    byte[] Data,
    string Format,
    string Extension,
    string Source,
    string AudioGroup,
    int GroupId,
    int AudioId,
    string SuggestedFileName);


public sealed class ObjectPreviewInfo
{
    public string? SpriteName { get; init; }
    public string? ParentObjectName { get; init; }
    public string? CollisionMaskName { get; init; }
    public bool Visible { get; init; }
    public bool Solid { get; init; }
    public bool Persistent { get; init; }
    public int Depth { get; init; }
    public IReadOnlyList<ObjectEventInfo> Events { get; init; } = [];
}

public sealed record ObjectEventInfo(
    string EventType,
    uint Subtype,
    string CodeName,
    int ActionCount,
    int CodeIndex);

public sealed class RoomPreviewInfo
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint Speed { get; init; }
    public bool Persistent { get; init; }
    public IReadOnlyList<RoomLayerInfo> Layers { get; init; } = [];
    public IReadOnlyList<RoomInstanceInfo> Instances { get; init; } = [];
    public IReadOnlyList<RoomTileInfo> Tiles { get; init; } = [];
    public int RenderedObjectCount { get; init; }
    public int SkippedObjectCount { get; init; }
}

public sealed record RoomLayerInfo(
    int Index,
    string Name,
    string Type,
    int Depth,
    bool Visible,
    float XOffset,
    float YOffset,
    int ItemCount);

public sealed record RoomInstanceInfo(
    uint InstanceId,
    string ObjectName,
    string LayerName,
    int X,
    int Y,
    float ScaleX,
    float ScaleY,
    float Rotation,
    int ImageIndex,
    string? SpriteName,
    string? CreationCodeName,
    int ObjectResourceIndex);

public sealed record RoomTileInfo(
    uint InstanceId,
    string AssetName,
    string LayerName,
    int X,
    int Y,
    int SourceX,
    int SourceY,
    uint Width,
    uint Height,
    float ScaleX,
    float ScaleY,
    int Depth);

public sealed record ResourceExportResult(
    string OutputPath,
    ResourceKind ResourceKind,
    int ResourceIndex,
    string ResourceName,
    int FilesWritten,
    long BytesWritten);

public sealed record ResourceExtractionResult(
    int ResourcesProcessed,
    int FilesWritten,
    int FailedResources,
    long BytesWritten,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings);

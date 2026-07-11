namespace SplitGM.Core;

public sealed record DecompileOptions(
    string InputPath,
    string OutputDirectory,
    bool OverwriteOutput = false,
    bool ExportAssembly = true,
    bool ExportResourceIndexes = true,
    bool ExportResources = true);

public sealed record ProjectExportOptions(
    string OutputDirectory,
    bool OverwriteOutput = false,
    bool ExportAssembly = true,
    bool ExportResourceIndexes = true,
    bool ExportResources = true);

public enum DecompileStage
{
    ResolvingInput,
    LoadingData,
    InspectingGame,
    BuildingResourceIndex,
    DecompilingCode,
    ExportingAssembly,
    WritingManifest,
    ExportingResources,
    RenderingPreview,
    SearchingCode,
    Completed
}

public sealed record DecompileProgress(
    DecompileStage Stage,
    int Completed,
    int Total,
    string Message)
{
    public double Percentage => Total <= 0
        ? 0
        : Math.Clamp((double)Completed / Total * 100.0, 0, 100);
}

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Success
}

public sealed record LogMessage(DateTimeOffset Timestamp, LogLevel Level, string Text)
{
    public static LogMessage Info(string text) => new(DateTimeOffset.Now, LogLevel.Info, text);
    public static LogMessage Warning(string text) => new(DateTimeOffset.Now, LogLevel.Warning, text);
    public static LogMessage Error(string text) => new(DateTimeOffset.Now, LogLevel.Error, text);
    public static LogMessage Success(string text) => new(DateTimeOffset.Now, LogLevel.Success, text);
}

public sealed record DecompileResult(
    string OutputDirectory,
    string ManifestPath,
    int TotalEntries,
    int SuccessfulEntries,
    int FailedEntries,
    int WarningCount,
    TimeSpan Elapsed);

public enum GameCompatibility
{
    Compatible,
    YycNoVmCode,
    UnsupportedBytecode,
    NoCodeEntries,
    Limited
}

public sealed record GameProjectInfo(
    string OriginalInput,
    string ResolvedDataSource,
    string ResolutionMethod,
    string GameName,
    string DisplayName,
    string GameMakerVersion,
    int BytecodeVersion,
    string RuntimeType,
    bool IsYyc,
    bool UnsupportedBytecodeVersion,
    GameCompatibility Compatibility,
    string CompatibilityMessage,
    long InputFileSize,
    DateTimeOffset LoadedAt);

public sealed record ResourceCounts(
    int RootCodeEntries,
    int Scripts,
    int Objects,
    int Rooms,
    int Sprites,
    int Sounds,
    int Fonts,
    int Shaders,
    int Backgrounds,
    int Paths,
    int Timelines,
    int Extensions,
    int AudioGroups,
    int Sequences,
    int AnimationCurves,
    int ParticleSystems,
    int ParticleSystemEmitters,
    int TextureGroups,
    int TexturePageItems,
    int TexturePages,
    int EmbeddedImages,
    int EmbeddedAudio,
    int FilterEffects,
    int Strings,
    int Functions,
    int Variables);

public enum CodeCategory
{
    Scripts,
    ObjectEvents,
    RoomCode,
    GlobalInit,
    Timelines,
    Other
}

public sealed record CodeEntryInfo(
    int Index,
    string Name,
    CodeCategory Category,
    int InstructionCount,
    long EstimatedByteLength,
    int ChildEntryCount,
    int ArgumentsCount,
    int LocalsCount);

public enum ResourceKind
{
    Objects,
    Rooms,
    Sprites,
    Sounds,
    Fonts,
    Shaders,
    Backgrounds,
    Paths,
    Timelines,
    Extensions,
    AudioGroups,
    Sequences,
    AnimationCurves,
    ParticleSystems,
    ParticleSystemEmitters,
    TextureGroups,
    TexturePageItems,
    TexturePages,
    EmbeddedImages,
    EmbeddedAudio,
    FilterEffects,
    Strings,
    Functions,
    Variables
}

public sealed record ResourceEntryInfo(
    ResourceKind Kind,
    int Index,
    string Name);

public sealed record CodeViewResult(
    CodeEntryInfo Entry,
    string Gml,
    string Assembly,
    string Details,
    string? DecompileError,
    string? AssemblyError)
{
    public bool GmlSucceeded => DecompileError is null;
    public bool AssemblySucceeded => AssemblyError is null;
}

public sealed record CodeSearchResult(
    int CodeIndex,
    string CodeName,
    CodeCategory Category,
    int LineNumber,
    string Source,
    string Snippet);

public sealed class SplitGmManifest
{
    public string Product { get; init; } = SplitGmProduct.Name;
    public string Version { get; init; } = SplitGmProduct.Version;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required string OriginalInput { get; init; }
    public required string ResolvedDataSource { get; init; }
    public required string ResolutionMethod { get; init; }
    public string? GameName { get; init; }
    public string? DisplayName { get; init; }
    public string? GameMakerVersion { get; init; }
    public int BytecodeVersion { get; init; }
    public bool IsYyc { get; init; }
    public required ResourceCounts ResourceCounts { get; init; }
    public int TotalRootCodeEntries { get; init; }
    public int SuccessfulEntries { get; init; }
    public int FailedEntries { get; init; }
    public int WarningCount { get; init; }
    public int ResourcesExported { get; init; }
    public int ResourceFilesWritten { get; init; }
    public int ResourceExportFailures { get; init; }
    public long ResourceBytesWritten { get; init; }
    public List<ManifestCodeEntry> CodeEntries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class ManifestCodeEntry
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? GmlPath { get; init; }
    public string? AssemblyPath { get; init; }
    public string? ErrorPath { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

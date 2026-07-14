namespace SplitGM.Core;

public enum ReconstructionStage
{
    Preparing,
    SelectingTargetProfile,
    DecompilingCode,
    BuildingIntermediateProject,
    ExportingResources,
    WritingGameMakerProject,
    ValidatingProject,
    Completed
}

public sealed record ReconstructedProjectOptions(
    string OutputDirectory,
    bool OverwriteOutput = false,
    bool ExportRawFallbacks = true,
    bool ExportAssemblyFallbacks = true,
    bool ValidateOutput = true);

public sealed record ReconstructionResourceCatalogItem(
    ResourceKind? ResourceKind,
    int ResourceIndex,
    string ResourceName,
    string RelativeOutputPath);

public sealed record ReconstructionProgress(
    ReconstructionStage Stage,
    int Completed,
    int Total,
    string Message,
    ResourceKind? ResourceKind = null,
    int ResourceIndex = -1,
    string? ResourceName = null,
    string? RelativeOutputPath = null,
    byte[]? PreviewPng = null,
    string? Status = null,
    string? PreviewText = null,
    IReadOnlyList<ReconstructionResourceCatalogItem>? ResourceCatalog = null)
{
    public double Percentage => Total <= 0
        ? 0
        : Math.Clamp((double)Completed / Total * 100.0, 0, 100);
}

public sealed record ReconstructedProjectResult(
    string OutputDirectory,
    string ProjectFile,
    string IntermediateProjectFile,
    string ValidationReportFile,
    string TargetProfile,
    int ResourcesDiscovered,
    int ResourcesRepresented,
    int ResourcesPreservedAsFallback,
    int ResourceFailures,
    int WarningCount,
    int ErrorCount,
    TimeSpan Elapsed);

public sealed class SplitGmProjectDocument
{
    public string Format { get; init; } = "SplitGM Reconstructed Project";
    public string FormatVersion { get; init; } = "1.0";
    public string Generator { get; init; } = SplitGmProduct.Name;
    public string GeneratorVersion { get; init; } = SplitGmProduct.Version;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required SplitGmSourceProject Source { get; init; }
    public required SplitGmTargetProject Target { get; init; }
    public List<SplitGmProjectResource> Resources { get; init; } = [];
    public List<SplitGmProjectRelationship> Relationships { get; init; } = [];
    public List<SplitGmReconstructionMessage> Messages { get; init; } = [];
    public SplitGmValidationSummary Validation { get; set; } = new();
}

public sealed class SplitGmSourceProject
{
    public required string OriginalInput { get; init; }
    public required string ResolvedDataSource { get; init; }
    public required string ResolutionMethod { get; init; }
    public required string GameName { get; init; }
    public required string DisplayName { get; init; }
    public required string GameMakerVersion { get; init; }
    public int BytecodeVersion { get; init; }
    public bool IsYyc { get; init; }
}

public sealed class SplitGmTargetProject
{
    public required string ProfileId { get; init; }
    public required string ProfileDescription { get; init; }
    public required string IdeVersion { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectFile { get; init; }
    public bool UsesModernTypeTags { get; init; }
    public bool IsRepairOriented { get; init; }
}

public sealed class SplitGmProjectResource
{
    public required string StableId { get; init; }
    public required string SourceType { get; init; }
    public required string SourceName { get; init; }
    public int SourceIndex { get; init; }
    public string? SourceCodeName { get; init; }
    public required string ReconstructedName { get; init; }
    public required string Representation { get; init; }
    public string? YypResourcePath { get; init; }
    public string? FallbackPath { get; init; }
    public bool RepresentedInYyp { get; init; }
    public bool ExportSucceeded { get; init; }
    public List<string> Files { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class SplitGmProjectRelationship
{
    public required string Kind { get; init; }
    public required string SourceStableId { get; init; }
    public required string TargetStableId { get; init; }
    public string? Details { get; init; }
    public string Confidence { get; init; } = "Direct";
}

public sealed class SplitGmReconstructionMessage
{
    public required string Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? ResourceStableId { get; init; }
    public string? Path { get; init; }
}

public sealed class SplitGmValidationSummary
{
    public int ChecksPerformed { get; set; }
    public int PassedChecks { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public bool ProjectJsonParsed { get; set; }
    public bool IntermediateProjectJsonParsed { get; set; }
    public bool IntermediateFormatVersionRecognized { get; set; }
    public bool EveryListedResourceExists { get; set; }
    public bool EveryListedResourceJsonParsed { get; set; }
    public bool EveryRecordedOutputFileExists { get; set; }
    public int RecordedOutputFilesChecked { get; set; }
    public int MissingRecordedOutputFiles { get; set; }
    public bool ResourcePathsAreUnique { get; set; }
    public bool ResourceNamesAreUniqueWithinType { get; set; }
    public bool StableResourceIdsAreUnique { get; set; }
    public bool RelationshipEndpointsResolved { get; set; }
    public int ResourceJsonFilesChecked { get; set; }
    public int ResourceJsonParseErrors { get; set; }
}

namespace SplitGM.Core;

public enum RepairConfidence
{
    Certain,
    High,
    Medium,
    Low,
    ManualReview
}

public sealed class ReconstructionRepairAction
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public string? RelativePath { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public RepairConfidence Confidence { get; init; }
    public bool Applied { get; init; }
    public string? Evidence { get; init; }
    public List<string> ManualSteps { get; init; } = [];
}

public sealed class ReconstructionCompilePreflight
{
    public int JsonFilesChecked { get; set; }
    public int JsonParseErrors { get; set; }
    public int GmlFilesChecked { get; set; }
    public int GmlBalanceErrors { get; set; }
    public int MissingFiles { get; set; }
    public int InvalidIdentifiers { get; set; }
    public int DuplicateNames { get; set; }
    public int BrokenResourceReferences { get; set; }
    public int UnresolvedFunctionCandidates { get; set; }
    public bool Passed => JsonParseErrors == 0 && GmlBalanceErrors == 0 && MissingFiles == 0 &&
                          InvalidIdentifiers == 0 && DuplicateNames == 0 && BrokenResourceReferences == 0;
}

public sealed class ReconstructionRepairReport
{
    public string Format { get; init; } = "SplitGM Automatic Reconstruction Repair Report";
    public string FormatVersion { get; init; } = "1.0";
    public string GeneratorVersion { get; init; } = SplitGmProduct.Version;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ProjectFile { get; init; }
    public required string TargetProfile { get; init; }
    public required string OriginalOutputDirectory { get; init; }
    public List<ReconstructionRepairAction> Actions { get; init; } = [];
    public List<string> UnresolvedFunctions { get; init; } = [];
    public List<string> ExtensionFunctions { get; init; } = [];
    public ReconstructionCompilePreflight Preflight { get; init; } = new();

    public int AppliedCount => Actions.Count(action => action.Applied);
    public int ManualReviewCount => Actions.Count(action => !action.Applied || action.Confidence == RepairConfidence.ManualReview);
    public int CertainOrHighConfidenceCount => Actions.Count(action => action.Applied && action.Confidence is RepairConfidence.Certain or RepairConfidence.High);
}

public sealed record ReconstructionRepairResult(
    string JsonReportFile,
    string TextReportFile,
    string OriginalOutputDirectory,
    string UnresolvedFunctionsFile,
    int AppliedRepairs,
    int ManualReviewItems,
    ReconstructionCompilePreflight Preflight);

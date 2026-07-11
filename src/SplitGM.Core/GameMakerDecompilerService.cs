using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SplitGM.Core;

public sealed class GameMakerDecompilerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<DecompileResult> DecompileAsync(
        DecompileOptions options,
        IProgress<DecompileProgress>? progress = null,
        IProgress<LogMessage>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        GameProjectLoader loader = new();
        using GameProjectSession session = await loader.LoadAsync(
            options.InputPath,
            progress,
            log,
            cancellationToken).ConfigureAwait(false);

        return await ExportAsync(
            session,
            new ProjectExportOptions(
                options.OutputDirectory,
                options.OverwriteOutput,
                options.ExportAssembly,
                options.ExportResourceIndexes,
                options.ExportResources),
            progress,
            log,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<DecompileResult> ExportAsync(
        GameProjectSession session,
        ProjectExportOptions options,
        IProgress<DecompileProgress>? progress = null,
        IProgress<LogMessage>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        return ExportCoreAsync(session, options, progress, log, cancellationToken);
    }

    private static async Task<DecompileResult> ExportCoreAsync(
        GameProjectSession session,
        ProjectExportOptions options,
        IProgress<DecompileProgress>? progress,
        IProgress<LogMessage>? externalLog,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<LogMessage> logMessages = [];

        void Report(LogMessage message)
        {
            logMessages.Add(message);
            externalLog?.Report(message);
        }

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        PrepareOutputDirectory(outputDirectory, options.OverwriteOutput);
        Report(LogMessage.Info($"Exporting {session.Info.DisplayName} to {outputDirectory}"));

        string gameInfoDirectory = Path.Combine(outputDirectory, "GameInfo");
        string resourceIndexDirectory = Path.Combine(outputDirectory, "ResourceIndexes");
        string errorDirectory = Path.Combine(outputDirectory, "Errors");
        string logsDirectory = Path.Combine(outputDirectory, "Logs");
        Directory.CreateDirectory(gameInfoDirectory);
        Directory.CreateDirectory(errorDirectory);
        Directory.CreateDirectory(logsDirectory);
        if (options.ExportResourceIndexes)
            Directory.CreateDirectory(resourceIndexDirectory);

        WriteJson(Path.Combine(gameInfoDirectory, "GeneralInfo.json"), session.Info);
        WriteJson(Path.Combine(gameInfoDirectory, "ResourceCounts.json"), session.ResourceCounts);
        File.WriteAllText(
            Path.Combine(gameInfoDirectory, "CompatibilityReport.txt"),
            session.GetCompatibilityReportText(),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(gameInfoDirectory, "GameInformation.txt"),
            session.GetGeneralInformationText(),
            new UTF8Encoding(false));

        if (options.ExportResourceIndexes)
            WriteResourceIndexes(session, resourceIndexDirectory, cancellationToken);

        IReadOnlyList<string> objectNames = session.GetResourceNames(ResourceKind.Objects);
        IReadOnlyList<string> roomNames = session.GetResourceNames(ResourceKind.Rooms);
        List<ManifestCodeEntry> manifestEntries = new(session.CodeEntries.Count);
        List<object> failureEntries = [];
        int succeeded = 0;
        int failed = 0;

        if (session.Info.IsYyc)
        {
            Report(LogMessage.Warning(
                "YYC was detected. SplitGM exported resource indexes and compatibility information, but no GML or VM assembly."));
        }
        else
        {
            progress?.Report(new DecompileProgress(
                DecompileStage.DecompilingCode,
                0,
                session.CodeEntries.Count,
                $"Exporting {session.CodeEntries.Count:N0} code entries..."));

            foreach (CodeEntryInfo entry in session.CodeEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CodeViewResult view = await session.GetCodeViewAsync(entry.Index, cancellationToken)
                    .ConfigureAwait(false);

                string relativeGmlPath = OutputPathHelper.BuildRelativeGmlPath(
                    entry,
                    objectNames,
                    roomNames);
                string fullGmlPath = Path.Combine(outputDirectory, relativeGmlPath);
                string? writtenGmlPath = null;
                string? writtenAssemblyPath = null;
                string? writtenErrorPath = null;

                if (view.GmlSucceeded)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fullGmlPath)!);
                    fullGmlPath = OutputPathHelper.EnsureUniquePath(fullGmlPath);
                    File.WriteAllText(fullGmlPath, view.Gml, new UTF8Encoding(false));
                    writtenGmlPath = Path.GetRelativePath(outputDirectory, fullGmlPath);
                    succeeded++;
                }
                else
                {
                    failed++;
                    string fullErrorPath = Path.Combine(
                        errorDirectory,
                        entry.Category.ToString(),
                        OutputPathHelper.SafeFileName(entry.Name) + ".error.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(fullErrorPath)!);
                    fullErrorPath = OutputPathHelper.EnsureUniquePath(fullErrorPath);
                    File.WriteAllText(fullErrorPath, view.DecompileError ?? "Unknown decompiler error.", new UTF8Encoding(false));
                    writtenErrorPath = Path.GetRelativePath(outputDirectory, fullErrorPath);
                    failureEntries.Add(new
                    {
                        entry.Index,
                        entry.Name,
                        Category = entry.Category.ToString(),
                        Error = view.DecompileError,
                        AssemblyAvailable = view.AssemblySucceeded
                    });
                    Report(LogMessage.Error($"GML decompilation failed for {entry.Name}; assembly fallback was retained."));
                }

                if (options.ExportAssembly && view.AssemblySucceeded)
                {
                    string assemblyRelative = Path.Combine(
                        "VMAssembly",
                        Path.ChangeExtension(relativeGmlPath, ".asm"));
                    string fullAssemblyPath = Path.Combine(outputDirectory, assemblyRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullAssemblyPath)!);
                    fullAssemblyPath = OutputPathHelper.EnsureUniquePath(fullAssemblyPath);
                    File.WriteAllText(fullAssemblyPath, view.Assembly, new UTF8Encoding(false));
                    writtenAssemblyPath = Path.GetRelativePath(outputDirectory, fullAssemblyPath);
                }

                manifestEntries.Add(new ManifestCodeEntry
                {
                    Name = entry.Name,
                    Category = entry.Category.ToString(),
                    GmlPath = writtenGmlPath,
                    AssemblyPath = writtenAssemblyPath,
                    ErrorPath = writtenErrorPath,
                    Success = view.GmlSucceeded,
                    Error = view.DecompileError is null
                        ? null
                        : FirstLine(view.DecompileError)
                });

                int completed = entry.Index + 1;
                progress?.Report(new DecompileProgress(
                    DecompileStage.DecompilingCode,
                    completed,
                    session.CodeEntries.Count,
                    $"[{completed:N0}/{session.CodeEntries.Count:N0}] {entry.Name}"));
            }
        }

        ResourceExtractionResult? resourceExtraction = null;
        if (options.ExportResources)
        {
            Report(LogMessage.Info("Beginning full read-only resource extraction..."));
            resourceExtraction = await Task.Run(
                () => ResourceExtractionService.ExportAll(
                    session,
                    Path.Combine(outputDirectory, "Resources"),
                    progress,
                    externalLog,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            Report(resourceExtraction.FailedResources == 0
                ? LogMessage.Success($"Resource extraction completed: {resourceExtraction.ResourcesProcessed:N0} resources, {resourceExtraction.FilesWritten:N0} files.")
                : LogMessage.Warning($"Resource extraction completed with {resourceExtraction.FailedResources:N0} failed resources."));
        }

        progress?.Report(new DecompileProgress(
            DecompileStage.WritingManifest,
            0,
            0,
            "Writing SplitGM project manifest and diagnostics..."));

        SplitGmManifest manifest = new()
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            OriginalInput = session.Info.OriginalInput,
            ResolvedDataSource = session.Info.ResolvedDataSource,
            ResolutionMethod = session.Info.ResolutionMethod,
            GameName = session.Info.GameName,
            DisplayName = session.Info.DisplayName,
            GameMakerVersion = session.Info.GameMakerVersion,
            BytecodeVersion = session.Info.BytecodeVersion,
            IsYyc = session.Info.IsYyc,
            ResourceCounts = session.ResourceCounts,
            TotalRootCodeEntries = session.CodeEntries.Count,
            SuccessfulEntries = succeeded,
            FailedEntries = failed,
            WarningCount = session.Warnings.Count + (resourceExtraction?.Warnings.Count ?? 0),
            ResourcesExported = resourceExtraction?.ResourcesProcessed ?? 0,
            ResourceFilesWritten = resourceExtraction?.FilesWritten ?? 0,
            ResourceExportFailures = resourceExtraction?.FailedResources ?? 0,
            ResourceBytesWritten = resourceExtraction?.BytesWritten ?? 0,
            CodeEntries = manifestEntries,
            Warnings = session.Warnings
                .Concat(resourceExtraction?.Warnings ?? Array.Empty<string>())
                .ToList()
        };

        string manifestPath = Path.Combine(outputDirectory, "SplitGM-Manifest.json");
        WriteJson(manifestPath, manifest);
        WriteJson(Path.Combine(outputDirectory, "CodeIndex.json"), manifestEntries);
        WriteJson(Path.Combine(logsDirectory, "DecompilerFailures.json"), failureEntries);

        stopwatch.Stop();
        WriteSummary(outputDirectory, manifest, stopwatch.Elapsed);
        Report(LogMessage.Success(
            session.Info.IsYyc
                ? "Resource metadata export completed for the YYC game."
                : $"Project export complete: {succeeded:N0} GML entries succeeded and {failed:N0} failed."));
        WriteLog(Path.Combine(logsDirectory, "Export.log"), logMessages);

        progress?.Report(new DecompileProgress(
            DecompileStage.Completed,
            session.CodeEntries.Count,
            session.CodeEntries.Count,
            session.Info.IsYyc
                ? "Resource metadata export completed."
                : $"Completed: {succeeded:N0} succeeded, {failed:N0} failed."));

        return new DecompileResult(
            outputDirectory,
            manifestPath,
            session.CodeEntries.Count,
            succeeded,
            failed,
            manifest.WarningCount,
            stopwatch.Elapsed);
    }

    private static void WriteResourceIndexes(
        GameProjectSession session,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        foreach (ResourceKind kind in Enum.GetValues<ResourceKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ResourceEntryInfo> entries = session.GetResourceEntries(kind);
            WriteJson(Path.Combine(outputDirectory, kind + ".json"), entries);
        }
    }

    private static void PrepareOutputDirectory(string outputDirectory, bool overwrite)
    {
        string? root = Path.GetPathRoot(outputDirectory);
        if (string.Equals(
                outputDirectory.TrimEnd(Path.DirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A drive root cannot be used as the SplitGM output directory.");
        }

        const string markerName = ".splitgm-output";
        string markerPath = Path.Combine(outputDirectory, markerName);

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            if (!overwrite)
                throw new IOException("The output directory is not empty. Enable overwrite or choose an empty folder.");

            bool looksLikeSplitGmOutput = File.Exists(markerPath) ||
                File.Exists(Path.Combine(outputDirectory, "SplitGM-Manifest.json")) ||
                File.Exists(Path.Combine(outputDirectory, "splitgm-manifest.json"));

            if (!looksLikeSplitGmOutput)
            {
                throw new IOException(
                    "For safety, SplitGM will only erase a non-empty folder previously created by SplitGM. Choose a new empty folder instead.");
            }

            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            markerPath,
            $"Created by {SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}. This folder may be replaced when overwrite mode is enabled.\n",
            new UTF8Encoding(false));
    }

    private static void WriteSummary(
        string outputDirectory,
        SplitGmManifest manifest,
        TimeSpan elapsed)
    {
        StringBuilder summary = new();
        summary.AppendLine($"{SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}");
        summary.AppendLine("Organized reconstructed-project export");
        summary.AppendLine(new string('=', 60));
        summary.AppendLine($"Game: {manifest.DisplayName ?? manifest.GameName ?? "Unknown"}");
        summary.AppendLine($"GameMaker version: {manifest.GameMakerVersion}");
        summary.AppendLine($"Bytecode version: {manifest.BytecodeVersion}");
        summary.AppendLine($"YYC: {manifest.IsYyc}");
        summary.AppendLine($"Root code entries: {manifest.TotalRootCodeEntries:N0}");
        summary.AppendLine($"GML succeeded: {manifest.SuccessfulEntries:N0}");
        summary.AppendLine($"GML failed: {manifest.FailedEntries:N0}");
        summary.AppendLine($"Load warnings: {manifest.WarningCount:N0}");
        summary.AppendLine($"Resources exported: {manifest.ResourcesExported:N0}");
        summary.AppendLine($"Resource files written: {manifest.ResourceFilesWritten:N0}");
        summary.AppendLine($"Resource export failures: {manifest.ResourceExportFailures:N0}");
        summary.AppendLine($"Resource bytes written: {manifest.ResourceBytesWritten:N0}");
        summary.AppendLine($"Elapsed: {elapsed}");
        summary.AppendLine();
        summary.AppendLine("This output is reconstructed from compiled GameMaker data. Original comments,");
        summary.AppendLine("formatting, optimized-out code, and some original symbol information cannot be recovered.");

        File.WriteAllText(
            Path.Combine(outputDirectory, "README-SplitGM-Export.txt"),
            summary.ToString(),
            new UTF8Encoding(false));
    }

    private static void WriteLog(string path, IEnumerable<LogMessage> messages)
    {
        StringBuilder output = new();
        foreach (LogMessage message in messages)
        {
            output.Append('[').Append(message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .Append(message.Level.ToString().ToUpperInvariant().PadRight(7)).Append(' ')
                .AppendLine(message.Text);
        }
        File.WriteAllText(path, output.ToString(), new UTF8Encoding(false));
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false));
    }

    private static string FirstLine(string value)
    {
        int newline = value.IndexOfAny(['\r', '\n']);
        return newline < 0 ? value : value[..newline];
    }
}

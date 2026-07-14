using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace SplitGM.Core;

public sealed partial class GameProjectSession
{
    private static readonly JsonSerializerOptions ReconstructionJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<ReconstructedProjectResult> ExportReconstructedProjectAsync(
        ReconstructedProjectOptions options,
        IProgress<ReconstructionProgress>? progress = null,
        IProgress<LogMessage>? log = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);

        Stopwatch stopwatch = Stopwatch.StartNew();
        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        PrepareReconstructionOutputDirectory(outputDirectory, options.OverwriteOutput);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.Preparing, 0, 0,
            "Preparing the reconstructed GameMaker project folder..."));
        log?.Report(LogMessage.Info($"Beginning reconstructed .yyp export to {outputDirectory}"));

        ReconstructionTargetProfile profile = SelectReconstructionProfile(Info.GameMakerVersion);
        string projectName = MakeGameMakerIdentifier(
            string.IsNullOrWhiteSpace(Info.GameName) ? Info.DisplayName : Info.GameName,
            "ReconstructedGame");
        string projectFileName = projectName + ".yyp";
        string projectFilePath = Path.Combine(outputDirectory, projectFileName);
        string intermediateFilePath = Path.Combine(outputDirectory, projectName + ".splitgmproj");
        string validationFilePath = Path.Combine(outputDirectory, "SplitGM-Reconstruction-Validation.json");

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.SelectingTargetProfile, 1, 1,
            $"Selected target profile: {profile.Description}"));
        log?.Report(LogMessage.Info($"Target profile: {profile.Id} ({profile.IdeVersion})"));

        SplitGmProjectDocument document = new()
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Source = new SplitGmSourceProject
            {
                OriginalInput = Info.OriginalInput,
                ResolvedDataSource = Info.ResolvedDataSource,
                ResolutionMethod = Info.ResolutionMethod,
                GameName = Info.GameName,
                DisplayName = Info.DisplayName,
                GameMakerVersion = Info.GameMakerVersion,
                BytecodeVersion = Info.BytecodeVersion,
                IsYyc = Info.IsYyc
            },
            Target = new SplitGmTargetProject
            {
                ProfileId = profile.Id,
                ProfileDescription = profile.Description,
                IdeVersion = profile.IdeVersion,
                ProjectName = projectName,
                ProjectFile = projectFileName,
                UsesModernTypeTags = true,
                IsRepairOriented = true
            }
        };

        void AddMessage(string severity, string code, string message, string? resourceId = null, string? path = null)
        {
            document.Messages.Add(new SplitGmReconstructionMessage
            {
                Severity = severity,
                Code = code,
                Message = message,
                ResourceStableId = resourceId,
                Path = path
            });
            if (severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                log?.Report(LogMessage.Error(message));
            else if (severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                log?.Report(LogMessage.Warning(message));
            else
                log?.Report(LogMessage.Info(message));
        }

        foreach (string warning in Warnings)
            AddMessage("Warning", "SOURCE_LOAD_WARNING", warning);

        if (!profile.SourceVersionDirectlyCompatible)
        {
            AddMessage(
                "Warning",
                "TARGET_PROFILE_REPAIR_MODE",
                $"The compiled game reports GameMaker {Info.GameMakerVersion}. SplitGM is generating a modern repair-oriented project using {profile.Description}; it is not an original-version project clone.");
        }

        if (Info.IsYyc)
        {
            AddMessage(
                "Warning",
                "YYC_CODE_UNAVAILABLE",
                "The game uses YYC. Resource reconstruction can continue, but normal VM GML and object-event source are unavailable.");
        }

        Dictionary<int, CodeViewResult> codeViews = [];
        if (!Info.IsYyc)
        {
            for (int position = 0; position < CodeEntries.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CodeEntryInfo entry = CodeEntries[position];
                progress?.Report(new ReconstructionProgress(
                    ReconstructionStage.DecompilingCode,
                    position,
                    CodeEntries.Count,
                    $"Decompiling code [{position + 1:N0}/{CodeEntries.Count:N0}] {entry.Name}",
                    ResourceName: entry.Name,
                    Status: "Decompiling"));
                try
                {
                    CodeViewResult codeView = await GetCodeViewAsync(entry.Index, cancellationToken).ConfigureAwait(false);
                    codeViews[entry.Index] = codeView;
                    if (!codeView.GmlSucceeded)
                    {
                        AddMessage(
                            "Warning",
                            "CODE_GML_NOT_RECOVERED",
                            $"GML could not be reconstructed safely for {entry.Name}. SplitGM preserved a failure marker and VM assembly when available.",
                            StableId("Code", entry.Index, entry.Name));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    AddMessage("Error", "CODE_DECOMPILE_EXCEPTION", $"Failed to decompile {entry.Name}: {exception.Message}", StableId("Code", entry.Index, entry.Name));
                }
            }
        }

        IReadOnlyList<string> objectSourceNames = GetResourceNames(ResourceKind.Objects);
        IReadOnlyList<string> roomSourceNames = GetResourceNames(ResourceKind.Rooms);
        string rawCodeDirectory = Path.Combine(outputDirectory, "__SplitGM_Unrepresented", "Code");
        Directory.CreateDirectory(rawCodeDirectory);
        foreach (CodeEntryInfo entry in CodeEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!codeViews.TryGetValue(entry.Index, out CodeViewResult? view))
                continue;
            string relative = Path.Combine("__SplitGM_Unrepresented", "Code",
                OutputPathHelper.BuildRelativeGmlPath(entry, objectSourceNames, roomSourceNames));
            string full = Path.Combine(outputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, view.GmlSucceeded ? view.Gml : BuildFailedGml(entry, view), new UTF8Encoding(false));
            if (options.ExportAssemblyFallbacks && view.AssemblySucceeded)
            {
                string assemblyPath = Path.ChangeExtension(full, ".asm");
                File.WriteAllText(assemblyPath, view.Assembly, new UTF8Encoding(false));
            }
        }

        Dictionary<(ResourceKind Kind, int Index), string> reconstructedNames = [];
        Dictionary<(ResourceKind Kind, int Index), string> stableIds = [];
        foreach (ResourceKind kind in Enum.GetValues<ResourceKind>())
        {
            HashSet<string> allocated = new(StringComparer.OrdinalIgnoreCase);
            foreach (ResourceEntryInfo entry in GetResourceEntries(kind))
            {
                string name = AllocateIdentifier(entry.Name, kind.ToString(), entry.Index, allocated);
                reconstructedNames[(kind, entry.Index)] = name;
                stableIds[(kind, entry.Index)] = StableId(kind.ToString(), entry.Index, entry.Name);
            }
        }

        HashSet<string> scriptNameAllocator = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> scriptNames = [];
        foreach (CodeEntryInfo entry in CodeEntries.Where(item => item.Category == CodeCategory.Scripts))
            scriptNames[entry.Index] = AllocateIdentifier(StripCodePrefix(entry.Name, "gml_Script_"), "script", entry.Index, scriptNameAllocator);

        List<YypResourceReference> yypResources = [];
        List<YypResourceReference> roomOrder = [];
        List<YypFolderDefinition> folders = BuildDefaultFolders();
        List<JsonObject> audioGroups = BuildAudioGroups(reconstructedNames);

        ResourceKind[] exportOrder =
        [
            ResourceKind.Sprites,
            ResourceKind.Sounds,
            ResourceKind.Paths,
            ResourceKind.Objects,
            ResourceKind.Rooms,
            ResourceKind.Backgrounds,
            ResourceKind.Fonts,
            ResourceKind.Shaders,
            ResourceKind.Timelines,
            ResourceKind.Extensions,
            ResourceKind.AudioGroups,
            ResourceKind.Sequences,
            ResourceKind.AnimationCurves,
            ResourceKind.ParticleSystems,
            ResourceKind.ParticleSystemEmitters,
            ResourceKind.TextureGroups,
            ResourceKind.FilterEffects,
            ResourceKind.EmbeddedImages,
            ResourceKind.EmbeddedAudio,
            ResourceKind.TexturePageItems,
            ResourceKind.TexturePages,
            ResourceKind.Strings,
            ResourceKind.Functions,
            ResourceKind.Variables
        ];

        int totalResourceWork = scriptNames.Count + Enum.GetValues<ResourceKind>()
            .Sum(kind => GetResourceEntries(kind).Count);
        int completedResourceWork = 0;
        int represented = 0;
        int fallbackOnly = 0;
        int failures = 0;

        // Send the complete workload to the dedicated progress window as one catalog
        // update. This keeps very large games responsive while still listing every
        // resource before the first export begins.
        List<ReconstructionResourceCatalogItem> resourceCatalog = [];
        foreach ((int _, string scriptName) in scriptNames.OrderBy(item => item.Key))
        {
            resourceCatalog.Add(new ReconstructionResourceCatalogItem(
                null,
                -1,
                scriptName,
                $"scripts/{scriptName}/{scriptName}.yy"));
        }
        foreach (ResourceKind kind in exportOrder)
        {
            foreach (ResourceEntryInfo entry in GetResourceEntries(kind))
            {
                string queuedName = reconstructedNames[(kind, entry.Index)];
                resourceCatalog.Add(new ReconstructionResourceCatalogItem(
                    kind,
                    entry.Index,
                    entry.Name,
                    SuggestedResourcePath(kind, queuedName)));
            }
        }
        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.ExportingResources,
            0,
            totalResourceWork,
            $"Queued {resourceCatalog.Count:N0} scripts and resources for reconstruction.",
            Status: "Queued",
            ResourceCatalog: resourceCatalog));

        foreach ((int codeIndex, string scriptName) in scriptNames.OrderBy(item => item.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodeEntryInfo entry = CodeEntries.First(item => item.Index == codeIndex);
            string stableId = StableId("Code", entry.Index, entry.Name);
            string relativeYy = $"scripts/{scriptName}/{scriptName}.yy";
            string relativeGml = $"scripts/{scriptName}/{scriptName}.gml";
            string gml = codeViews.TryGetValue(codeIndex, out CodeViewResult? scriptView)
                ? (scriptView.GmlSucceeded ? scriptView.Gml : BuildFailedGml(entry, scriptView))
                : $"/// @description SplitGM could not recover {entry.Name}.\n";
            string scriptPreviewText = TruncatePreviewText(gml);
            progress?.Report(new ReconstructionProgress(
                ReconstructionStage.ExportingResources,
                completedResourceWork,
                totalResourceWork,
                $"Exporting script {scriptName}",
                ResourceName: scriptName,
                RelativeOutputPath: relativeYy,
                Status: "Exporting",
                PreviewText: scriptPreviewText));

            string scriptStatus = !codeViews.TryGetValue(codeIndex, out CodeViewResult? queuedCodeView) || !queuedCodeView.GmlSucceeded
                ? "Partial"
                : "Complete";
            try
            {
                Directory.CreateDirectory(Path.Combine(outputDirectory, "scripts", scriptName));
                WriteJsonNode(Path.Combine(outputDirectory, relativeYy), CreateScriptJson(scriptName));
                File.WriteAllText(Path.Combine(outputDirectory, relativeGml), gml, new UTF8Encoding(false));
                yypResources.Add(new YypResourceReference(scriptName, relativeYy, "Scripts"));
                represented++;
                document.Resources.Add(new SplitGmProjectResource
                {
                    StableId = stableId,
                    SourceType = "Code",
                    SourceName = entry.Name,
                    SourceIndex = entry.Index,
                    SourceCodeName = entry.Name,
                    ReconstructedName = scriptName,
                    Representation = "GMScript",
                    YypResourcePath = relativeYy,
                    RepresentedInYyp = true,
                    ExportSucceeded = true,
                    Files = [relativeYy, relativeGml],
                    Warnings = scriptStatus == "Partial"
                        ? [$"The compiled script {entry.Name} did not produce fully reconstructed GML. Review its failure marker and VM assembly fallback."]
                        : []
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                scriptStatus = "Failed";
                failures++;
                AddMessage("Error", "SCRIPT_EXPORT_FAILED", $"Script {scriptName} failed to export: {exception.Message}", stableId, relativeYy);
                document.Resources.Add(FailedResource(stableId, "Code", entry.Name, entry.Index, scriptName, "GMScript", relativeYy, exception));
            }

            completedResourceWork++;
            progress?.Report(new ReconstructionProgress(
                ReconstructionStage.ExportingResources,
                completedResourceWork,
                totalResourceWork,
                $"Exported script {scriptName}",
                ResourceName: scriptName,
                RelativeOutputPath: relativeYy,
                Status: scriptStatus,
                PreviewText: scriptPreviewText));
        }

        foreach (ResourceKind kind in exportOrder)
        {
            IReadOnlyList<ResourceEntryInfo> entries = GetResourceEntries(kind);
            if (entries.Count == 0)
                continue;

            int maxWorkers = GetReconstructionParallelism(kind);
            using TextureWorker categoryTextureWorker = new();
            ReconstructionWorkResult?[] results = new ReconstructionWorkResult?[entries.Count];
            log?.Report(LogMessage.Info($"Exporting {kind} using {maxWorkers:N0} parallel worker(s)."));

            await Task.Run(() => Parallel.For(
                0,
                entries.Count,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maxWorkers
                },
                position =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResourceEntryInfo entry = entries[position];
                    string reconstructedName = reconstructedNames[(kind, entry.Index)];
                    string stableId = stableIds[(kind, entry.Index)];
                    string statusPath = SuggestedResourcePath(kind, reconstructedName);
                    int current = Volatile.Read(ref completedResourceWork);

                    progress?.Report(new ReconstructionProgress(
                        ReconstructionStage.ExportingResources,
                        current,
                        totalResourceWork,
                        $"Exporting {kind} with {maxWorkers:N0} worker(s): {entry.Name}",
                        kind,
                        entry.Index,
                        entry.Name,
                        statusPath,
                        null,
                        "Exporting",
                        null));

                    try
                    {
                        ResourceExportOutcome outcome = ExportReconstructionResource(
                            outputDirectory,
                            kind,
                            entry,
                            reconstructedName,
                            reconstructedNames,
                            stableIds,
                            codeViews,
                            options,
                            categoryTextureWorker,
                            cancellationToken);

                        string completedStatus = outcome.Resource.RepresentedInYyp ? "Complete" : "Fallback";
                        if (outcome.Messages.Any(message =>
                                message.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase) ||
                                message.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
                        {
                            completedStatus = outcome.Resource.RepresentedInYyp ? "Partial" : "Fallback";
                        }

                        byte[]? preview = TryLoadExportedPreview(outputDirectory, outcome.Resource.Files);
                        results[position] = new ReconstructionWorkResult(entry, reconstructedName, stableId, statusPath, outcome, null, completedStatus, preview);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[position] = new ReconstructionWorkResult(entry, reconstructedName, stableId, statusPath, null, exception, "Failed", null);
                    }

                    int done = Interlocked.Increment(ref completedResourceWork);
                    ReconstructionWorkResult completed = results[position]!;
                    progress?.Report(new ReconstructionProgress(
                        ReconstructionStage.ExportingResources,
                        done,
                        totalResourceWork,
                        $"Finished {kind}: {entry.Name}",
                        kind,
                        entry.Index,
                        entry.Name,
                        statusPath,
                        completed.PreviewPng,
                        completed.Status,
                        null));
                }), cancellationToken).ConfigureAwait(false);

            // Merge in source order so the intermediate project and .yyp remain deterministic.
            foreach (ReconstructionWorkResult result in results.Where(item => item is not null).Select(item => item!))
            {
                if (result.Exception is not null)
                {
                    failures++;
                    AddMessage("Error", "RESOURCE_EXPORT_FAILED", $"{kind} resource {result.Entry.Name} failed to export: {result.Exception.Message}", result.StableId, result.StatusPath);
                    document.Resources.Add(FailedResource(result.StableId, kind.ToString(), result.Entry.Name, result.Entry.Index, result.ReconstructedName, "Export failed", result.StatusPath, result.Exception));
                    continue;
                }

                ResourceExportOutcome outcome = result.Outcome!;
                document.Resources.Add(outcome.Resource);
                document.Relationships.AddRange(outcome.Relationships);
                foreach (SplitGmReconstructionMessage message in outcome.Messages)
                {
                    document.Messages.Add(message);
                    if (message.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
                        log?.Report(LogMessage.Error(message.Message));
                    else if (message.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                        log?.Report(LogMessage.Warning(message.Message));
                    else
                        log?.Report(LogMessage.Info(message.Message));
                }

                if (outcome.Resource.RepresentedInYyp)
                {
                    represented++;
                    if (outcome.YypReference is not null)
                    {
                        yypResources.Add(outcome.YypReference);
                        if (kind == ResourceKind.Rooms)
                            roomOrder.Add(outcome.YypReference);
                    }
                }
                else
                {
                    fallbackOnly++;
                }
            }
        }

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.BuildingIntermediateProject,
            0,
            1,
            "Writing the versioned .splitgmproj intermediate document..."));
        WriteReconstructionJson(intermediateFilePath, document);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.WritingGameMakerProject,
            0,
            1,
            "Writing the reconstructed .yyp project and resource order..."));
        JsonObject yyp = CreateProjectJson(projectName, profile, yypResources, roomOrder, folders, audioGroups);
        WriteJsonNode(projectFilePath, yyp);
        WriteJsonNode(
            Path.Combine(outputDirectory, projectName + ".resource_order"),
            CreateResourceOrderJson(yypResources, folders));
        WriteFolderFiles(outputDirectory, folders);
        WriteReconstructionReadme(outputDirectory, projectName, profile, represented, fallbackOnly, failures, document.Messages);

        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.ValidatingProject,
            0,
            1,
            "Validating project JSON, resource paths, names, and reconstructed references..."));
        SplitGmValidationSummary validation = options.ValidateOutput
            ? ValidateReconstructedProject(outputDirectory, projectFilePath, intermediateFilePath, yypResources, document.Resources, document.Relationships, document.Messages)
            : new SplitGmValidationSummary();
        document.Validation = validation;
        WriteReconstructionJson(validationFilePath, new
        {
            Project = projectFileName,
            Profile = profile.Id,
            Summary = validation,
            Messages = document.Messages
        });
        WriteValidationText(Path.Combine(outputDirectory, "SplitGM-Reconstruction-Validation.txt"), validation, document.Messages);
        WriteReconstructionJson(intermediateFilePath, document);

        stopwatch.Stop();
        int warningCount = document.Messages.Count(message => message.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        int errorCount = document.Messages.Count(message => message.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        progress?.Report(new ReconstructionProgress(
            ReconstructionStage.Completed,
            totalResourceWork,
            totalResourceWork,
            $"Reconstructed project complete: {represented:N0} represented, {fallbackOnly:N0} fallback-only, {errorCount:N0} error(s).",
            Status: "Complete"));
        log?.Report(errorCount == 0
            ? LogMessage.Success($"Reconstructed .yyp project written to {projectFilePath}")
            : LogMessage.Warning($"Reconstructed .yyp project completed with {errorCount:N0} error(s)."));

        return new ReconstructedProjectResult(
            outputDirectory,
            projectFilePath,
            intermediateFilePath,
            validationFilePath,
            profile.Description,
            totalResourceWork,
            represented,
            fallbackOnly,
            failures,
            warningCount,
            errorCount,
            stopwatch.Elapsed);
    }

    private ResourceExportOutcome ExportReconstructionResource(
        string outputDirectory,
        ResourceKind kind,
        ResourceEntryInfo entry,
        string reconstructedName,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        IReadOnlyDictionary<int, CodeViewResult> codeViews,
        ReconstructedProjectOptions options,
        TextureWorker textureWorker,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            ResourceKind.Sprites => ExportReconstructedSprite(outputDirectory, entry, reconstructedName, stableIds, options, textureWorker, cancellationToken),
            ResourceKind.Sounds => ExportReconstructedSound(outputDirectory, entry, reconstructedName, reconstructedNames, stableIds, cancellationToken),
            ResourceKind.Paths => ExportReconstructedPath(outputDirectory, entry, reconstructedName, stableIds),
            ResourceKind.Objects => ExportReconstructedObject(outputDirectory, entry, reconstructedName, reconstructedNames, stableIds, codeViews, cancellationToken),
            ResourceKind.Rooms => ExportReconstructedRoom(outputDirectory, entry, reconstructedName, reconstructedNames, stableIds, codeViews, cancellationToken),
            ResourceKind.AudioGroups => ExportReconstructedAudioGroup(outputDirectory, entry, reconstructedName, stableIds),
            _ => ExportFallbackResource(outputDirectory, kind, entry, reconstructedName, stableIds, options, cancellationToken)
        };
    }

    private ResourceExportOutcome ExportReconstructedSprite(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        ReconstructedProjectOptions options,
        TextureWorker textureWorker,
        CancellationToken cancellationToken)
    {
        UndertaleSprite? sprite = GetAt(_data.Sprites, entry.Index) as UndertaleSprite;
        if (sprite is null)
            throw new InvalidDataException("The sprite resource was null.");
        if (!sprite.ProjectExportable)
            return ExportFallbackResource(outputDirectory, ResourceKind.Sprites, entry, name, stableIds, options, cancellationToken);

        string directory = Path.Combine(outputDirectory, "sprites", name);
        Directory.CreateDirectory(directory);
        string relativeYy = $"sprites/{name}/{name}.yy";
        List<string> files = [relativeYy];
        List<string> warnings = [];
        List<string> frameIds = [];
        string layerId = StableGuid("SpriteLayer", entry.Index, entry.Name);
        int frameCount = sprite.Textures?.Count ?? 0;

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UndertaleTexturePageItem? item = sprite.Textures?[frameIndex]?.Texture;
            if (item is null)
            {
                warnings.Add($"Frame {frameIndex} did not contain a recoverable texture-page item.");
                continue;
            }
            string frameId = StableGuid("SpriteFrame", entry.Index * 100000 + frameIndex, entry.Name);
            frameIds.Add(frameId);
            string compositeRelative = $"sprites/{name}/{frameId}.png";
            string layerRelative = $"sprites/{name}/layers/{frameId}/{layerId}.png";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outputDirectory, layerRelative))!);
            textureWorker.ExportAsPNG(item, Path.Combine(outputDirectory, compositeRelative), entry.Name, includePadding: true);
            File.Copy(Path.Combine(outputDirectory, compositeRelative), Path.Combine(outputDirectory, layerRelative), overwrite: true);
            files.Add(compositeRelative);
            files.Add(layerRelative);
        }

        if (sprite.CollisionMasks is { Count: > 0 })
        {
            (int maskWidth, int maskHeight) = sprite.CalculateMaskDimensions(_data);
            for (int maskIndex = 0; maskIndex < sprite.CollisionMasks.Count; maskIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UndertaleSprite.MaskEntry? mask = sprite.CollisionMasks[maskIndex];
                if (mask?.Data is null)
                    continue;
                string maskRelative = $"__SplitGM_Metadata/Sprites/{name}/CollisionMasks/mask_{maskIndex:D4}.png";
                string maskPath = Path.Combine(outputDirectory, maskRelative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(maskPath)!);
                TextureWorker.ExportCollisionMaskPNG(mask, maskPath, maskWidth, maskHeight);
                files.Add(maskRelative);
            }
        }

        JsonObject spriteJson = CreateSpriteJson(name, sprite, frameIds, layerId);
        WriteJsonNode(Path.Combine(outputDirectory, relativeYy), spriteJson);
        string metadataRelative = $"__SplitGM_Metadata/Sprites/{name}.details.txt";
        WriteMetadataFile(outputDirectory, metadataRelative, GetResourceDetails(ResourceKind.Sprites, entry.Index));
        files.Add(metadataRelative);

        string stableId = stableIds[(ResourceKind.Sprites, entry.Index)];
        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = stableId,
                SourceType = ResourceKind.Sprites.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMSprite",
                YypResourcePath = relativeYy,
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = files,
                Warnings = warnings
            },
            new YypResourceReference(name, relativeYy, "Sprites"),
            [],
            warnings.Select(message => WarningMessage("SPRITE_PARTIAL", message, stableId, relativeYy)).ToList());
    }

    private ResourceExportOutcome ExportReconstructedSound(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        CancellationToken cancellationToken)
    {
        UndertaleSound? sound = GetAt(_data.Sounds, entry.Index) as UndertaleSound;
        if (sound is null)
            throw new InvalidDataException("The sound resource was null.");
        AudioPayload payload = GetAudioPayload(entry.Index, cancellationToken);
        string directory = Path.Combine(outputDirectory, "sounds", name);
        Directory.CreateDirectory(directory);
        string audioFileName = name + payload.Extension;
        string relativeAudio = $"sounds/{name}/{audioFileName}";
        string relativeYy = $"sounds/{name}/{name}.yy";
        File.WriteAllBytes(Path.Combine(outputDirectory, relativeAudio), payload.Data);

        int groupIndex = Math.Max(0, payload.GroupId);
        string groupName = reconstructedNames.TryGetValue((ResourceKind.AudioGroups, groupIndex), out string? mappedGroup)
            ? mappedGroup
            : "audiogroup_default";
        WriteJsonNode(Path.Combine(outputDirectory, relativeYy), CreateSoundJson(name, audioFileName, groupName, sound));
        string metadataRelative = $"__SplitGM_Metadata/Sounds/{name}.details.txt";
        WriteMetadataFile(outputDirectory, metadataRelative, GetResourceDetails(ResourceKind.Sounds, entry.Index));

        string stableId = stableIds[(ResourceKind.Sounds, entry.Index)];
        List<SplitGmProjectRelationship> relationships = [];
        List<SplitGmReconstructionMessage> messages =
        [
            WarningMessage(
                "SOUND_AUTHORING_SETTINGS_INFERRED",
                $"Sound {entry.Name} was reconstructed with recovered audio, volume, duration when available, and audio-group assignment. Authoring-only sample rate, channel format, bit depth, compression/conversion, and modern preload settings are not fully preserved by compiled data, so repair-safe defaults were written.",
                stableId,
                relativeYy)
        ];
        if (stableIds.TryGetValue((ResourceKind.AudioGroups, groupIndex), out string? audioGroupStableId))
        {
            relationships.Add(new SplitGmProjectRelationship
            {
                Kind = "SoundAudioGroup",
                SourceStableId = stableId,
                TargetStableId = audioGroupStableId,
                Details = groupName
            });
        }

        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = stableId,
                SourceType = ResourceKind.Sounds.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMSound",
                YypResourcePath = relativeYy,
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = [relativeYy, relativeAudio, metadataRelative],
                Warnings = messages.Select(message => message.Message).ToList()
            },
            new YypResourceReference(name, relativeYy, "Sounds"),
            relationships,
            messages);
    }

    private ResourceExportOutcome ExportReconstructedPath(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds)
    {
        UndertalePath? path = GetAt(_data.Paths, entry.Index) as UndertalePath;
        if (path is null)
            throw new InvalidDataException("The path resource was null.");
        string relativeYy = $"paths/{name}/{name}.yy";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outputDirectory, relativeYy))!);
        WriteJsonNode(Path.Combine(outputDirectory, relativeYy), CreatePathJson(name, path));
        string metadataRelative = $"__SplitGM_Metadata/Paths/{name}.details.txt";
        WriteMetadataFile(outputDirectory, metadataRelative, GetResourceDetails(ResourceKind.Paths, entry.Index));
        string stableId = stableIds[(ResourceKind.Paths, entry.Index)];
        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = stableId,
                SourceType = ResourceKind.Paths.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMPath",
                YypResourcePath = relativeYy,
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = [relativeYy, metadataRelative]
            },
            new YypResourceReference(name, relativeYy, "Paths"),
            [],
            []);
    }

    private ResourceExportOutcome ExportReconstructedObject(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        IReadOnlyDictionary<int, CodeViewResult> codeViews,
        CancellationToken cancellationToken)
    {
        ResourcePreviewData preview = BuildObjectPreview(entry.Index);
        ObjectPreviewInfo info = preview.Object ?? throw new InvalidDataException("Object metadata could not be read.");
        UndertaleGameObject? sourceObject = GetAt(_data.GameObjects, entry.Index) as UndertaleGameObject;
        string relativeYy = $"objects/{name}/{name}.yy";
        string directory = Path.Combine(outputDirectory, "objects", name);
        Directory.CreateDirectory(directory);
        List<string> files = [relativeYy];
        List<JsonObject> eventJson = [];
        List<SplitGmProjectRelationship> relationships = [];
        List<SplitGmReconstructionMessage> messages = [];
        string objectStableId = stableIds[(ResourceKind.Objects, entry.Index)];

        foreach (ObjectEventInfo eventInfo in info.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eventInfo.ActionCount > 1)
            {
                messages.Add(WarningMessage(
                    "OBJECT_EVENT_MULTIPLE_ACTIONS",
                    $"Event {eventInfo.EventType}/{eventInfo.Subtype} contains {eventInfo.ActionCount:N0} compiled actions. SplitGM mapped the first action's code entry into the reconstructed event.",
                    objectStableId,
                    relativeYy));
            }
            int eventTypeNumber = Enum.TryParse(eventInfo.EventType, ignoreCase: true, out EventType eventType)
                ? (int)eventType
                : 0;

            JsonNode? collisionObjectId = null;
            string? collisionName = null;
            int? collisionIndex = null;
            int eventNumber;
            if (eventInfo.Subtype > int.MaxValue)
            {
                eventNumber = 0;
                messages.Add(WarningMessage(
                    "OBJECT_EVENT_SUBTYPE_OUT_OF_RANGE",
                    $"Event {eventInfo.EventType}/{eventInfo.Subtype} used a subtype value that cannot be represented safely by the reconstructed GameMaker event schema. The exported event number was reset to 0.",
                    objectStableId,
                    relativeYy));
            }
            else
            {
                eventNumber = (int)eventInfo.Subtype;
            }

            if (eventInfo.EventType.Equals("Collision", StringComparison.OrdinalIgnoreCase))
            {
                if (eventInfo.Subtype <= int.MaxValue)
                {
                    collisionIndex = (int)eventInfo.Subtype;
                    if (reconstructedNames.TryGetValue((ResourceKind.Objects, collisionIndex.Value), out string? mappedCollisionName))
                    {
                        collisionName = mappedCollisionName;
                        collisionObjectId = ResourceId(collisionName, $"objects/{collisionName}/{collisionName}.yy");
                        eventNumber = 0;
                    }
                    else
                    {
                        messages.Add(WarningMessage(
                            "COLLISION_TARGET_MISSING",
                            $"Collision event {eventInfo.CodeName} referenced object index {collisionIndex.Value}, which was not present in the reconstructed object table.",
                            objectStableId,
                            relativeYy));
                    }
                }
                else
                {
                    messages.Add(WarningMessage(
                        "COLLISION_TARGET_OUT_OF_RANGE",
                        $"Collision event {eventInfo.CodeName} referenced object index {eventInfo.Subtype}, which is outside the safely representable object-index range.",
                        objectStableId,
                        relativeYy));
                }
            }

            string fileStem = collisionName is null
                ? BuildObjectEventFileStem(eventInfo.EventType, eventInfo.Subtype)
                : "Collision_" + collisionName;
            string relativeGml = $"objects/{name}/{fileStem}.gml";
            string gml;
            if (eventInfo.CodeIndex >= 0 && codeViews.TryGetValue(eventInfo.CodeIndex, out CodeViewResult? codeView))
            {
                gml = codeView.GmlSucceeded ? codeView.Gml : BuildFailedGml(FindCodeEntry(eventInfo.CodeIndex), codeView);
                if (!codeView.GmlSucceeded)
                {
                    messages.Add(WarningMessage(
                        "OBJECT_EVENT_GML_PARTIAL",
                        $"The event source for {eventInfo.CodeName} could not be reconstructed as safe GML. A failure marker and VM assembly fallback were preserved.",
                        objectStableId,
                        relativeGml));
                }
            }
            else
            {
                gml = $"/// @description SplitGM could not recover {eventInfo.CodeName}.\n";
                messages.Add(WarningMessage(
                    "OBJECT_EVENT_CODE_MISSING",
                    $"No recoverable code entry was found for object event {eventInfo.EventType}/{eventInfo.Subtype} ({eventInfo.CodeName}).",
                    objectStableId,
                    relativeGml));
            }
            File.WriteAllText(Path.Combine(outputDirectory, relativeGml), gml, new UTF8Encoding(false));
            files.Add(relativeGml);

            if (collisionName is not null && collisionIndex is int resolvedCollisionIndex)
            {
                if (stableIds.TryGetValue((ResourceKind.Objects, resolvedCollisionIndex), out string? collisionStableId))
                {
                    relationships.Add(new SplitGmProjectRelationship
                    {
                        Kind = "ObjectCollisionEvent",
                        SourceStableId = objectStableId,
                        TargetStableId = collisionStableId,
                        Details = fileStem
                    });
                }
            }

            eventJson.Add(new JsonObject
            {
                ["$GMEvent"] = "v1",
                ["%Name"] = "",
                ["collisionObjectId"] = collisionObjectId,
                ["eventNum"] = eventNumber,
                ["eventType"] = eventTypeNumber,
                ["isDnD"] = false,
                ["name"] = "",
                ["resourceType"] = "GMEvent",
                ["resourceVersion"] = "2.0"
            });
        }

        JsonNode? spriteId = ResolveNamedResourceId(ResourceKind.Sprites, info.SpriteName, reconstructedNames);
        JsonNode? parentObjectId = ResolveNamedResourceId(ResourceKind.Objects, info.ParentObjectName, reconstructedNames);
        JsonNode? maskId = ResolveNamedResourceId(ResourceKind.Sprites, info.CollisionMaskName, reconstructedNames);
        WriteJsonNode(Path.Combine(outputDirectory, relativeYy), CreateObjectJson(name, info, sourceObject, eventJson, spriteId, parentObjectId, maskId));
        string metadataRelative = $"__SplitGM_Metadata/Objects/{name}.details.txt";
        WriteMetadataFile(outputDirectory, metadataRelative, preview.Details);
        files.Add(metadataRelative);

        AddNamedRelationship(ResourceKind.Sprites, info.SpriteName, "ObjectSprite", objectStableId, reconstructedNames, stableIds, relationships);
        AddNamedRelationship(ResourceKind.Objects, info.ParentObjectName, "ObjectParent", objectStableId, reconstructedNames, stableIds, relationships);
        AddNamedRelationship(ResourceKind.Sprites, info.CollisionMaskName, "ObjectCollisionMask", objectStableId, reconstructedNames, stableIds, relationships);

        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = objectStableId,
                SourceType = ResourceKind.Objects.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMObject",
                YypResourcePath = relativeYy,
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = files,
                Warnings = messages.Select(message => message.Message).ToList()
            },
            new YypResourceReference(name, relativeYy, "Objects"),
            relationships,
            messages);
    }

    private ResourceExportOutcome ExportReconstructedRoom(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        IReadOnlyDictionary<int, CodeViewResult> codeViews,
        CancellationToken cancellationToken)
    {
        ResourcePreviewData preview = BuildRoomPreview(entry.Index, cancellationToken);
        RoomPreviewInfo info = preview.Room ?? throw new InvalidDataException("Room metadata could not be read.");
        UndertaleRoom? sourceRoom = GetAt(_data.Rooms, entry.Index) as UndertaleRoom;
        string relativeYy = $"rooms/{name}/{name}.yy";
        string directory = Path.Combine(outputDirectory, "rooms", name);
        Directory.CreateDirectory(directory);
        List<string> files = [relativeYy];
        List<SplitGmProjectRelationship> relationships = [];
        List<SplitGmReconstructionMessage> messages = [];
        string roomStableId = stableIds[(ResourceKind.Rooms, entry.Index)];
        string creationCodeFile = string.Empty;
        string? roomCreationCodeName = sourceRoom?.CreationCodeId?.Name?.Content;
        if (!string.IsNullOrWhiteSpace(roomCreationCodeName))
        {
            int roomCodeIndex = FindCodeIndexByName(roomCreationCodeName);
            string relativeRoomCode = $"rooms/{name}/RoomCreationCode.gml";
            string roomGml;
            if (roomCodeIndex >= 0 && codeViews.TryGetValue(roomCodeIndex, out CodeViewResult? roomCodeView))
            {
                roomGml = roomCodeView.GmlSucceeded
                    ? roomCodeView.Gml
                    : BuildFailedGml(FindCodeEntry(roomCodeIndex), roomCodeView);
                if (!roomCodeView.GmlSucceeded)
                {
                    messages.Add(WarningMessage(
                        "ROOM_CREATION_GML_PARTIAL",
                        $"Room creation code {roomCreationCodeName} could not be reconstructed as safe GML. A failure marker and VM assembly fallback were preserved.",
                        roomStableId,
                        relativeRoomCode));
                }
            }
            else
            {
                roomGml = $"/// @description SplitGM could not recover {roomCreationCodeName}.\n";
                messages.Add(WarningMessage(
                    "ROOM_CREATION_CODE_MISSING",
                    $"No recoverable code entry was found for room creation code {roomCreationCodeName}.",
                    roomStableId,
                    relativeRoomCode));
            }
            File.WriteAllText(Path.Combine(outputDirectory, relativeRoomCode), roomGml, new UTF8Encoding(false));
            files.Add(relativeRoomCode);
            creationCodeFile = $"${{project_dir}}/rooms/{name}/RoomCreationCode.gml";
        }

        List<JsonObject> layers = [];
        List<JsonObject> creationOrder = [];
        List<IGrouping<string, RoomInstanceInfo>> groupedInstances = info.Instances
            .Where(instance => instance.ObjectResourceIndex >= 0)
            .GroupBy(instance => string.IsNullOrWhiteSpace(instance.LayerName) ? "Instances" : instance.LayerName)
            .ToList();

        int layerOrdinal = 0;
        HashSet<string> allocatedLayerNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, RoomInstanceInfo> group in groupedInstances)
        {
            List<JsonObject> instances = [];
            foreach (RoomInstanceInfo instance in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reconstructedNames.TryGetValue((ResourceKind.Objects, instance.ObjectResourceIndex), out string? objectName) ||
                    !stableIds.TryGetValue((ResourceKind.Objects, instance.ObjectResourceIndex), out string? objectStableId))
                {
                    messages.Add(WarningMessage(
                        "ROOM_INSTANCE_OBJECT_MISSING",
                        $"Room instance {instance.InstanceId} referenced object index {instance.ObjectResourceIndex}, which could not be reconstructed.",
                        roomStableId,
                        relativeYy));
                    continue;
                }
                string instanceName = "inst_" + instance.InstanceId.ToString("X8", CultureInfo.InvariantCulture);
                bool hasCreationCode = !string.IsNullOrWhiteSpace(instance.CreationCodeName);
                if (hasCreationCode)
                {
                    int codeIndex = FindCodeIndexByName(instance.CreationCodeName);
                    string relativeCode = $"rooms/{name}/InstanceCreationCode_{instanceName}.gml";
                    string gml;
                    if (codeIndex >= 0 && codeViews.TryGetValue(codeIndex, out CodeViewResult? view))
                    {
                        gml = view.GmlSucceeded
                            ? view.Gml
                            : BuildFailedGml(FindCodeEntry(codeIndex), view);
                        if (!view.GmlSucceeded)
                        {
                            messages.Add(WarningMessage(
                                "ROOM_INSTANCE_GML_PARTIAL",
                                $"Creation code for instance {instance.InstanceId} could not be reconstructed as safe GML. A failure marker and VM assembly fallback were preserved.",
                                roomStableId,
                                relativeCode));
                        }
                    }
                    else
                    {
                        gml = $"/// @description SplitGM could not recover {instance.CreationCodeName}.\n";
                        messages.Add(WarningMessage(
                            "ROOM_INSTANCE_CODE_MISSING",
                            $"No recoverable code entry was found for creation code {instance.CreationCodeName} on instance {instance.InstanceId}.",
                            roomStableId,
                            relativeCode));
                    }
                    File.WriteAllText(Path.Combine(outputDirectory, relativeCode), gml, new UTF8Encoding(false));
                    files.Add(relativeCode);
                }

                instances.Add(new JsonObject
                {
                    ["$GMRInstance"] = "v4",
                    ["%Name"] = instanceName,
                    ["colour"] = 4294967295L,
                    ["frozen"] = false,
                    ["hasCreationCode"] = hasCreationCode,
                    ["ignore"] = false,
                    ["imageIndex"] = instance.ImageIndex,
                    ["imageSpeed"] = 1.0,
                    ["inheritCode"] = false,
                    ["inheritedItemId"] = null,
                    ["inheritItemSettings"] = false,
                    ["isDnd"] = false,
                    ["name"] = instanceName,
                    ["objectId"] = ResourceId(objectName, $"objects/{objectName}/{objectName}.yy"),
                    ["properties"] = new JsonArray(),
                    ["resourceType"] = "GMRInstance",
                    ["resourceVersion"] = "2.0",
                    ["rotation"] = instance.Rotation,
                    ["scaleX"] = instance.ScaleX,
                    ["scaleY"] = instance.ScaleY,
                    ["x"] = instance.X,
                    ["y"] = instance.Y
                });
                creationOrder.Add(ResourceId(instanceName, relativeYy));
                relationships.Add(new SplitGmProjectRelationship
                {
                    Kind = "RoomInstanceObject",
                    SourceStableId = roomStableId,
                    TargetStableId = objectStableId,
                    Details = $"Instance {instance.InstanceId} on layer {group.Key}"
                });
            }

            string layerName = AllocateIdentifier(group.Key, "Instances", layerOrdinal, allocatedLayerNames);
            int depth = info.Layers.FirstOrDefault(layer => layer.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))?.Depth
                        ?? layerOrdinal * 100;
            layers.Add(CreateInstanceLayerJson(layerName, depth, instances));
            layerOrdinal++;
        }

        int reconstructedSourceLayerCount = groupedInstances.Count;
        if (layers.Count == 0)
            layers.Add(CreateInstanceLayerJson("Instances", 0, []));

        if (sourceRoom?.DrawBackgroundColor == true)
        {
            int backgroundDepth = info.Layers.Count > 0
                ? info.Layers.Max(layer => layer.Depth) + 100
                : 100;
            string backgroundLayerName = AllocateIdentifier(
                "BackgroundColor",
                "BackgroundColor",
                layerOrdinal,
                allocatedLayerNames);
            layers.Add(CreateBackgroundLayerJson(
                backgroundLayerName,
                backgroundDepth,
                unchecked((long)sourceRoom.BackgroundColor)));
        }

        int omittedLayerCount = Math.Max(0, info.Layers.Count - reconstructedSourceLayerCount);
        if (omittedLayerCount > 0)
        {
            messages.Add(WarningMessage(
                "ROOM_LAYER_FALLBACK",
                $"{omittedLayerCount:N0} non-instance or empty room layer(s) were preserved in .splitgmproj metadata but were not recreated as editable GameMaker layers.",
                roomStableId,
                relativeYy));
        }
        if (info.Tiles.Count > 0)
        {
            messages.Add(WarningMessage(
                "ROOM_TILES_NOT_REPRESENTED",
                $"The room contains {info.Tiles.Count:N0} compiled tile entry/entries. They were preserved in metadata and preview output but not written as editable tile-layer data.",
                roomStableId,
                relativeYy));
        }

        int legacyBackgroundCount = sourceRoom is null
            ? 0
            : sourceRoom.Backgrounds.Count(background =>
                background is not null && background.Enabled && background.BackgroundDefinition is not null);
        if (legacyBackgroundCount > 0)
        {
            messages.Add(WarningMessage(
                "ROOM_LEGACY_BACKGROUNDS_NOT_REPRESENTED",
                $"The room contains {legacyBackgroundCount:N0} enabled legacy background slot(s). Their metadata was preserved, but their sprite placement was not converted into editable modern room layers.",
                roomStableId,
                relativeYy));
        }

        HashSet<int> viewFollowObjectIndexes = [];
        if (sourceRoom?.Views is not null)
        {
            foreach (UndertaleRoom.View? view in sourceRoom.Views)
            {
                if (view?.ObjectId is null)
                    continue;
                int objectIndex = IndexOfReference(_data.GameObjects, view.ObjectId);
                if (objectIndex >= 0 &&
                    viewFollowObjectIndexes.Add(objectIndex) &&
                    stableIds.TryGetValue((ResourceKind.Objects, objectIndex), out string? objectStableId))
                {
                    relationships.Add(new SplitGmProjectRelationship
                    {
                        Kind = "RoomViewFollowObject",
                        SourceStableId = roomStableId,
                        TargetStableId = objectStableId,
                        Details = $"View follows source object index {objectIndex}"
                    });
                }
                else if (objectIndex < 0)
                {
                    messages.Add(WarningMessage(
                        "ROOM_VIEW_TARGET_MISSING",
                        "A room view referenced a follow object that could not be resolved in the reconstructed object table.",
                        roomStableId,
                        relativeYy));
                }
            }
        }

        WriteJsonNode(
            Path.Combine(outputDirectory, relativeYy),
            CreateRoomJson(name, info, sourceRoom, reconstructedNames, layers, creationOrder, creationCodeFile));
        string previewRelative = $"__SplitGM_Metadata/Rooms/{name}.preview.png";
        if (preview.ImagePng is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outputDirectory, previewRelative))!);
            File.WriteAllBytes(Path.Combine(outputDirectory, previewRelative), preview.ImagePng);
            files.Add(previewRelative);
        }
        string metadataRelative = $"__SplitGM_Metadata/Rooms/{name}.room.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(outputDirectory, metadataRelative))!);
        WriteReconstructionJson(Path.Combine(outputDirectory, metadataRelative), info);
        files.Add(metadataRelative);

        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = roomStableId,
                SourceType = ResourceKind.Rooms.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMRoom (instances and room dimensions reconstructed; complex layers may require repair)",
                YypResourcePath = relativeYy,
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = files,
                Warnings = messages.Select(message => message.Message).ToList()
            },
            new YypResourceReference(name, relativeYy, "Rooms"),
            relationships,
            messages);
    }

    private ResourceExportOutcome ExportReconstructedAudioGroup(
        string outputDirectory,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds)
    {
        string stableId = stableIds[(ResourceKind.AudioGroups, entry.Index)];
        string metadataRelative = $"__SplitGM_Metadata/AudioGroups/{name}.details.txt";
        WriteMetadataFile(outputDirectory, metadataRelative, GetResourceDetails(ResourceKind.AudioGroups, entry.Index));
        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = stableId,
                SourceType = ResourceKind.AudioGroups.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "GMAudioGroup (top-level .yyp entry)",
                RepresentedInYyp = true,
                ExportSucceeded = true,
                Files = [metadataRelative]
            },
            null,
            [],
            []);
    }

    private ResourceExportOutcome ExportFallbackResource(
        string outputDirectory,
        ResourceKind kind,
        ResourceEntryInfo entry,
        string name,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        ReconstructedProjectOptions options,
        CancellationToken cancellationToken)
    {
        string stableId = stableIds[(kind, entry.Index)];
        string fallbackRoot = Path.Combine(outputDirectory, "__SplitGM_Unrepresented", GetResourceCategoryDirectoryName(kind));
        Directory.CreateDirectory(fallbackRoot);
        List<string> files = [];
        List<SplitGmReconstructionMessage> messages = [];

        if (options.ExportRawFallbacks)
        {
            ExportSelectedResource(kind, entry.Index, fallbackRoot, null, cancellationToken);

            // ExportSelectedResource gives every resource a stable index/name prefix. Collect
            // only that resource's files so fallback sprites can safely export in parallel.
            string exportPrefix = $"{entry.Index:D6}_{OutputPathHelper.SafeFileName(entry.Name)}";
            files.AddRange(Directory.EnumerateFiles(fallbackRoot, exportPrefix + "*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/')));
            string resourceDirectory = Path.Combine(fallbackRoot, exportPrefix);
            if (Directory.Exists(resourceDirectory))
            {
                files.AddRange(Directory.EnumerateFiles(resourceDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/')));
            }
        }

        string fallbackPath = files.FirstOrDefault() ?? $"__SplitGM_Unrepresented/{GetResourceCategoryDirectoryName(kind)}";
        messages.Add(WarningMessage(
            "RESOURCE_NOT_SAFELY_REPRESENTABLE",
            $"{kind} resource {entry.Name} was exported as inspectable fallback data but was not added to the .yyp because SplitGM v0.5.0 cannot represent this compiled resource type safely yet.",
            stableId,
            fallbackPath));

        return new ResourceExportOutcome(
            new SplitGmProjectResource
            {
                StableId = stableId,
                SourceType = kind.ToString(),
                SourceName = entry.Name,
                SourceIndex = entry.Index,
                ReconstructedName = name,
                Representation = "Raw/inspectable fallback",
                FallbackPath = fallbackPath,
                RepresentedInYyp = false,
                ExportSucceeded = true,
                Files = files,
                Warnings = [messages[0].Message]
            },
            null,
            [],
            messages);
    }

    private static JsonObject CreateProjectJson(
        string projectName,
        ReconstructionTargetProfile profile,
        IEnumerable<YypResourceReference> resources,
        IEnumerable<YypResourceReference> rooms,
        IEnumerable<YypFolderDefinition> folders,
        IEnumerable<JsonObject> audioGroups)
    {
        return new JsonObject
        {
            ["$GMProject"] = "v1",
            ["%Name"] = projectName,
            ["AudioGroups"] = new JsonArray(audioGroups.Select(CloneNode).ToArray()),
            ["configs"] = new JsonObject
            {
                ["children"] = new JsonArray(),
                ["name"] = "Default"
            },
            ["defaultScriptType"] = 0,
            ["Folders"] = new JsonArray(folders.Select(folder => (JsonNode?)new JsonObject
            {
                ["$GMFolder"] = "",
                ["%Name"] = folder.Name,
                ["folderPath"] = folder.Path,
                ["name"] = folder.Name,
                ["resourceType"] = "GMFolder",
                ["resourceVersion"] = "2.0"
            }).ToArray()),
            ["ForcedPrefabProjectReferences"] = new JsonArray(),
            ["IncludedFiles"] = new JsonArray(),
            ["isEcma"] = false,
            ["LibraryEmitters"] = new JsonArray(),
            ["MetaData"] = new JsonObject { ["IDEVersion"] = profile.IdeVersion },
            ["name"] = projectName,
            ["resources"] = new JsonArray(resources.Select(resource => (JsonNode?)new JsonObject
            {
                ["id"] = ResourceId(resource.Name, resource.Path)
            }).ToArray()),
            ["resourceType"] = "GMProject",
            ["resourceVersion"] = "2.0",
            ["RoomOrderNodes"] = new JsonArray(rooms.Select(room => (JsonNode?)new JsonObject
            {
                ["roomId"] = ResourceId(room.Name, room.Path)
            }).ToArray()),
            ["templateType"] = "game",
            ["TextureGroups"] = new JsonArray(new JsonObject
            {
                ["$GMTextureGroup"] = "",
                ["%Name"] = "Default",
                ["autocrop"] = true,
                ["border"] = 2,
                ["compressFormat"] = "bz2",
                ["customOptions"] = "",
                ["directory"] = "",
                ["groupParent"] = null,
                ["isScaled"] = true,
                ["loadType"] = "default",
                ["mipsToGenerate"] = 0,
                ["name"] = "Default",
                ["resourceType"] = "GMTextureGroup",
                ["resourceVersion"] = "2.0",
                ["targets"] = -1
            })
        };
    }

    private static JsonObject CreateResourceOrderJson(
        IEnumerable<YypResourceReference> resources,
        IEnumerable<YypFolderDefinition> folders)
    {
        return new JsonObject
        {
            ["FolderOrderSettings"] = new JsonArray(folders.Select((folder, index) => (JsonNode?)new JsonObject
            {
                ["name"] = folder.Name,
                ["order"] = index + 1,
                ["path"] = folder.Path
            }).ToArray()),
            ["ResourceOrderSettings"] = new JsonArray(resources
                .GroupBy(resource => resource.Folder)
                .SelectMany(group => group.Select((resource, index) => (JsonNode?)new JsonObject
                {
                    ["name"] = resource.Name,
                    ["order"] = index + 1,
                    ["path"] = resource.Path
                })).ToArray())
        };
    }

    private static JsonObject CreateScriptJson(string name) => new()
    {
        ["$GMScript"] = "v1",
        ["%Name"] = name,
        ["isCompatibility"] = false,
        ["isDnD"] = false,
        ["name"] = name,
        ["parent"] = FolderId("Scripts"),
        ["resourceType"] = "GMScript",
        ["resourceVersion"] = "2.0"
    };

    private static JsonObject CreateObjectJson(
        string name,
        ObjectPreviewInfo info,
        UndertaleGameObject? sourceObject,
        IEnumerable<JsonObject> events,
        JsonNode? spriteId,
        JsonNode? parentObjectId,
        JsonNode? maskId)
    {
        return new JsonObject
        {
            ["$GMObject"] = "",
            ["%Name"] = name,
            ["eventList"] = new JsonArray(events.Select(CloneNode).ToArray()),
            ["managed"] = sourceObject?.Managed ?? true,
            ["name"] = name,
            ["overriddenProperties"] = new JsonArray(),
            ["parent"] = FolderId("Objects"),
            ["parentObjectId"] = parentObjectId,
            ["persistent"] = info.Persistent,
            ["physicsAngularDamping"] = sourceObject?.AngularDamping ?? 0.1f,
            ["physicsDensity"] = sourceObject?.Density ?? 0.5f,
            ["physicsFriction"] = sourceObject?.Friction ?? 0.2f,
            ["physicsGroup"] = sourceObject is null ? 0 : (long)sourceObject.Group,
            ["physicsKinematic"] = sourceObject?.Kinematic ?? false,
            ["physicsLinearDamping"] = sourceObject?.LinearDamping ?? 0.1f,
            ["physicsObject"] = sourceObject?.UsesPhysics ?? false,
            ["physicsRestitution"] = sourceObject?.Restitution ?? 0.1f,
            ["physicsSensor"] = sourceObject?.IsSensor ?? false,
            ["physicsShape"] = sourceObject is null ? 1 : (int)sourceObject.CollisionShape,
            ["physicsShapePoints"] = CreatePhysicsShapePoints(sourceObject),
            ["physicsStartAwake"] = sourceObject?.Awake ?? true,
            ["properties"] = new JsonArray(),
            ["resourceType"] = "GMObject",
            ["resourceVersion"] = "2.0",
            ["solid"] = info.Solid,
            ["spriteId"] = spriteId,
            ["spriteMaskId"] = maskId,
            ["visible"] = info.Visible
        };
    }

    private static JsonArray CreatePhysicsShapePoints(UndertaleGameObject? sourceObject)
    {
        JsonArray points = new();
        if (sourceObject?.PhysicsVertices is null)
            return points;
        foreach (UndertaleGameObject.UndertalePhysicsVertex? vertex in sourceObject.PhysicsVertices)
        {
            if (vertex is null)
                continue;
            points.Add(new JsonObject
            {
                ["x"] = vertex.X,
                ["y"] = vertex.Y
            });
        }
        return points;
    }

    private JsonObject CreateRoomJson(
        string name,
        RoomPreviewInfo info,
        UndertaleRoom? sourceRoom,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IEnumerable<JsonObject> layers,
        IEnumerable<JsonObject> creationOrder,
        string creationCodeFile)
    {
        JsonArray views = CreateRoomViews(sourceRoom, reconstructedNames);
        bool enableViews = sourceRoom?.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.EnableViews) ?? false;
        bool clearDisplayBuffer = sourceRoom is null ||
                                  !sourceRoom.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.DoNotClearDisplayBuffer);
        bool clearViewBackground = sourceRoom?.Flags.HasFlag(UndertaleRoom.RoomEntryFlags.ClearViewBackground) ?? true;

        return new JsonObject
        {
            ["$GMRoom"] = "v1",
            ["%Name"] = name,
            ["creationCodeFile"] = creationCodeFile,
            ["inheritCode"] = false,
            ["inheritCreationOrder"] = false,
            ["inheritLayers"] = false,
            ["instanceCreationOrder"] = new JsonArray(creationOrder.Select(CloneNode).ToArray()),
            ["isDnd"] = false,
            ["layers"] = new JsonArray(layers.Select(CloneNode).ToArray()),
            ["name"] = name,
            ["parent"] = FolderId("Rooms"),
            ["parentRoom"] = null,
            ["physicsSettings"] = new JsonObject
            {
                ["inheritPhysicsSettings"] = false,
                ["PhysicsWorld"] = sourceRoom?.World ?? false,
                ["PhysicsWorldGravityX"] = sourceRoom?.GravityX ?? 0.0f,
                ["PhysicsWorldGravityY"] = sourceRoom?.GravityY ?? 10.0f,
                ["PhysicsWorldPixToMetres"] = sourceRoom?.MetersPerPixel ?? 0.1f
            },
            ["resourceType"] = "GMRoom",
            ["resourceVersion"] = "2.0",
            ["roomSettings"] = new JsonObject
            {
                ["Height"] = (long)info.Height,
                ["inheritRoomSettings"] = false,
                ["persistent"] = info.Persistent,
                ["Width"] = (long)info.Width
            },
            ["sequenceId"] = null,
            ["views"] = views,
            ["viewSettings"] = new JsonObject
            {
                ["clearDisplayBuffer"] = clearDisplayBuffer,
                ["clearViewBackground"] = clearViewBackground,
                ["enableViews"] = enableViews,
                ["inheritViewSettings"] = false
            },
            ["volume"] = 1.0
        };
    }

    private JsonArray CreateRoomViews(
        UndertaleRoom? sourceRoom,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames)
    {
        JsonArray views = [];
        for (int index = 0; index < 8; index++)
        {
            UndertaleRoom.View? sourceView = sourceRoom is not null && index < sourceRoom.Views.Count
                ? sourceRoom.Views[index]
                : null;

            JsonNode? objectId = null;
            if (sourceView?.ObjectId is not null)
            {
                int objectIndex = IndexOfReference(_data.GameObjects, sourceView.ObjectId);
                if (objectIndex >= 0 &&
                    reconstructedNames.TryGetValue((ResourceKind.Objects, objectIndex), out string? objectName))
                {
                    objectId = ResourceId(objectName, $"objects/{objectName}/{objectName}.yy");
                }
            }

            views.Add(new JsonObject
            {
                ["hborder"] = sourceView is null ? 32 : (long)sourceView.BorderX,
                ["hport"] = sourceView?.PortHeight ?? 768,
                ["hspeed"] = sourceView?.SpeedX ?? -1,
                ["hview"] = sourceView?.ViewHeight ?? 768,
                ["inherit"] = false,
                ["objectId"] = objectId,
                ["vborder"] = sourceView is null ? 32 : (long)sourceView.BorderY,
                ["visible"] = sourceView?.Enabled ?? false,
                ["vspeed"] = sourceView?.SpeedY ?? -1,
                ["wport"] = sourceView?.PortWidth ?? 1366,
                ["wview"] = sourceView?.ViewWidth ?? 1366,
                ["xport"] = sourceView?.PortX ?? 0,
                ["xview"] = sourceView?.ViewX ?? 0,
                ["yport"] = sourceView?.PortY ?? 0,
                ["yview"] = sourceView?.ViewY ?? 0
            });
        }
        return views;
    }

    private static JsonObject CreateBackgroundLayerJson(string name, int depth, long colour)
    {
        return new JsonObject
        {
            ["$GMRBackgroundLayer"] = "",
            ["%Name"] = name,
            ["animationFPS"] = 30.0,
            ["animationSpeedType"] = 0,
            ["colour"] = colour,
            ["depth"] = depth,
            ["effectEnabled"] = true,
            ["effectType"] = null,
            ["gridX"] = 32,
            ["gridY"] = 32,
            ["hierarchyFrozen"] = false,
            ["hspeed"] = 0.0,
            ["htiled"] = false,
            ["inheritLayerDepth"] = false,
            ["inheritLayerSettings"] = false,
            ["inheritSubLayers"] = true,
            ["inheritVisibility"] = true,
            ["layers"] = new JsonArray(),
            ["name"] = name,
            ["properties"] = new JsonArray(),
            ["resourceType"] = "GMRBackgroundLayer",
            ["resourceVersion"] = "2.0",
            ["spriteId"] = null,
            ["stretch"] = false,
            ["userdefinedAnimFPS"] = false,
            ["userdefinedDepth"] = false,
            ["visible"] = true,
            ["vspeed"] = 0.0,
            ["vtiled"] = false,
            ["x"] = 0,
            ["y"] = 0
        };
    }

    private static JsonObject CreateInstanceLayerJson(string name, int depth, IEnumerable<JsonObject> instances)
    {
        return new JsonObject
        {
            ["$GMRInstanceLayer"] = "",
            ["%Name"] = name,
            ["depth"] = depth,
            ["effectEnabled"] = true,
            ["effectType"] = null,
            ["gridX"] = 32,
            ["gridY"] = 32,
            ["hierarchyFrozen"] = false,
            ["inheritLayerDepth"] = false,
            ["inheritLayerSettings"] = false,
            ["inheritSubLayers"] = true,
            ["inheritVisibility"] = true,
            ["instances"] = new JsonArray(instances.Select(CloneNode).ToArray()),
            ["layers"] = new JsonArray(),
            ["name"] = name,
            ["properties"] = new JsonArray(),
            ["resourceType"] = "GMRInstanceLayer",
            ["resourceVersion"] = "2.0",
            ["userdefinedDepth"] = false,
            ["visible"] = true
        };
    }

    private static JsonObject CreateSoundJson(
        string name,
        string soundFile,
        string groupName,
        UndertaleSound sound) => new()
    {
        ["$GMSound"] = "v2",
        ["%Name"] = name,
        ["audioGroupId"] = ResourceId(groupName, $"audiogroups/{groupName}"),
        ["bitDepth"] = 1,
        ["channelFormat"] = 0,
        ["compression"] = 0,
        ["compressionQuality"] = 4,
        ["conversionMode"] = 0,
        ["duration"] = Math.Max(0.0f, sound.AudioLength),
        ["exportDir"] = "",
        ["name"] = name,
        ["parent"] = FolderId("Sounds"),
        ["preload"] = sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.Regular)
            ? false
            : sound.Preload,
        ["resourceType"] = "GMSound",
        ["resourceVersion"] = "2.0",
        ["sampleRate"] = 44100,
        ["soundFile"] = soundFile,
        ["volume"] = Math.Clamp(sound.Volume, 0.0f, 1.0f)
    };

    private static JsonObject CreatePathJson(string name, UndertalePath path)
    {
        JsonArray points = [];
        if (path.Points is not null)
        {
            foreach (UndertalePath.PathPoint? point in path.Points)
            {
                if (point is null)
                    continue;
                points.Add(new JsonObject
                {
                    ["speed"] = point.Speed,
                    ["x"] = point.X,
                    ["y"] = point.Y
                });
            }
        }
        return new JsonObject
        {
            ["$GMPath"] = "",
            ["%Name"] = name,
            ["closed"] = path.IsClosed,
            ["kind"] = path.IsSmooth ? 1 : 0,
            ["name"] = name,
            ["parent"] = FolderId("Paths"),
            ["points"] = points,
            ["precision"] = path.Precision,
            ["resourceType"] = "GMPath",
            ["resourceVersion"] = "2.0"
        };
    }

    private static JsonObject CreateSpriteJson(
        string name,
        UndertaleSprite sprite,
        IReadOnlyList<string> frameIds,
        string layerId)
    {
        JsonArray frames = new(frameIds.Select(frameId => (JsonNode?)new JsonObject
        {
            ["$GMSpriteFrame"] = "v1",
            ["%Name"] = frameId,
            ["name"] = frameId,
            ["resourceType"] = "GMSpriteFrame",
            ["resourceVersion"] = "2.0"
        }).ToArray());
        JsonArray keyframes = [];
        for (int index = 0; index < frameIds.Count; index++)
        {
            string frameId = frameIds[index];
            keyframes.Add(new JsonObject
            {
                ["$Keyframe<SpriteFrameKeyframe>"] = "",
                ["Channels"] = new JsonObject
                {
                    ["0"] = new JsonObject
                    {
                        ["$SpriteFrameKeyframe"] = "",
                        ["Id"] = ResourceId(frameId, $"sprites/{name}/{name}.yy"),
                        ["resourceType"] = "SpriteFrameKeyframe",
                        ["resourceVersion"] = "2.0"
                    }
                },
                ["Disabled"] = false,
                ["id"] = StableGuid("SpriteSequenceKey", index, name),
                ["IsCreationKey"] = false,
                ["Key"] = (double)index,
                ["Length"] = 1.0,
                ["resourceType"] = "Keyframe<SpriteFrameKeyframe>",
                ["resourceVersion"] = "2.0",
                ["Stretch"] = false
            });
        }

        int bboxLeft = Convert.ToInt32(sprite.MarginLeft);
        int bboxRight = Convert.ToInt32(sprite.MarginRight);
        int bboxTop = Convert.ToInt32(sprite.MarginTop);
        int bboxBottom = Convert.ToInt32(sprite.MarginBottom);
        return new JsonObject
        {
            ["$GMSprite"] = "v2",
            ["%Name"] = name,
            ["bboxMode"] = Convert.ToInt32(sprite.BBoxMode),
            ["bbox_bottom"] = bboxBottom,
            ["bbox_left"] = bboxLeft,
            ["bbox_right"] = bboxRight,
            ["bbox_top"] = bboxTop,
            ["collisionKind"] = (int)sprite.SepMasks,
            ["collisionTolerance"] = 0,
            ["DynamicTexturePage"] = false,
            ["edgeFiltering"] = sprite.Smooth,
            ["For3D"] = false,
            ["frames"] = frames,
            ["gridX"] = 0,
            ["gridY"] = 0,
            ["height"] = Convert.ToInt32(sprite.Height),
            ["HTile"] = false,
            ["layers"] = new JsonArray(new JsonObject
            {
                ["$GMImageLayer"] = "",
                ["%Name"] = layerId,
                ["blendMode"] = 0,
                ["displayName"] = "default",
                ["isLocked"] = false,
                ["name"] = layerId,
                ["opacity"] = 100.0,
                ["resourceType"] = "GMImageLayer",
                ["resourceVersion"] = "2.0",
                ["visible"] = true
            }),
            ["name"] = name,
            ["nineSlice"] = null,
            ["origin"] = 9,
            ["parent"] = FolderId("Sprites"),
            ["preMultiplyAlpha"] = false,
            ["resourceType"] = "GMSprite",
            ["resourceVersion"] = "2.0",
            ["sequence"] = new JsonObject
            {
                ["$GMSequence"] = "v1",
                ["%Name"] = name,
                ["autoRecord"] = true,
                ["backdropHeight"] = 768,
                ["backdropImageOpacity"] = 0.5,
                ["backdropImagePath"] = "",
                ["backdropWidth"] = 1366,
                ["backdropXOffset"] = 0.0,
                ["backdropYOffset"] = 0.0,
                ["events"] = EmptyKeyframeStore("MessageEventKeyframe"),
                ["eventStubScript"] = null,
                ["eventToFunction"] = new JsonObject(),
                ["length"] = Math.Max(1, frameIds.Count),
                ["lockOrigin"] = false,
                ["moments"] = EmptyKeyframeStore("MomentsEventKeyframe"),
                ["name"] = name,
                ["playback"] = 1,
                ["playbackSpeed"] = sprite.GMS2PlaybackSpeed,
                ["playbackSpeedType"] = (int)sprite.GMS2PlaybackSpeedType,
                ["resourceType"] = "GMSequence",
                ["resourceVersion"] = "2.0",
                ["showBackdrop"] = true,
                ["showBackdropImage"] = false,
                ["timeUnits"] = 1,
                ["tracks"] = new JsonArray(new JsonObject
                {
                    ["$GMSpriteFramesTrack"] = "",
                    ["builtinName"] = 0,
                    ["events"] = new JsonArray(),
                    ["inheritsTrackColour"] = true,
                    ["interpolation"] = 1,
                    ["isCreationTrack"] = false,
                    ["keyframes"] = new JsonObject
                    {
                        ["$KeyframeStore<SpriteFrameKeyframe>"] = "",
                        ["Keyframes"] = keyframes,
                        ["resourceType"] = "KeyframeStore<SpriteFrameKeyframe>",
                        ["resourceVersion"] = "2.0"
                    },
                    ["modifiers"] = new JsonArray(),
                    ["name"] = "frames",
                    ["resourceType"] = "GMSpriteFramesTrack",
                    ["resourceVersion"] = "2.0",
                    ["spriteId"] = null,
                    ["trackColour"] = 0,
                    ["tracks"] = new JsonArray(),
                    ["traits"] = 0
                }),
                ["visibleRange"] = null,
                ["volume"] = 1.0,
                ["xorigin"] = sprite.OriginXWrapper,
                ["yorigin"] = sprite.OriginYWrapper
            },
            ["swatchColours"] = null,
            ["swfPrecision"] = 0.5,
            ["textureGroupId"] = ResourceId("Default", "texturegroups/Default"),
            ["type"] = 0,
            ["VTile"] = false,
            ["width"] = Convert.ToInt32(sprite.Width)
        };
    }

    private static JsonObject EmptyKeyframeStore(string typeName) => new()
    {
        ["$KeyframeStore<" + typeName + ">"] = "",
        ["Keyframes"] = new JsonArray(),
        ["resourceType"] = "KeyframeStore<" + typeName + ">",
        ["resourceVersion"] = "2.0"
    };

    private List<JsonObject> BuildAudioGroups(
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames)
    {
        List<JsonObject> groups = [];
        IReadOnlyList<ResourceEntryInfo> entries = GetResourceEntries(ResourceKind.AudioGroups);
        if (entries.Count == 0)
        {
            groups.Add(CreateAudioGroupJson("audiogroup_default"));
            return groups;
        }
        foreach (ResourceEntryInfo entry in entries)
            groups.Add(CreateAudioGroupJson(reconstructedNames[(ResourceKind.AudioGroups, entry.Index)]));
        if (!groups.Any(group => string.Equals(group["name"]?.GetValue<string>(), "audiogroup_default", StringComparison.OrdinalIgnoreCase)))
            groups.Insert(0, CreateAudioGroupJson("audiogroup_default"));
        return groups;
    }

    private static JsonObject CreateAudioGroupJson(string name) => new()
    {
        ["$GMAudioGroup"] = "v1",
        ["%Name"] = name,
        ["exportDir"] = "",
        ["name"] = name,
        ["resourceType"] = "GMAudioGroup",
        ["resourceVersion"] = "2.0",
        ["targets"] = -1
    };

    private static List<YypFolderDefinition> BuildDefaultFolders() =>
    [
        new("Scripts", "folders/Scripts.yy"),
        new("Objects", "folders/Objects.yy"),
        new("Rooms", "folders/Rooms.yy"),
        new("Sprites", "folders/Sprites.yy"),
        new("Sounds", "folders/Sounds.yy"),
        new("Paths", "folders/Paths.yy")
    ];

    private static void WriteFolderFiles(string outputDirectory, IEnumerable<YypFolderDefinition> folders)
    {
        foreach (YypFolderDefinition folder in folders)
        {
            string path = Path.Combine(outputDirectory, folder.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteJsonNode(path, new JsonObject
            {
                ["$GMFolder"] = "",
                ["%Name"] = folder.Name,
                ["folderPath"] = folder.Path,
                ["name"] = folder.Name,
                ["resourceType"] = "GMFolder",
                ["resourceVersion"] = "2.0"
            });
        }
    }

    private static SplitGmValidationSummary ValidateReconstructedProject(
        string outputDirectory,
        string projectFilePath,
        string intermediateFilePath,
        IReadOnlyList<YypResourceReference> resources,
        IReadOnlyList<SplitGmProjectResource> projectResources,
        IReadOnlyList<SplitGmProjectRelationship> relationships,
        List<SplitGmReconstructionMessage> messages)
    {
        SplitGmValidationSummary summary = new();
        summary.ChecksPerformed++;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectFilePath));
            summary.ProjectJsonParsed = true;
            summary.PassedChecks++;
        }
        catch (Exception exception)
        {
            messages.Add(new SplitGmReconstructionMessage
            {
                Severity = "Error",
                Code = "YYP_JSON_INVALID",
                Message = "The generated .yyp could not be parsed as JSON: " + exception.Message,
                Path = Path.GetFileName(projectFilePath)
            });
        }

        summary.ChecksPerformed++;
        try
        {
            using JsonDocument intermediateDocument = JsonDocument.Parse(File.ReadAllText(intermediateFilePath));
            summary.IntermediateProjectJsonParsed = true;
            summary.PassedChecks++;

            summary.ChecksPerformed++;
            JsonElement root = intermediateDocument.RootElement;
            bool recognized = root.TryGetProperty("FormatVersion", out JsonElement formatVersion) &&
                              string.Equals(formatVersion.GetString(), "1.0", StringComparison.Ordinal) &&
                              root.TryGetProperty("Format", out JsonElement formatName) &&
                              string.Equals(formatName.GetString(), "SplitGM Reconstructed Project", StringComparison.Ordinal);
            summary.IntermediateFormatVersionRecognized = recognized;
            if (recognized)
                summary.PassedChecks++;
            else
            {
                messages.Add(new SplitGmReconstructionMessage
                {
                    Severity = "Error",
                    Code = "SPLITGMPROJ_FORMAT_UNRECOGNIZED",
                    Message = "The generated .splitgmproj does not identify itself as SplitGM Reconstructed Project format version 1.0.",
                    Path = Path.GetFileName(intermediateFilePath)
                });
            }
        }
        catch (Exception exception)
        {
            messages.Add(new SplitGmReconstructionMessage
            {
                Severity = "Error",
                Code = "SPLITGMPROJ_JSON_INVALID",
                Message = "The generated .splitgmproj could not be parsed as JSON: " + exception.Message,
                Path = Path.GetFileName(intermediateFilePath)
            });
            // Keep the check count stable even if parsing failed: the format-version
            // check cannot pass without a readable document.
            summary.ChecksPerformed++;
        }

        summary.ChecksPerformed++;
        string[] missing = resources
            .Where(resource => !File.Exists(Path.Combine(outputDirectory, resource.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(resource => resource.Path)
            .ToArray();
        summary.EveryListedResourceExists = missing.Length == 0;
        if (missing.Length == 0)
            summary.PassedChecks++;
        else
        {
            foreach (string path in missing)
            {
                messages.Add(new SplitGmReconstructionMessage
                {
                    Severity = "Error",
                    Code = "YYP_RESOURCE_MISSING",
                    Message = "A resource listed by the .yyp does not exist on disk.",
                    Path = path
                });
            }
        }

        summary.ChecksPerformed++;
        int resourceJsonErrors = 0;
        foreach (YypResourceReference resource in resources)
        {
            string resourcePath = Path.Combine(outputDirectory, resource.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(resourcePath))
                continue;
            summary.ResourceJsonFilesChecked++;
            try
            {
                using JsonDocument resourceDocument = JsonDocument.Parse(File.ReadAllText(resourcePath));
            }
            catch (Exception exception)
            {
                resourceJsonErrors++;
                messages.Add(new SplitGmReconstructionMessage
                {
                    Severity = "Error",
                    Code = "YYP_RESOURCE_JSON_INVALID",
                    Message = $"The generated resource JSON for {resource.Name} could not be parsed: {exception.Message}",
                    Path = resource.Path
                });
            }
        }
        summary.ResourceJsonParseErrors = resourceJsonErrors;
        summary.EveryListedResourceJsonParsed = resourceJsonErrors == 0;
        if (resourceJsonErrors == 0)
            summary.PassedChecks++;

        summary.ChecksPerformed++;
        string[] recordedOutputFiles = projectResources
            .SelectMany(resource => resource.Files)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        summary.RecordedOutputFilesChecked = recordedOutputFiles.Length;
        string[] missingRecordedOutputFiles = recordedOutputFiles
            .Where(path => !File.Exists(Path.Combine(
                outputDirectory,
                path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();
        summary.MissingRecordedOutputFiles = missingRecordedOutputFiles.Length;
        summary.EveryRecordedOutputFileExists = missingRecordedOutputFiles.Length == 0;
        if (missingRecordedOutputFiles.Length == 0)
        {
            summary.PassedChecks++;
        }
        else
        {
            foreach (string path in missingRecordedOutputFiles)
            {
                messages.Add(new SplitGmReconstructionMessage
                {
                    Severity = "Error",
                    Code = "SPLITGMPROJ_OUTPUT_FILE_MISSING",
                    Message = "A file recorded by the .splitgmproj resource manifest does not exist on disk.",
                    Path = path
                });
            }
        }

        summary.ChecksPerformed++;
        string[] duplicatePaths = resources.GroupBy(resource => resource.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        summary.ResourcePathsAreUnique = duplicatePaths.Length == 0;
        if (duplicatePaths.Length == 0)
            summary.PassedChecks++;
        else
        {
            foreach (string path in duplicatePaths)
                messages.Add(new SplitGmReconstructionMessage { Severity = "Error", Code = "YYP_DUPLICATE_RESOURCE_PATH", Message = "The .yyp contains a duplicate resource path.", Path = path });
        }

        summary.ChecksPerformed++;
        string[] duplicateNames = resources.GroupBy(resource => (resource.Folder, resource.Name), ResourceNameTupleComparer.Instance)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key.Folder + ":" + group.Key.Name)
            .ToArray();
        summary.ResourceNamesAreUniqueWithinType = duplicateNames.Length == 0;
        if (duplicateNames.Length == 0)
            summary.PassedChecks++;
        else
        {
            foreach (string name in duplicateNames)
                messages.Add(new SplitGmReconstructionMessage { Severity = "Error", Code = "YYP_DUPLICATE_RESOURCE_NAME", Message = "The .yyp contains a duplicate resource name within a resource type: " + name });
        }

        summary.ChecksPerformed++;
        string[] duplicateStableIds = projectResources
            .GroupBy(resource => resource.StableId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        summary.StableResourceIdsAreUnique = duplicateStableIds.Length == 0;
        if (duplicateStableIds.Length == 0)
            summary.PassedChecks++;
        else
        {
            foreach (string stableId in duplicateStableIds)
                messages.Add(new SplitGmReconstructionMessage { Severity = "Error", Code = "SPLITGMPROJ_DUPLICATE_STABLE_ID", Message = "The intermediate project contains a duplicate stable resource ID.", ResourceStableId = stableId });
        }

        summary.ChecksPerformed++;
        HashSet<string> stableIds = projectResources.Select(resource => resource.StableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SplitGmProjectRelationship[] brokenRelationships = relationships
            .Where(relationship => !stableIds.Contains(relationship.SourceStableId) || !stableIds.Contains(relationship.TargetStableId))
            .ToArray();
        summary.RelationshipEndpointsResolved = brokenRelationships.Length == 0;
        if (brokenRelationships.Length == 0)
            summary.PassedChecks++;
        else
        {
            foreach (SplitGmProjectRelationship relationship in brokenRelationships)
            {
                messages.Add(new SplitGmReconstructionMessage
                {
                    Severity = "Error",
                    Code = "SPLITGMPROJ_BROKEN_RELATIONSHIP",
                    Message = $"Relationship {relationship.Kind} references a stable ID that is not present in the resource table.",
                    ResourceStableId = relationship.SourceStableId
                });
            }
        }

        summary.WarningCount = messages.Count(message => message.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        summary.ErrorCount = messages.Count(message => message.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        return summary;
    }

    private static void WriteValidationText(
        string path,
        SplitGmValidationSummary summary,
        IEnumerable<SplitGmReconstructionMessage> messages)
    {
        StringBuilder text = new();
        text.AppendLine("SplitGM reconstructed project validation");
        text.AppendLine("========================================");
        text.AppendLine($"Checks performed: {summary.ChecksPerformed:N0}");
        text.AppendLine($"Checks passed: {summary.PassedChecks:N0}");
        text.AppendLine($"Warnings: {summary.WarningCount:N0}");
        text.AppendLine($"Errors: {summary.ErrorCount:N0}");
        text.AppendLine($"Project JSON parsed: {summary.ProjectJsonParsed}");
        text.AppendLine($"Intermediate .splitgmproj JSON parsed: {summary.IntermediateProjectJsonParsed}");
        text.AppendLine($"Intermediate format/version recognized: {summary.IntermediateFormatVersionRecognized}");
        text.AppendLine($"All .yyp resource paths exist: {summary.EveryListedResourceExists}");
        text.AppendLine($"All listed resource JSON parsed: {summary.EveryListedResourceJsonParsed} ({summary.ResourceJsonFilesChecked:N0} checked, {summary.ResourceJsonParseErrors:N0} errors)");
        text.AppendLine($"All .splitgmproj-recorded output files exist: {summary.EveryRecordedOutputFileExists} ({summary.RecordedOutputFilesChecked:N0} checked, {summary.MissingRecordedOutputFiles:N0} missing)");
        text.AppendLine($"Resource paths unique: {summary.ResourcePathsAreUnique}");
        text.AppendLine($"Resource names unique within type: {summary.ResourceNamesAreUniqueWithinType}");
        text.AppendLine($"Stable resource IDs unique: {summary.StableResourceIdsAreUnique}");
        text.AppendLine($"Relationship endpoints resolved: {summary.RelationshipEndpointsResolved}");
        text.AppendLine();
        foreach (SplitGmReconstructionMessage message in messages)
            text.AppendLine($"[{message.Severity}] {message.Code}: {message.Message}{(string.IsNullOrWhiteSpace(message.Path) ? string.Empty : " (" + message.Path + ")")}");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static void WriteReconstructionReadme(
        string outputDirectory,
        string projectName,
        ReconstructionTargetProfile profile,
        int represented,
        int fallbackOnly,
        int failures,
        IEnumerable<SplitGmReconstructionMessage> messages)
    {
        int warnings = messages.Count(message => message.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase));
        int errors = messages.Count(message => message.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase));
        StringBuilder readme = new();
        readme.AppendLine($"{SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}");
        readme.AppendLine("Reconstructed GameMaker .yyp project");
        readme.AppendLine(new string('=', 64));
        readme.AppendLine();
        readme.AppendLine($"Open this project: {projectName}.yyp");
        readme.AppendLine($"Target profile: {profile.Description}");
        readme.AppendLine($"Resources represented in .yyp: {represented:N0}");
        readme.AppendLine($"Resources preserved as fallback data: {fallbackOnly:N0}");
        readme.AppendLine($"Export failures: {failures:N0}");
        readme.AppendLine($"Warnings: {warnings:N0}");
        readme.AppendLine($"Errors: {errors:N0}");
        readme.AppendLine();
        readme.AppendLine("This project is reconstructed, not identical to the original source project.");
        readme.AppendLine("Open it as a repair workspace. Review the validation report and the");
        readme.AppendLine("__SplitGM_Unrepresented folder before attempting to compile.");
        readme.AppendLine();
        readme.AppendLine("Important files:");
        readme.AppendLine($"- {projectName}.splitgmproj: stable versioned intermediate project document");
        readme.AppendLine("- SplitGM-Reconstruction-Validation.txt/.json: validation and repair warnings");
        readme.AppendLine("- __SplitGM_Metadata: inspectable metadata and room previews");
        readme.AppendLine("- __SplitGM_Unrepresented: resource data that could not be represented safely in .yyp");
        File.WriteAllText(Path.Combine(outputDirectory, "README-SplitGM-Reconstructed-Project.txt"), readme.ToString(), new UTF8Encoding(false));
    }

    private static void PrepareReconstructionOutputDirectory(string outputDirectory, bool overwrite)
    {
        string? root = Path.GetPathRoot(outputDirectory);
        if (string.Equals(outputDirectory.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root cannot be used as the reconstructed project folder.");

        string marker = Path.Combine(outputDirectory, ".splitgm-reconstructed-project");
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            if (!overwrite)
                throw new IOException("The selected reconstructed project folder is not empty.");
            if (!File.Exists(marker) && !Directory.EnumerateFiles(outputDirectory, "*.splitgmproj", SearchOption.TopDirectoryOnly).Any())
                throw new IOException("For safety, SplitGM only replaces a non-empty folder previously created by the reconstructed-project exporter.");
            Directory.Delete(outputDirectory, recursive: true);
        }
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(marker, $"Created by {SplitGmProduct.Name} {SplitGmProduct.DisplayVersion}.\n", new UTF8Encoding(false));
    }

    private static ReconstructionTargetProfile SelectReconstructionProfile(string? version)
    {
        string source = version ?? string.Empty;
        if (source.StartsWith("2024", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("2025", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("2026", StringComparison.OrdinalIgnoreCase))
        {
            return new ReconstructionTargetProfile(
                "modern-2024.14",
                "GameMaker 2024.14 modern project schema",
                "2024.14.4.222",
                true);
        }
        if (source.StartsWith("2022", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("2023", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("2.", StringComparison.OrdinalIgnoreCase))
        {
            return new ReconstructionTargetProfile(
                "modern-compatibility-repair",
                "Modern GameMaker compatibility/repair schema",
                "2024.14.4.222",
                false);
        }
        return new ReconstructionTargetProfile(
            "legacy-import-repair",
            "Modern GameMaker legacy-import repair schema",
            "2024.14.4.222",
            false);
    }

    private static string? BuildProgressPreviewText(ResourcePreviewData preview)
    {
        string? text = !string.IsNullOrWhiteSpace(preview.Text) ? preview.Text : preview.Details;
        return string.IsNullOrWhiteSpace(text) ? null : TruncatePreviewText(text);
    }

    private static string TruncatePreviewText(string text, int maximumCharacters = 16000)
    {
        if (text.Length <= maximumCharacters)
            return text;
        return text[..maximumCharacters] + Environment.NewLine + Environment.NewLine +
               $"[Preview truncated by SplitGM after {maximumCharacters:N0} characters.]";
    }

    private static string SuggestedResourcePath(ResourceKind kind, string name) => kind switch
    {
        ResourceKind.Sprites => $"sprites/{name}/{name}.yy",
        ResourceKind.Sounds => $"sounds/{name}/{name}.yy",
        ResourceKind.Paths => $"paths/{name}/{name}.yy",
        ResourceKind.Objects => $"objects/{name}/{name}.yy",
        ResourceKind.Rooms => $"rooms/{name}/{name}.yy",
        ResourceKind.AudioGroups => $"<project>.yyp / AudioGroups/{name}",
        _ => $"__SplitGM_Unrepresented/{GetResourceCategoryDirectoryName(kind)}/{name}"
    };

    private static JsonObject ResourceId(string name, string path) => new()
    {
        ["name"] = name,
        ["path"] = path
    };

    private static JsonObject FolderId(string name) => ResourceId(name, $"folders/{name}.yy");

    private JsonNode? ResolveNamedResourceId(
        ResourceKind kind,
        string? sourceName,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return null;
        ResourceEntryInfo? entry = GetResourceEntries(kind).FirstOrDefault(item => item.Name.Equals(sourceName, StringComparison.Ordinal));
        if (entry is null)
            return null;
        string name = reconstructedNames[(kind, entry.Index)];
        string root = kind switch
        {
            ResourceKind.Sprites => "sprites",
            ResourceKind.Objects => "objects",
            _ => kind.ToString().ToLowerInvariant()
        };
        return ResourceId(name, $"{root}/{name}/{name}.yy");
    }

    private void AddNamedRelationship(
        ResourceKind targetKind,
        string? sourceTargetName,
        string relationshipKind,
        string sourceStableId,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> reconstructedNames,
        IReadOnlyDictionary<(ResourceKind Kind, int Index), string> stableIds,
        List<SplitGmProjectRelationship> relationships)
    {
        if (string.IsNullOrWhiteSpace(sourceTargetName))
            return;
        ResourceEntryInfo? target = GetResourceEntries(targetKind).FirstOrDefault(item => item.Name.Equals(sourceTargetName, StringComparison.Ordinal));
        if (target is null || !reconstructedNames.ContainsKey((targetKind, target.Index)))
            return;
        relationships.Add(new SplitGmProjectRelationship
        {
            Kind = relationshipKind,
            SourceStableId = sourceStableId,
            TargetStableId = stableIds[(targetKind, target.Index)],
            Details = sourceTargetName
        });
    }

    private static string BuildObjectEventFileStem(string eventType, uint subtype)
    {
        string normalized = eventType.Equals("Cleanup", StringComparison.OrdinalIgnoreCase) ? "CleanUp" : eventType;
        return OutputPathHelper.SafeFileName(normalized + "_" + subtype.ToString(CultureInfo.InvariantCulture));
    }

    private CodeEntryInfo FindCodeEntry(int codeIndex) =>
        CodeEntries.FirstOrDefault(entry => entry.Index == codeIndex)
        ?? new CodeEntryInfo(codeIndex, $"code_{codeIndex}", CodeCategory.Other, 0, 0, 0, 0, 0);

    private static string BuildFailedGml(CodeEntryInfo entry, CodeViewResult view)
    {
        StringBuilder output = new();
        output.AppendLine("/// @description SplitGM reconstructed-code failure marker");
        output.AppendLine($"// Source code entry: {entry.Name}");
        output.AppendLine("// The decompiler could not produce safe GML for this entry.");
        if (!string.IsNullOrWhiteSpace(view.DecompileError))
        {
            foreach (string line in view.DecompileError.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Take(20))
                output.Append("// ").AppendLine(line);
        }
        return output.ToString();
    }

    private static string StripCodePrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private static string AllocateIdentifier(string source, string fallbackPrefix, int index, HashSet<string> allocated)
    {
        string baseName = MakeGameMakerIdentifier(source, fallbackPrefix + "_" + index);
        string candidate = baseName;
        int suffix = 2;
        while (!allocated.Add(candidate))
            candidate = baseName + "_sgm" + suffix++;
        return candidate;
    }

    private static string MakeGameMakerIdentifier(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            value = fallback;
        StringBuilder result = new();
        foreach (char character in value)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        string identifier = result.ToString().Trim('_');
        if (identifier.Length == 0)
            identifier = fallback;
        if (char.IsDigit(identifier[0]))
            identifier = "_" + identifier;
        return identifier.Length > 120 ? identifier[..120] : identifier;
    }

    private static string StableId(string type, int index, string name) =>
        "sgm:" + type.ToLowerInvariant() + ":" + index.ToString(CultureInfo.InvariantCulture) + ":" + StableGuid(type, index, name);

    private static string StableGuid(string type, int index, string name)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"SplitGM|1.0|{type}|{index}|{name}"));
        return new Guid(hash).ToString();
    }

    private static JsonNode? CloneNode(JsonObject value) => value.DeepClone();

    private static void WriteJsonNode(string path, JsonNode node)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(ReconstructionJsonOptions), new UTF8Encoding(false));
    }

    private static void WriteReconstructionJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, ReconstructionJsonOptions), new UTF8Encoding(false));
    }

    private static void WriteMetadataFile(string outputDirectory, string relativePath, string content)
    {
        string path = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static SplitGmReconstructionMessage WarningMessage(string code, string message, string resourceId, string? path) => new()
    {
        Severity = "Warning",
        Code = code,
        Message = message,
        ResourceStableId = resourceId,
        Path = path
    };

    private static SplitGmProjectResource FailedResource(
        string stableId,
        string sourceType,
        string sourceName,
        int sourceIndex,
        string reconstructedName,
        string representation,
        string? path,
        Exception exception) => new()
    {
        StableId = stableId,
        SourceType = sourceType,
        SourceName = sourceName,
        SourceIndex = sourceIndex,
        ReconstructedName = reconstructedName,
        Representation = representation,
        YypResourcePath = path,
        RepresentedInYyp = false,
        ExportSucceeded = false,
        Warnings = [exception.Message]
    };

    private sealed record ReconstructionTargetProfile(
        string Id,
        string Description,
        string IdeVersion,
        bool SourceVersionDirectlyCompatible);

    private sealed record YypResourceReference(string Name, string Path, string Folder);
    private sealed record YypFolderDefinition(string Name, string Path);

    private sealed record ResourceExportOutcome(
        SplitGmProjectResource Resource,
        YypResourceReference? YypReference,
        List<SplitGmProjectRelationship> Relationships,
        List<SplitGmReconstructionMessage> Messages);

    private sealed class ResourceNameTupleComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public static ResourceNameTupleComparer Instance { get; } = new();
        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Folder, string Name) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
    private static int GetReconstructionParallelism(ResourceKind kind)
    {
        // Only resource writers with isolated output paths and read-only source access are
        // parallelized. Fallback exporters retain source order because they discover files
        // by comparing directory contents.
        return kind switch
        {
            ResourceKind.Sprites
                => Math.Clamp(Math.Max(1, Environment.ProcessorCount) / 2, 2, 8),
            ResourceKind.Sounds or ResourceKind.Paths or ResourceKind.Objects or ResourceKind.Rooms or ResourceKind.AudioGroups
                => Math.Clamp(Math.Max(1, Environment.ProcessorCount), 2, 12),
            _ => 1
        };
    }

    private static byte[]? TryLoadExportedPreview(string outputDirectory, IReadOnlyList<string> files)
    {
        foreach (string relative in files)
        {
            if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            string fullPath = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                FileInfo info = new(fullPath);
                if (info.Exists && info.Length is > 0 and <= 8L * 1024 * 1024)
                    return File.ReadAllBytes(fullPath);
            }
            catch (IOException)
            {
                // A progress preview is optional; the exported resource itself is authoritative.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private sealed record ReconstructionWorkResult(
        ResourceEntryInfo Entry,
        string ReconstructedName,
        string StableId,
        string StatusPath,
        ResourceExportOutcome? Outcome,
        Exception? Exception,
        string Status,
        byte[]? PreviewPng);

}

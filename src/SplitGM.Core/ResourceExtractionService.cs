using System.Diagnostics;
using System.Text;

namespace SplitGM.Core;

internal static class ResourceExtractionService
{
    private static readonly ResourceKind[] ExportKinds =
    [
        ResourceKind.Objects,
        ResourceKind.Rooms,
        ResourceKind.Sprites,
        ResourceKind.Sounds,
        ResourceKind.AudioGroups,
        ResourceKind.EmbeddedAudio,
        ResourceKind.Fonts,
        ResourceKind.Shaders,
        ResourceKind.Backgrounds,
        ResourceKind.Paths,
        ResourceKind.Timelines,
        ResourceKind.Extensions,
        ResourceKind.Sequences,
        ResourceKind.AnimationCurves,
        ResourceKind.ParticleSystems,
        ResourceKind.ParticleSystemEmitters,
        ResourceKind.TextureGroups,
        ResourceKind.TexturePageItems,
        ResourceKind.TexturePages,
        ResourceKind.EmbeddedImages,
        ResourceKind.FilterEffects
    ];

    public static ResourceExtractionResult ExportAll(
        GameProjectSession session,
        string outputDirectory,
        IProgress<DecompileProgress>? progress,
        IProgress<LogMessage>? log,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(outputDirectory);

        int totalResources = ExportKinds.Sum(kind => session.GetResourceEntries(kind).Count);
        int processed = 0;
        int files = 0;
        int failures = 0;
        long bytes = 0;
        List<string> warnings = [];

        foreach (ResourceKind kind in ExportKinds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ResourceEntryInfo> entries = session.GetResourceEntries(kind);
            if (entries.Count == 0)
                continue;

            string kindDirectory = Path.Combine(outputDirectory, GetFolderName(kind));
            Directory.CreateDirectory(kindDirectory);

            foreach (ResourceEntryInfo entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DecompileProgress(
                    DecompileStage.ExportingResources,
                    processed,
                    totalResources,
                    $"Resources [{processed + 1:N0}/{totalResources:N0}] {kind}: {entry.Name}"));

                try
                {
                    if (kind == ResourceKind.AudioGroups)
                    {
                        // Sounds are already exported once in Resources\Sounds. For a full-project
                        // export, preserve each audio-group resource as metadata without duplicating
                        // every audio byte a second time. The GUI's explicit "Export audio group"
                        // command still exports the complete selected group on demand.
                        string metadataPath = OutputPathHelper.EnsureUniquePath(Path.Combine(
                            kindDirectory,
                            $"{entry.Index:D4}_{OutputPathHelper.SafeFileName(entry.Name)}.details.txt"));
                        File.WriteAllText(
                            metadataPath,
                            session.GetResourceDetails(kind, entry.Index),
                            new UTF8Encoding(false));
                        files++;
                        bytes += new FileInfo(metadataPath).Length;
                    }
                    else
                    {
                        ResourceExportResult result = session.ExportSelectedResource(
                            kind,
                            entry.Index,
                            kindDirectory,
                            null,
                            cancellationToken);
                        files += result.FilesWritten;
                        bytes += result.BytesWritten;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures++;
                    string message = $"{kind} #{entry.Index} ({entry.Name}): {exception.Message}";
                    warnings.Add(message);
                    log?.Report(LogMessage.Warning($"Resource export failed: {message}"));

                    string errorDirectory = Path.Combine(outputDirectory, "_ResourceErrors", kind.ToString());
                    Directory.CreateDirectory(errorDirectory);
                    string errorPath = Path.Combine(
                        errorDirectory,
                        $"{entry.Index:D6}_{OutputPathHelper.SafeFileName(entry.Name)}.error.txt");
                    File.WriteAllText(errorPath, exception.ToString(), new UTF8Encoding(false));
                    files++;
                    bytes += new FileInfo(errorPath).Length;
                }

                processed++;
                // TextureWorker deliberately caches decoded pages. Trim that cache during a
                // full-game export so a game with thousands of texture pages cannot grow without bound.
                if (processed % 128 == 0)
                    session.TrimResourceImageCache();
            }

            session.TrimResourceImageCache();
        }

        stopwatch.Stop();
        progress?.Report(new DecompileProgress(
            DecompileStage.ExportingResources,
            totalResources,
            totalResources,
            $"Resource extraction complete: {processed - failures:N0} succeeded, {failures:N0} failed."));

        return new ResourceExtractionResult(
            processed,
            files,
            failures,
            bytes,
            stopwatch.Elapsed,
            warnings);
    }

    private static string GetFolderName(ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.Backgrounds => "Backgrounds-And-Tilesets",
            ResourceKind.AnimationCurves => "Animation-Curves",
            ResourceKind.ParticleSystems => "Particle-Systems",
            ResourceKind.ParticleSystemEmitters => "Particle-Emitters",
            ResourceKind.TextureGroups => "Texture-Groups",
            ResourceKind.TexturePageItems => "Texture-Page-Items",
            ResourceKind.TexturePages => "Texture-Pages",
            ResourceKind.EmbeddedImages => "Embedded-Images",
            ResourceKind.FilterEffects => "Filter-Effects",
            ResourceKind.AudioGroups => "Audio-Groups",
            ResourceKind.EmbeddedAudio => "Embedded-Audio",
            _ => kind.ToString()
        };
    }
}

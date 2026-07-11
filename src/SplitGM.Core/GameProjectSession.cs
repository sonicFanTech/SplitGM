using System.Collections.Concurrent;
using System.Text;
using Underanalyzer.Decompiler;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace SplitGM.Core;

public sealed partial class GameProjectSession : IDisposable
{
    private readonly ResolvedGameInput _resolvedInput;
    private readonly UndertaleData _data;
    private readonly GlobalDecompileContext? _globalContext;
    private readonly IDecompileSettings? _decompileSettings;
    private readonly List<UndertaleCode> _rootCode;
    private readonly List<CodeEntryInfo> _codeEntries;
    private const int MaximumCachedCodeEntries = 64;
    private const int MaximumCachedCodeCharactersPerEntry = 4_000_000;
    private readonly ConcurrentDictionary<int, CodeViewResult> _codeCache = new();
    private readonly ConcurrentQueue<int> _codeCacheOrder = new();
    private readonly ConcurrentDictionary<ResourceKind, IReadOnlyList<ResourceEntryInfo>> _resourceCache = new();
    private readonly SemaphoreSlim _codeWorkGate = new(1, 1);
    private bool _disposed;

    internal GameProjectSession(
        ResolvedGameInput resolvedInput,
        UndertaleData data,
        GameProjectInfo info,
        ResourceCounts resourceCounts,
        IReadOnlyList<string> warnings)
    {
        _resolvedInput = resolvedInput;
        _data = data;
        Info = info;
        ResourceCounts = resourceCounts;
        Warnings = warnings.ToArray();

        _rootCode = data.Code?
            .Where(code => code is not null && code.ParentEntry is null)
            .ToList() ?? [];

        _codeEntries = _rootCode
            .Select((code, index) => CreateCodeEntryInfo(code, index))
            .ToList();

        if (!info.IsYyc && _rootCode.Count > 0)
        {
            _globalContext = new GlobalDecompileContext(data);
            _decompileSettings = data.ToolInfo.DecompilerSettings;
        }
    }

    public GameProjectInfo Info { get; }
    public ResourceCounts ResourceCounts { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<CodeEntryInfo> CodeEntries => _codeEntries;

    public IReadOnlyList<CodeEntryInfo> GetCodeEntries(CodeCategory category)
    {
        ThrowIfDisposed();
        return _codeEntries.Where(entry => entry.Category == category).ToArray();
    }

    public IReadOnlyList<ResourceEntryInfo> GetResourceEntries(ResourceKind kind)
    {
        ThrowIfDisposed();
        return _resourceCache.GetOrAdd(kind, BuildResourceEntries);
    }

    public IReadOnlyList<string> GetResourceNames(ResourceKind kind)
    {
        return GetResourceEntries(kind).Select(entry => entry.Name).ToArray();
    }

    public string GetResourceDetails(ResourceKind kind, int index)
    {
        ThrowIfDisposed();
        object? resource = GetRawResource(kind, index);
        string name = GetResourceEntries(kind).FirstOrDefault(entry => entry.Index == index)?.Name
                      ?? $"{kind} #{index}";
        return ObjectInspector.Format(resource, name);
    }

    public string GetGeneralInformationText()
    {
        ThrowIfDisposed();
        StringBuilder output = new();
        output.AppendLine(Info.DisplayName);
        output.AppendLine(new string('=', Math.Min(Info.DisplayName.Length, 72)));
        output.AppendLine();
        output.AppendLine($"Internal name: {Info.GameName}");
        output.AppendLine($"GameMaker version: {Info.GameMakerVersion}");
        output.AppendLine($"Bytecode version: {Info.BytecodeVersion}");
        output.AppendLine($"Runtime: {Info.RuntimeType}");
        output.AppendLine($"Compatibility: {Info.Compatibility}");
        output.AppendLine($"Input size: {FormatBytes(Info.InputFileSize)}");
        output.AppendLine($"Resolution method: {Info.ResolutionMethod}");
        output.AppendLine($"Original input: {Info.OriginalInput}");
        output.AppendLine($"Resolved data source: {Info.ResolvedDataSource}");
        output.AppendLine();
        output.AppendLine(Info.CompatibilityMessage);
        output.AppendLine();
        output.AppendLine("Resource counts");
        output.AppendLine("---------------");
        foreach ((string label, int count) in EnumerateCounts())
            output.AppendLine($"{label,-22} {count,10:N0}");

        if (Warnings.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"Load warnings ({Warnings.Count:N0})");
            output.AppendLine("----------------");
            foreach (string warning in Warnings.Take(100))
                output.AppendLine($"- {warning}");
            if (Warnings.Count > 100)
                output.AppendLine($"... {Warnings.Count - 100:N0} additional warnings omitted ...");
        }

        return output.ToString();
    }

    public string GetCompatibilityReportText()
    {
        ThrowIfDisposed();
        StringBuilder output = new();
        output.AppendLine($"{SplitGmProduct.Name} {SplitGmProduct.DisplayVersion} Compatibility Report");
        output.AppendLine(new string('=', 68));
        output.AppendLine($"Game: {Info.DisplayName}");
        output.AppendLine($"GameMaker version: {Info.GameMakerVersion}");
        output.AppendLine($"Bytecode version: {Info.BytecodeVersion}");
        output.AppendLine($"Runtime type: {Info.RuntimeType}");
        output.AppendLine($"YYC: {Info.IsYyc}");
        output.AppendLine($"Unsupported bytecode flag: {Info.UnsupportedBytecodeVersion}");
        output.AppendLine($"Result: {Info.Compatibility}");
        output.AppendLine();
        output.AppendLine(Info.CompatibilityMessage);
        output.AppendLine();
        output.AppendLine("SplitGM capabilities for this file:");
        output.AppendLine($"- Read-only resource browser: Yes");
        output.AppendLine($"- Sprite/room/texture/audio preview: Yes, where resource data is recoverable");
        output.AppendLine($"- Full resource extraction: Yes, with per-resource error continuation");
        output.AppendLine($"- GML decompilation: {(!Info.IsYyc && !Info.UnsupportedBytecodeVersion ? "Yes" : "Limited/No")}");
        output.AppendLine($"- VM assembly view: {(!Info.IsYyc ? "Yes" : "No")}");
        output.AppendLine($"- Global code search: {(!Info.IsYyc ? "Yes" : "No")}");
        output.AppendLine($"- Relationship navigation: {(!Info.IsYyc ? "Direct model links and GML heuristics" : "Direct resource links only")}");
        output.AppendLine($"- Connected object/room code navigation: {(!Info.IsYyc ? "Yes" : "No VM code")}");
        output.AppendLine($"- Organized project export: {(!Info.IsYyc ? "Yes" : "Resource metadata only")}");
        return output.ToString();
    }

    public async Task<CodeViewResult> GetCodeViewAsync(
        int codeIndex,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if ((uint)codeIndex >= (uint)_rootCode.Count)
            throw new ArgumentOutOfRangeException(nameof(codeIndex));

        if (_codeCache.TryGetValue(codeIndex, out CodeViewResult? cached))
            return cached;

        await _codeWorkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_codeCache.TryGetValue(codeIndex, out cached))
                return cached;

            CodeViewResult created = await Task.Run(
                () => BuildCodeView(codeIndex),
                cancellationToken).ConfigureAwait(false);
            // Keep the viewer responsive on very large games: UMT decompiles on demand rather
            // than retaining every source document. SplitGM follows that behavior and avoids
            // caching multi-megabyte entries, while retaining a modest LRU-style set of normal ones.
            long characterCount = (long)created.Gml.Length + created.Assembly.Length;
            if (characterCount <= MaximumCachedCodeCharactersPerEntry)
            {
                _codeCache[codeIndex] = created;
                _codeCacheOrder.Enqueue(codeIndex);
                while (_codeCache.Count > MaximumCachedCodeEntries &&
                       _codeCacheOrder.TryDequeue(out int oldestIndex))
                {
                    if (oldestIndex != codeIndex)
                        _codeCache.TryRemove(oldestIndex, out _);
                }
            }
            return created;
        }
        finally
        {
            _codeWorkGate.Release();
        }
    }

    public async Task<IReadOnlyList<CodeSearchResult>> SearchCodeAsync(
        string query,
        int maximumResults = 500,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Info.IsYyc)
            throw new YycGameException("This game uses YYC, so there is no GameMaker VM code to search.");
        if (string.IsNullOrWhiteSpace(query))
            return [];

        string needle = query.Trim();
        List<CodeSearchResult> results = [];

        for (int index = 0; index < _codeEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodeEntryInfo entry = _codeEntries[index];
            progress?.Report(new DecompileProgress(
                DecompileStage.SearchingCode,
                index,
                _codeEntries.Count,
                $"Searching [{index + 1:N0}/{_codeEntries.Count:N0}] {entry.Name}"));

            if (entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new CodeSearchResult(
                    entry.Index,
                    entry.Name,
                    entry.Category,
                    0,
                    "Name",
                    entry.Name));
            }

            if (results.Count >= maximumResults)
                break;

            CodeViewResult view = await GetCodeViewAsync(entry.Index, cancellationToken).ConfigureAwait(false);
            AddLineMatches(results, view.Gml, needle, entry, "GML", maximumResults);
            if (results.Count < maximumResults)
                AddLineMatches(results, view.Assembly, needle, entry, "Assembly", maximumResults);

            if (results.Count >= maximumResults)
                break;
        }

        progress?.Report(new DecompileProgress(
            DecompileStage.SearchingCode,
            _codeEntries.Count,
            _codeEntries.Count,
            $"Search complete: {results.Count:N0} results."));

        return results;
    }

    internal UndertaleData Data
    {
        get
        {
            ThrowIfDisposed();
            return _data;
        }
    }

    private CodeViewResult BuildCodeView(int codeIndex)
    {
        UndertaleCode code = _rootCode[codeIndex];
        CodeEntryInfo entry = _codeEntries[codeIndex];
        string gml = string.Empty;
        string assembly = string.Empty;
        string? decompileError = null;
        string? assemblyError = null;

        if (Info.IsYyc || _globalContext is null || _decompileSettings is null)
        {
            decompileError = "YYC game: no VM bytecode is available.";
            assemblyError = decompileError;
        }
        else
        {
            try
            {
                DecompileContext context = new(_globalContext, code, _decompileSettings);
                gml = context.DecompileToString();
            }
            catch (Exception exception)
            {
                decompileError = exception.ToString();
                gml = $"/*\nSPLITGM DECOMPILER ERROR\n\n{exception}\n*/";
            }

            try
            {
                IList<UndertaleVariable> variables = _data.Variables ?? new List<UndertaleVariable>();
                assembly = code.Disassemble(
                    variables,
                    _data.CodeLocals?.For(code),
                    ignoreMissingCodeLocals: true);
            }
            catch (Exception exception)
            {
                assemblyError = exception.ToString();
                assembly = $"; SPLITGM DISASSEMBLY ERROR\n; {exception}";
            }
        }

        StringBuilder details = new();
        details.AppendLine(entry.Name);
        details.AppendLine(new string('=', Math.Min(entry.Name.Length, 72)));
        details.AppendLine($"Category: {entry.Category}");
        details.AppendLine($"Root code index: {entry.Index}");
        details.AppendLine($"Instructions: {entry.InstructionCount:N0}");
        details.AppendLine($"Estimated bytecode size: {FormatBytes(entry.EstimatedByteLength)}");
        details.AppendLine($"Child code entries: {entry.ChildEntryCount:N0}");
        details.AppendLine($"Arguments: {entry.ArgumentsCount:N0}");
        details.AppendLine($"Locals: {entry.LocalsCount:N0}");
        details.AppendLine($"GML status: {(decompileError is null ? "Success" : "Failed; assembly fallback retained")}");
        details.AppendLine($"Assembly status: {(assemblyError is null ? "Success" : "Failed")}");
        if (decompileError is not null)
        {
            details.AppendLine();
            details.AppendLine("Decompiler error");
            details.AppendLine("----------------");
            details.AppendLine(decompileError);
        }
        if (assemblyError is not null)
        {
            details.AppendLine();
            details.AppendLine("Disassembler error");
            details.AppendLine("------------------");
            details.AppendLine(assemblyError);
        }

        return new CodeViewResult(
            entry,
            gml,
            assembly,
            details.ToString(),
            decompileError,
            assemblyError);
    }

    private IReadOnlyList<ResourceEntryInfo> BuildResourceEntries(ResourceKind kind)
    {
        return kind switch
        {
            ResourceKind.Objects => BuildList(_data.GameObjects, kind, item => item?.Name?.Content),
            ResourceKind.Rooms => BuildList(_data.Rooms, kind, item => item?.Name?.Content),
            ResourceKind.Sprites => BuildList(_data.Sprites, kind, item => item?.Name?.Content),
            ResourceKind.Sounds => BuildList(_data.Sounds, kind, item => item?.Name?.Content),
            ResourceKind.Fonts => BuildList(_data.Fonts, kind, item => item?.Name?.Content),
            ResourceKind.Shaders => BuildList(_data.Shaders, kind, item => item?.Name?.Content),
            ResourceKind.Backgrounds => BuildList(_data.Backgrounds, kind, item => item?.Name?.Content),
            ResourceKind.Paths => BuildList(_data.Paths, kind, item => item?.Name?.Content),
            ResourceKind.Timelines => BuildList(_data.Timelines, kind, item => item?.Name?.Content),
            ResourceKind.Extensions => BuildList(_data.Extensions, kind, item => item?.Name?.Content),
            ResourceKind.AudioGroups => BuildList(_data.AudioGroups, kind, item => item?.Name?.Content),
            ResourceKind.Sequences => BuildList(_data.Sequences, kind, item => item?.Name?.Content),
            ResourceKind.AnimationCurves => BuildList(_data.AnimationCurves, kind, item => item?.Name?.Content),
            ResourceKind.ParticleSystems => BuildList(_data.ParticleSystems, kind, item => item?.Name?.Content),
            ResourceKind.ParticleSystemEmitters => BuildList(_data.ParticleSystemEmitters, kind, item => item?.Name?.Content),
            ResourceKind.TextureGroups => BuildList(_data.TextureGroupInfo, kind, item => item?.Name?.Content),
            ResourceKind.TexturePageItems => BuildIndexedList(_data.TexturePageItems, kind, "Texture Page Item"),
            ResourceKind.TexturePages => BuildIndexedList(_data.EmbeddedTextures, kind, "Texture Page"),
            ResourceKind.EmbeddedImages => BuildList(_data.EmbeddedImages, kind, item => item?.Name?.Content),
            ResourceKind.EmbeddedAudio => BuildIndexedList(_data.EmbeddedAudio, kind, "Embedded Audio"),
            ResourceKind.FilterEffects => BuildList(_data.FilterEffects, kind, item => item?.Name?.Content),
            ResourceKind.Functions => BuildList(_data.Functions, kind, item => item?.Name?.Content),
            ResourceKind.Variables => BuildList(_data.Variables, kind, item => item?.Name?.Content),
            ResourceKind.Strings => BuildStrings(),
            _ => []
        };
    }

    private object? GetRawResource(ResourceKind kind, int index)
    {
        return kind switch
        {
            ResourceKind.Objects => GetAt(_data.GameObjects, index),
            ResourceKind.Rooms => GetAt(_data.Rooms, index),
            ResourceKind.Sprites => GetAt(_data.Sprites, index),
            ResourceKind.Sounds => GetAt(_data.Sounds, index),
            ResourceKind.Fonts => GetAt(_data.Fonts, index),
            ResourceKind.Shaders => GetAt(_data.Shaders, index),
            ResourceKind.Backgrounds => GetAt(_data.Backgrounds, index),
            ResourceKind.Paths => GetAt(_data.Paths, index),
            ResourceKind.Timelines => GetAt(_data.Timelines, index),
            ResourceKind.Extensions => GetAt(_data.Extensions, index),
            ResourceKind.AudioGroups => GetAt(_data.AudioGroups, index),
            ResourceKind.Sequences => GetAt(_data.Sequences, index),
            ResourceKind.AnimationCurves => GetAt(_data.AnimationCurves, index),
            ResourceKind.ParticleSystems => GetAt(_data.ParticleSystems, index),
            ResourceKind.ParticleSystemEmitters => GetAt(_data.ParticleSystemEmitters, index),
            ResourceKind.TextureGroups => GetAt(_data.TextureGroupInfo, index),
            ResourceKind.TexturePageItems => GetAt(_data.TexturePageItems, index),
            ResourceKind.TexturePages => GetAt(_data.EmbeddedTextures, index),
            ResourceKind.EmbeddedImages => GetAt(_data.EmbeddedImages, index),
            ResourceKind.EmbeddedAudio => GetAt(_data.EmbeddedAudio, index),
            ResourceKind.FilterEffects => GetAt(_data.FilterEffects, index),
            ResourceKind.Strings => GetAt(_data.Strings, index),
            ResourceKind.Functions => GetAt(_data.Functions, index),
            ResourceKind.Variables => GetAt(_data.Variables, index),
            _ => null
        };
    }

    private IReadOnlyList<ResourceEntryInfo> BuildStrings()
    {
        if (_data.Strings is null)
            return [];

        List<ResourceEntryInfo> entries = new(_data.Strings.Count);
        for (int index = 0; index < _data.Strings.Count; index++)
        {
            string value = _data.Strings[index]?.Content ?? string.Empty;
            value = value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
            if (value.Length > 120)
                value = value[..120] + "...";
            entries.Add(new ResourceEntryInfo(ResourceKind.Strings, index, value));
        }
        return entries;
    }

    private static IReadOnlyList<ResourceEntryInfo> BuildList<T>(
        IList<T>? list,
        ResourceKind kind,
        Func<T, string?> getName)
    {
        if (list is null)
            return [];

        List<ResourceEntryInfo> entries = new(list.Count);
        for (int index = 0; index < list.Count; index++)
        {
            T value = list[index];
            string? name = value is null ? null : getName(value);
            entries.Add(new ResourceEntryInfo(
                kind,
                index,
                string.IsNullOrWhiteSpace(name) ? $"{kind} #{index}" : name));
        }
        return entries;
    }

    private static IReadOnlyList<ResourceEntryInfo> BuildIndexedList<T>(
        IList<T>? list,
        ResourceKind kind,
        string prefix)
    {
        if (list is null)
            return [];

        List<ResourceEntryInfo> entries = new(list.Count);
        for (int index = 0; index < list.Count; index++)
            entries.Add(new ResourceEntryInfo(kind, index, $"{prefix} {index}"));
        return entries;
    }

    private static object? GetAt<T>(IList<T>? list, int index)
    {
        if (list is null || (uint)index >= (uint)list.Count)
            return null;
        return list[index];
    }

    private static CodeEntryInfo CreateCodeEntryInfo(UndertaleCode code, int index)
    {
        string name = code.Name?.Content ?? $"unnamed_code_{index}";
        long estimatedBytes = code.Instructions?
            .Sum(instruction => (long)instruction.CalculateInstructionSize() * 4L) ?? 0;

        return new CodeEntryInfo(
            Index: index,
            Name: name,
            Category: OutputPathHelper.CategoryForCodeName(name),
            InstructionCount: code.Instructions?.Count ?? 0,
            EstimatedByteLength: estimatedBytes,
            ChildEntryCount: code.ChildEntries?.Count ?? 0,
            ArgumentsCount: Convert.ToInt32(code.ArgumentsCount),
            LocalsCount: Convert.ToInt32(code.LocalsCount));
    }

    private static void AddLineMatches(
        List<CodeSearchResult> results,
        string text,
        string needle,
        CodeEntryInfo entry,
        string source,
        int maximumResults)
    {
        if (string.IsNullOrEmpty(text))
            return;

        using StringReader reader = new(text);
        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (!line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            string snippet = line.Trim();
            if (snippet.Length > 220)
                snippet = snippet[..220] + "...";

            results.Add(new CodeSearchResult(
                entry.Index,
                entry.Name,
                entry.Category,
                lineNumber,
                source,
                snippet));

            if (results.Count >= maximumResults)
                return;
        }
    }

    private IEnumerable<(string Label, int Count)> EnumerateCounts()
    {
        yield return ("Root code entries", ResourceCounts.RootCodeEntries);
        yield return ("Scripts", ResourceCounts.Scripts);
        yield return ("Objects", ResourceCounts.Objects);
        yield return ("Rooms", ResourceCounts.Rooms);
        yield return ("Sprites", ResourceCounts.Sprites);
        yield return ("Sounds", ResourceCounts.Sounds);
        yield return ("Fonts", ResourceCounts.Fonts);
        yield return ("Shaders", ResourceCounts.Shaders);
        yield return ("Backgrounds/Tilesets", ResourceCounts.Backgrounds);
        yield return ("Paths", ResourceCounts.Paths);
        yield return ("Timelines", ResourceCounts.Timelines);
        yield return ("Extensions", ResourceCounts.Extensions);
        yield return ("Audio groups", ResourceCounts.AudioGroups);
        yield return ("Sequences", ResourceCounts.Sequences);
        yield return ("Animation curves", ResourceCounts.AnimationCurves);
        yield return ("Particle systems", ResourceCounts.ParticleSystems);
        yield return ("Particle emitters", ResourceCounts.ParticleSystemEmitters);
        yield return ("Texture groups", ResourceCounts.TextureGroups);
        yield return ("Texture page items", ResourceCounts.TexturePageItems);
        yield return ("Texture pages", ResourceCounts.TexturePages);
        yield return ("Embedded images", ResourceCounts.EmbeddedImages);
        yield return ("Embedded audio", ResourceCounts.EmbeddedAudio);
        yield return ("Filter effects", ResourceCounts.FilterEffects);
        yield return ("Strings", ResourceCounts.Strings);
        yield return ("Functions", ResourceCounts.Functions);
        yield return ("Variables", ResourceCounts.Variables);
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.##} {suffixes[suffix]}";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // A preview decompile can still be running when the user closes a game.
        // Wait for that one protected operation before disposing UMT's object graph.
        _codeWorkGate.Wait();
        try
        {
            _codeCache.Clear();
            while (_codeCacheOrder.TryDequeue(out _)) { }
            _resourceCache.Clear();
            DisposeResourcePreviewState();
            _data.Dispose();
            _resolvedInput.Dispose();
        }
        finally
        {
            _codeWorkGate.Release();
            // Deliberately do not dispose the semaphore: a caller that passed the
            // first disposed check may still be waiting and will re-check safely.
        }
    }
}

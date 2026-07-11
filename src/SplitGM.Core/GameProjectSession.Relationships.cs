using System.Text;
using System.Text.RegularExpressions;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace SplitGM.Core;

public sealed partial class GameProjectSession
{
    private static readonly Regex FunctionCallRegex = new(
        @"(?<![A-Za-z0-9_\.])(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GlobalVariableRegex = new(
        @"\bglobal\.(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierRegex = new(
        @"\b[A-Za-z_][A-Za-z0-9_]*\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RoomGotoRegex = new(
        @"\b(?<fn>room_goto|room_goto_next|room_goto_previous|room_restart)\s*\(\s*(?<target>[A-Za-z_][A-Za-z0-9_]*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NonCallKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "with", "repeat", "return", "exit",
        "function", "constructor", "new", "delete", "typeof", "instanceof"
    };

    public int FindCodeIndexByName(string? codeName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(codeName))
            return -1;

        for (int i = 0; i < _codeEntries.Count; i++)
        {
            if (string.Equals(_codeEntries[i].Name, codeName, StringComparison.Ordinal))
                return i;
        }

        string normalized = NormalizeCallableName(codeName);
        for (int i = 0; i < _codeEntries.Count; i++)
        {
            if (string.Equals(NormalizeCallableName(_codeEntries[i].Name), normalized, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public IReadOnlyList<ConnectedCodeInfo> GetConnectedCodeForObject(int objectIndex)
    {
        ThrowIfDisposed();
        UndertaleGameObject? gameObject = GetAt(_data.GameObjects, objectIndex) as UndertaleGameObject;
        if (gameObject is null)
            return [];

        List<ConnectedCodeInfo> output = [];
        if (gameObject.Events is not null)
        {
            for (int eventTypeIndex = 0; eventTypeIndex < gameObject.Events.Count; eventTypeIndex++)
            {
                UndertalePointerList<UndertaleGameObject.Event>? subEvents = gameObject.Events[eventTypeIndex];
                if (subEvents is null)
                    continue;

                foreach (UndertaleGameObject.Event eventEntry in subEvents)
                {
                    if (eventEntry?.Actions is null)
                        continue;
                    foreach (UndertaleGameObject.EventAction action in eventEntry.Actions)
                    {
                        AddConnectedCode(
                            output,
                            action?.CodeId,
                            $"{(EventType)eventTypeIndex} event",
                            $"Subtype {eventEntry.EventSubtype}");
                    }
                }
            }
        }
        return output
            .GroupBy(item => (item.CodeIndex, item.Relationship, item.Details))
            .Select(group => group.First())
            .OrderBy(item => item.Relationship, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CodeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ConnectedCodeInfo> GetConnectedCodeForRoomInstance(int roomIndex, uint instanceId)
    {
        ThrowIfDisposed();
        UndertaleRoom? room = GetAt(_data.Rooms, roomIndex) as UndertaleRoom;
        UndertaleRoom.GameObject? instance = room?.GameObjects?.FirstOrDefault(item => item?.InstanceID == instanceId);
        if (instance is null)
            return [];

        List<ConnectedCodeInfo> output = [];
        AddConnectedCode(output, instance.CreationCode, "Instance creation code", $"Instance {instanceId}");
        AddConnectedCode(output, instance.PreCreateCode, "Instance pre-create code", $"Instance {instanceId}");

        if (instance.ObjectDefinition is UndertaleGameObject gameObject)
        {
            int objectIndex = IndexOfReference(_data.GameObjects, gameObject);
            if (objectIndex >= 0)
                output.AddRange(GetConnectedCodeForObject(objectIndex));
        }

        return output
            .GroupBy(item => (item.CodeIndex, item.Relationship, item.Details))
            .Select(group => group.First())
            .OrderBy(item => item.Relationship, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.CodeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RelationshipAnalysisResult> AnalyzeCodeRelationshipsAsync(
        int codeIndex,
        int maximumResults = 1000,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if ((uint)codeIndex >= (uint)_codeEntries.Count)
            throw new ArgumentOutOfRangeException(nameof(codeIndex));

        CodeEntryInfo sourceEntry = _codeEntries[codeIndex];
        CodeViewResult sourceView = await GetCodeViewAsync(codeIndex, cancellationToken).ConfigureAwait(false);
        string source = sourceView.Gml;
        List<RelationshipEntry> relationships = [];
        HashSet<string> dedupe = new(StringComparer.Ordinal);

        Dictionary<string, int> callableLookup = BuildCallableLookup();
        foreach (Match match in FunctionCallRegex.Matches(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string callName = match.Groups["name"].Value;
            if (NonCallKeywords.Contains(callName))
                continue;

            if (callableLookup.TryGetValue(callName, out int targetCodeIndex) && targetCodeIndex != codeIndex)
            {
                AddRelationship(relationships, dedupe, new RelationshipEntry(
                    RelationshipKind.Calls,
                    "Calls",
                    "Code",
                    _codeEntries[targetCodeIndex].Name,
                    $"Detected call expression: {callName}(...) ",
                    CodeIndex: targetCodeIndex));
            }
        }

        foreach (Match match in GlobalVariableRegex.Matches(source))
        {
            AddRelationship(relationships, dedupe, new RelationshipEntry(
                RelationshipKind.UsesGlobal,
                "Uses global variable",
                "Global variable",
                "global." + match.Groups["name"].Value,
                "Referenced by the selected code entry."));
        }

        Dictionary<string, (ResourceKind Kind, int Index, string Name)> resourceLookup = BuildNamedResourceLookup();
        HashSet<string> identifiers = IdentifierRegex.Matches(source)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string identifier in identifiers)
        {
            if (!resourceLookup.TryGetValue(identifier, out var target))
                continue;
            AddRelationship(relationships, dedupe, new RelationshipEntry(
                RelationshipKind.UsesAsset,
                "References asset",
                target.Kind.ToString(),
                target.Name,
                "The asset name appears as an identifier in the decompiled GML.",
                ResourceKind: target.Kind,
                ResourceIndex: target.Index));
        }

        Dictionary<string, int> roomLookup = GetResourceEntries(ResourceKind.Rooms)
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.Ordinal);
        foreach (Match match in RoomGotoRegex.Matches(source))
        {
            string functionName = match.Groups["fn"].Value;
            string targetName = match.Groups["target"].Value;
            if (targetName.Length > 0 && roomLookup.TryGetValue(targetName, out int roomIndex))
            {
                AddRelationship(relationships, dedupe, new RelationshipEntry(
                    RelationshipKind.RoomTransition,
                    "Room transition",
                    "Room",
                    targetName,
                    $"Detected {functionName}({targetName}).",
                    ResourceKind: ResourceKind.Rooms,
                    ResourceIndex: roomIndex));
            }
            else
            {
                AddRelationship(relationships, dedupe, new RelationshipEntry(
                    RelationshipKind.RoomTransition,
                    "Room transition",
                    "Dynamic room target",
                    targetName.Length == 0 ? functionName : targetName,
                    "The destination could not be resolved to a static room resource."));
            }
        }

        string[] aliases = GetCallableAliases(sourceEntry.Name);
        for (int index = 0; index < _codeEntries.Count && relationships.Count < maximumResults; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == codeIndex)
                continue;

            progress?.Report(new DecompileProgress(
                DecompileStage.SearchingCode,
                index,
                _codeEntries.Count,
                $"Finding callers [{index + 1:N0}/{_codeEntries.Count:N0}] {_codeEntries[index].Name}"));

            CodeViewResult candidate = await GetCodeViewAsync(index, cancellationToken).ConfigureAwait(false);
            if (!aliases.Any(alias => ContainsCall(candidate.Gml, alias)))
                continue;

            AddRelationship(relationships, dedupe, new RelationshipEntry(
                RelationshipKind.CalledBy,
                "Called by",
                "Code",
                candidate.Entry.Name,
                $"A call to {aliases.First(alias => ContainsCall(candidate.Gml, alias))}(...) was detected.",
                CodeIndex: index));
        }

        string summary = BuildRelationshipSummary(sourceEntry.Name, relationships);
        return new RelationshipAnalysisResult
        {
            Title = $"Relationships for {sourceEntry.Name}",
            Summary = summary,
            Entries = relationships.Take(maximumResults).ToArray(),
            Warnings = relationships.Count >= maximumResults
                ? [$"The result limit of {maximumResults:N0} entries was reached."]
                : []
        };
    }

    public async Task<RelationshipAnalysisResult> AnalyzeResourceRelationshipsAsync(
        ResourceKind kind,
        int index,
        int maximumResults = 1000,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ResourceEntryInfo resourceEntry = GetResourceEntries(kind).FirstOrDefault(item => item.Index == index)
            ?? throw new ArgumentOutOfRangeException(nameof(index));

        List<RelationshipEntry> relationships = [];
        HashSet<string> dedupe = new(StringComparer.Ordinal);

        switch (kind)
        {
            case ResourceKind.Objects:
                AddObjectRelationships(index, relationships, dedupe);
                break;
            case ResourceKind.Rooms:
                AddRoomRelationships(index, relationships, dedupe);
                break;
            case ResourceKind.Sprites:
                AddSpriteRelationships(index, relationships, dedupe);
                break;
            case ResourceKind.Backgrounds:
                AddBackgroundRelationships(index, relationships, dedupe);
                break;
        }

        if (!Info.IsYyc && resourceEntry.Name.Length > 0)
        {
            IReadOnlyList<CodeSearchResult> references = await SearchCodeAsync(
                resourceEntry.Name,
                Math.Min(maximumResults, 500),
                progress,
                cancellationToken).ConfigureAwait(false);
            foreach (CodeSearchResult reference in references
                         .Where(item => item.Source == "GML")
                         .GroupBy(item => item.CodeIndex)
                         .Select(group => group.First()))
            {
                AddRelationship(relationships, dedupe, new RelationshipEntry(
                    RelationshipKind.HeuristicReference,
                    "Referenced by GML",
                    "Code",
                    reference.CodeName,
                    $"Line {reference.LineNumber:N0}: {reference.Snippet}",
                    CodeIndex: reference.CodeIndex));
            }
        }

        return new RelationshipAnalysisResult
        {
            Title = $"Relationships for {resourceEntry.Name}",
            Summary = BuildRelationshipSummary(resourceEntry.Name, relationships),
            Entries = relationships.Take(maximumResults).ToArray(),
            Warnings = relationships.Count >= maximumResults
                ? [$"The result limit of {maximumResults:N0} entries was reached."]
                : []
        };
    }

    public async Task<UnusedResourceReport> AnalyzeUnusedResourcesAsync(
        int maximumResults = 5000,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        HashSet<string> referencedNames = new(StringComparer.Ordinal);

        AddDirectModelReferences(referencedNames);
        if (!Info.IsYyc)
        {
            for (int index = 0; index < _codeEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new DecompileProgress(
                    DecompileStage.SearchingCode,
                    index,
                    _codeEntries.Count,
                    $"Indexing references [{index + 1:N0}/{_codeEntries.Count:N0}] {_codeEntries[index].Name}"));
                CodeViewResult view = await GetCodeViewAsync(index, cancellationToken).ConfigureAwait(false);
                foreach (Match match in IdentifierRegex.Matches(view.Gml))
                    referencedNames.Add(match.Value);
            }
        }

        List<UnusedResourceCandidate> candidates = [];
        ResourceKind[] candidateKinds =
        [
            ResourceKind.Sprites,
            ResourceKind.Sounds,
            ResourceKind.Backgrounds,
            ResourceKind.Paths,
            ResourceKind.Objects,
            ResourceKind.Rooms,
            ResourceKind.Shaders,
            ResourceKind.Fonts,
            ResourceKind.Sequences,
            ResourceKind.AnimationCurves,
            ResourceKind.ParticleSystems
        ];

        foreach (ResourceKind candidateKind in candidateKinds)
        {
            foreach (ResourceEntryInfo resource in GetResourceEntries(candidateKind))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (referencedNames.Contains(resource.Name))
                    continue;
                candidates.Add(new UnusedResourceCandidate(
                    candidateKind,
                    resource.Index,
                    resource.Name,
                    "No direct model reference or matching GML identifier was found.",
                    candidateKind is ResourceKind.Rooms or ResourceKind.Objects ? "Low" : "Medium"));
                if (candidates.Count >= maximumResults)
                    break;
            }
            if (candidates.Count >= maximumResults)
                break;
        }

        return new UnusedResourceReport
        {
            Summary = $"Found {candidates.Count:N0} heuristic unused-resource candidate(s). " +
                      "This is not proof that an asset is unused; dynamic lookup and extensions can hide references.",
            Candidates = candidates,
            Warnings =
            [
                "Results are heuristic and may contain false positives.",
                "Resources loaded by strings, extensions, native code, or YYC cannot always be resolved."
            ]
        };
    }

    private void AddObjectRelationships(
        int objectIndex,
        List<RelationshipEntry> output,
        HashSet<string> dedupe)
    {
        UndertaleGameObject? gameObject = GetAt(_data.GameObjects, objectIndex) as UndertaleGameObject;
        if (gameObject is null)
            return;

        AddResourceRelationship(output, dedupe, RelationshipKind.AssignedSprite, "Assigned sprite",
            ResourceKind.Sprites, gameObject.Sprite, _data.Sprites);
        AddResourceRelationship(output, dedupe, RelationshipKind.CollisionMask, "Collision mask",
            ResourceKind.Sprites, gameObject.TextureMaskId, _data.Sprites);
        AddResourceRelationship(output, dedupe, RelationshipKind.InheritsFrom, "Inherits from",
            ResourceKind.Objects, gameObject.ParentId, _data.GameObjects);

        for (int childIndex = 0; childIndex < (_data.GameObjects?.Count ?? 0); childIndex++)
        {
            UndertaleGameObject? child = GetAt(_data.GameObjects, childIndex) as UndertaleGameObject;
            if (ReferenceEquals(child?.ParentId, gameObject))
            {
                AddRelationship(output, dedupe, new RelationshipEntry(
                    RelationshipKind.InheritedBy,
                    "Inherited by",
                    "Object",
                    child?.Name?.Content ?? $"Object #{childIndex}",
                    "Direct object parent relationship.",
                    ResourceKind: ResourceKind.Objects,
                    ResourceIndex: childIndex));
            }
        }

        foreach (ConnectedCodeInfo code in GetConnectedCodeForObject(objectIndex))
        {
            AddRelationship(output, dedupe, new RelationshipEntry(
                RelationshipKind.EventCode,
                code.Relationship,
                "Code",
                code.CodeName,
                code.Details,
                CodeIndex: code.CodeIndex));
        }

        for (int roomIndex = 0; roomIndex < (_data.Rooms?.Count ?? 0); roomIndex++)
        {
            UndertaleRoom? room = GetAt(_data.Rooms, roomIndex) as UndertaleRoom;
            if (room?.GameObjects is null)
                continue;
            int count = room.GameObjects.Count(instance => ReferenceEquals(instance?.ObjectDefinition, gameObject));
            if (count > 0)
            {
                AddRelationship(output, dedupe, new RelationshipEntry(
                    RelationshipKind.RoomInstance,
                    "Placed in room",
                    "Room",
                    room.Name?.Content ?? $"Room #{roomIndex}",
                    $"{count:N0} instance(s) of this object are placed in the room.",
                    ResourceKind: ResourceKind.Rooms,
                    ResourceIndex: roomIndex));
            }
        }
    }

    private void AddRoomRelationships(
        int roomIndex,
        List<RelationshipEntry> output,
        HashSet<string> dedupe)
    {
        UndertaleRoom? room = GetAt(_data.Rooms, roomIndex) as UndertaleRoom;
        if (room is null)
            return;

        AddCodeRelationship(output, dedupe, RelationshipKind.CreationCode, "Room creation code", room.CreationCodeId, "Room-level creation code.");

        if (room.GameObjects is not null)
        {
            foreach (UndertaleRoom.GameObject instance in room.GameObjects)
            {
                if (instance?.ObjectDefinition is null)
                    continue;
                int objectIndex = IndexOfReference(_data.GameObjects, instance.ObjectDefinition);
                AddRelationship(output, dedupe, new RelationshipEntry(
                    RelationshipKind.InstanceOf,
                    "Contains instance",
                    "Object",
                    instance.ObjectDefinition.Name?.Content ?? "(unknown object)",
                    $"Instance ID {instance.InstanceID}; position {instance.X}, {instance.Y}.",
                    ResourceKind: objectIndex >= 0 ? ResourceKind.Objects : null,
                    ResourceIndex: objectIndex >= 0 ? objectIndex : null));
                AddCodeRelationship(output, dedupe, RelationshipKind.CreationCode, "Instance creation code", instance.CreationCode,
                    $"Instance ID {instance.InstanceID}.");
                AddCodeRelationship(output, dedupe, RelationshipKind.PreCreateCode, "Instance pre-create code", instance.PreCreateCode,
                    $"Instance ID {instance.InstanceID}.");
            }
        }

        if (room.Layers is not null)
        {
            foreach (UndertaleRoom.Layer layer in room.Layers)
            {
                if (layer?.BackgroundData?.Sprite is UndertaleSprite backgroundSprite)
                    AddResourceRelationship(output, dedupe, RelationshipKind.LayerAsset, "Background layer sprite",
                        ResourceKind.Sprites, backgroundSprite, _data.Sprites, layer.LayerName?.Content);
                if (layer?.TilesData?.Background is UndertaleBackground tileset)
                    AddResourceRelationship(output, dedupe, RelationshipKind.LayerAsset, "Tile layer tileset",
                        ResourceKind.Backgrounds, tileset, _data.Backgrounds, layer.LayerName?.Content);
                if (layer?.AssetsData?.Sprites is not null)
                {
                    foreach (UndertaleRoom.SpriteInstance spriteInstance in layer.AssetsData.Sprites)
                    {
                        AddResourceRelationship(output, dedupe, RelationshipKind.LayerAsset, "Asset-layer sprite",
                            ResourceKind.Sprites, spriteInstance?.Sprite, _data.Sprites, layer.LayerName?.Content);
                    }
                }
            }
        }
    }

    private void AddSpriteRelationships(
        int spriteIndex,
        List<RelationshipEntry> output,
        HashSet<string> dedupe)
    {
        UndertaleSprite? sprite = GetAt(_data.Sprites, spriteIndex) as UndertaleSprite;
        if (sprite is null)
            return;

        for (int objectIndex = 0; objectIndex < (_data.GameObjects?.Count ?? 0); objectIndex++)
        {
            UndertaleGameObject? gameObject = GetAt(_data.GameObjects, objectIndex) as UndertaleGameObject;
            if (ReferenceEquals(gameObject?.Sprite, sprite) || ReferenceEquals(gameObject?.TextureMaskId, sprite))
            {
                AddRelationship(output, dedupe, new RelationshipEntry(
                    RelationshipKind.ReferencedBy,
                    ReferenceEquals(gameObject?.Sprite, sprite) ? "Assigned to object" : "Used as collision mask",
                    "Object",
                    gameObject?.Name?.Content ?? $"Object #{objectIndex}",
                    "Direct resource relationship.",
                    ResourceKind: ResourceKind.Objects,
                    ResourceIndex: objectIndex));
            }
        }

        for (int roomIndex = 0; roomIndex < (_data.Rooms?.Count ?? 0); roomIndex++)
        {
            UndertaleRoom? room = GetAt(_data.Rooms, roomIndex) as UndertaleRoom;
            bool used = room?.Layers?.Any(layer =>
                ReferenceEquals(layer?.BackgroundData?.Sprite, sprite) ||
                (layer?.AssetsData?.Sprites?.Any(item => ReferenceEquals(item?.Sprite, sprite)) ?? false)) == true;
            if (used)
            {
                AddRelationship(output, dedupe, new RelationshipEntry(
                    RelationshipKind.ReferencedBy,
                    "Used by room layer",
                    "Room",
                    room?.Name?.Content ?? $"Room #{roomIndex}",
                    "The sprite is present on a background or asset layer.",
                    ResourceKind: ResourceKind.Rooms,
                    ResourceIndex: roomIndex));
            }
        }
    }

    private void AddBackgroundRelationships(
        int backgroundIndex,
        List<RelationshipEntry> output,
        HashSet<string> dedupe)
    {
        UndertaleBackground? background = GetAt(_data.Backgrounds, backgroundIndex) as UndertaleBackground;
        if (background is null)
            return;

        for (int roomIndex = 0; roomIndex < (_data.Rooms?.Count ?? 0); roomIndex++)
        {
            UndertaleRoom? room = GetAt(_data.Rooms, roomIndex) as UndertaleRoom;
            bool usedLegacy = room?.Backgrounds?.Any(item => ReferenceEquals(item?.BackgroundDefinition, background)) == true;
            bool usedTiles = room?.Layers?.Any(layer => ReferenceEquals(layer?.TilesData?.Background, background)) == true;
            if (!usedLegacy && !usedTiles)
                continue;
            AddRelationship(output, dedupe, new RelationshipEntry(
                RelationshipKind.ReferencedBy,
                usedTiles ? "Used as room tileset" : "Used as room background",
                "Room",
                room?.Name?.Content ?? $"Room #{roomIndex}",
                "Direct room resource relationship.",
                ResourceKind: ResourceKind.Rooms,
                ResourceIndex: roomIndex));
        }
    }

    private void AddDirectModelReferences(HashSet<string> names)
    {
        if (_data.GameObjects is not null)
        {
            foreach (UndertaleGameObject gameObject in _data.GameObjects)
            {
                AddName(names, gameObject?.Sprite?.Name?.Content);
                AddName(names, gameObject?.TextureMaskId?.Name?.Content);
                AddName(names, gameObject?.ParentId?.Name?.Content);
            }
        }

        if (_data.Rooms is null)
            return;

        foreach (UndertaleRoom room in _data.Rooms)
        {
            if (room?.GameObjects is not null)
            {
                foreach (UndertaleRoom.GameObject instance in room.GameObjects)
                    AddName(names, instance?.ObjectDefinition?.Name?.Content);
            }
            if (room?.Layers is null)
                continue;
            foreach (UndertaleRoom.Layer layer in room.Layers)
            {
                AddName(names, layer?.BackgroundData?.Sprite?.Name?.Content);
                AddName(names, layer?.TilesData?.Background?.Name?.Content);
                if (layer?.AssetsData?.Sprites is not null)
                {
                    foreach (UndertaleRoom.SpriteInstance sprite in layer.AssetsData.Sprites)
                        AddName(names, sprite?.Sprite?.Name?.Content);
                }
            }
        }
    }

    private Dictionary<string, int> BuildCallableLookup()
    {
        Dictionary<string, int> lookup = new(StringComparer.Ordinal);
        for (int index = 0; index < _codeEntries.Count; index++)
        {
            foreach (string alias in GetCallableAliases(_codeEntries[index].Name))
                lookup.TryAdd(alias, index);
        }
        return lookup;
    }

    private Dictionary<string, (ResourceKind Kind, int Index, string Name)> BuildNamedResourceLookup()
    {
        Dictionary<string, (ResourceKind, int, string)> lookup = new(StringComparer.Ordinal);
        ResourceKind[] kinds =
        [
            ResourceKind.Objects, ResourceKind.Rooms, ResourceKind.Sprites, ResourceKind.Sounds,
            ResourceKind.Backgrounds, ResourceKind.Paths, ResourceKind.Fonts, ResourceKind.Shaders,
            ResourceKind.Sequences, ResourceKind.AnimationCurves, ResourceKind.ParticleSystems,
            ResourceKind.ParticleSystemEmitters
        ];
        foreach (ResourceKind kind in kinds)
        {
            foreach (ResourceEntryInfo resource in GetResourceEntries(kind))
                lookup.TryAdd(resource.Name, (kind, resource.Index, resource.Name));
        }
        return lookup;
    }

    private void AddConnectedCode(
        List<ConnectedCodeInfo> output,
        UndertaleCode? code,
        string relationship,
        string details)
    {
        if (code is null)
            return;
        int index = FindCodeIndexByName(code.Name?.Content);
        if (index < 0)
            return;
        output.Add(new ConnectedCodeInfo(index, _codeEntries[index].Name, _codeEntries[index].Category, relationship, details));
    }

    private void AddCodeRelationship(
        List<RelationshipEntry> output,
        HashSet<string> dedupe,
        RelationshipKind kind,
        string relationship,
        UndertaleCode? code,
        string details)
    {
        if (code is null)
            return;
        int index = FindCodeIndexByName(code.Name?.Content);
        if (index < 0)
            return;
        AddRelationship(output, dedupe, new RelationshipEntry(
            kind,
            relationship,
            "Code",
            _codeEntries[index].Name,
            details,
            CodeIndex: index));
    }

    private void AddResourceRelationship<T>(
        List<RelationshipEntry> output,
        HashSet<string> dedupe,
        RelationshipKind kind,
        string relationship,
        ResourceKind resourceKind,
        T? resource,
        IList<T>? list,
        string? details = null)
        where T : class, UndertaleNamedResource
    {
        if (resource is null)
            return;
        int index = IndexOfReference(list, resource);
        string name = resource.Name?.Content ?? $"{resourceKind} #{index}";
        AddRelationship(output, dedupe, new RelationshipEntry(
            kind,
            relationship,
            resourceKind.ToString(),
            name,
            details ?? "Direct resource relationship.",
            ResourceKind: index >= 0 ? resourceKind : null,
            ResourceIndex: index >= 0 ? index : null));
    }

    private static int IndexOfReference<T>(IList<T>? list, T? target)
        where T : class
    {
        if (list is null || target is null)
            return -1;
        for (int index = 0; index < list.Count; index++)
        {
            if (ReferenceEquals(list[index], target))
                return index;
        }
        return -1;
    }

    private static void AddRelationship(
        List<RelationshipEntry> output,
        HashSet<string> dedupe,
        RelationshipEntry entry)
    {
        string key = $"{entry.Kind}|{entry.TargetType}|{entry.TargetName}|{entry.CodeIndex}|{entry.ResourceKind}|{entry.ResourceIndex}|{entry.Details}";
        if (dedupe.Add(key))
            output.Add(entry);
    }

    private static bool ContainsCall(string source, string alias)
    {
        return Regex.IsMatch(
            source,
            $@"(?<![A-Za-z0-9_\.]){Regex.Escape(alias)}\s*\(",
            RegexOptions.CultureInvariant);
    }

    private static string[] GetCallableAliases(string codeName)
    {
        string normalized = NormalizeCallableName(codeName);
        return string.Equals(normalized, codeName, StringComparison.Ordinal)
            ? [codeName]
            : [codeName, normalized];
    }

    private static string NormalizeCallableName(string codeName)
    {
        const string scriptPrefix = "gml_Script_";
        if (codeName.StartsWith(scriptPrefix, StringComparison.Ordinal))
            return codeName[scriptPrefix.Length..];
        return codeName;
    }

    private static string BuildRelationshipSummary(string name, IReadOnlyList<RelationshipEntry> entries)
    {
        StringBuilder summary = new();
        summary.AppendLine(name);
        summary.AppendLine(new string('=', Math.Min(72, name.Length)));
        summary.AppendLine($"Relationships found: {entries.Count:N0}");
        summary.AppendLine();
        foreach (IGrouping<RelationshipKind, RelationshipEntry> group in entries.GroupBy(item => item.Kind).OrderBy(group => group.Key))
            summary.AppendLine($"{group.Key,-22} {group.Count(),8:N0}");
        summary.AppendLine();
        summary.AppendLine("Double-click a navigable relationship to open its code or resource.");
        summary.AppendLine("Code and unused-resource results are heuristic when dynamic lookup is involved.");
        return summary.ToString();
    }

    private static void AddName(HashSet<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            names.Add(name);
    }
}

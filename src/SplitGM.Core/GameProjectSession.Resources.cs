using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ImageMagick;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Util;

namespace SplitGM.Core;

public sealed partial class GameProjectSession
{
    private const int MaximumCachedResourcePreviews = 64;
    private readonly ConcurrentDictionary<string, ResourcePreviewData> _resourcePreviewCache = new();
    private readonly ConcurrentQueue<string> _resourcePreviewCacheOrder = new();
    private readonly SemaphoreSlim _resourcePreviewGate = new(1, 1);
    private TextureWorker _textureWorker = new();
    private int _resourceImageOperations;
    private readonly Dictionary<int, UndertaleData?> _audioGroupData = [];
    private readonly object _audioGroupLock = new();

    public async Task<ResourcePreviewData> GetResourcePreviewAsync(
        ResourceKind kind,
        int index,
        int frameIndex = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string key = $"{kind}:{index}:{frameIndex}";
        if (_resourcePreviewCache.TryGetValue(key, out ResourcePreviewData? cached))
            return cached;

        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_resourcePreviewCache.TryGetValue(key, out cached))
                return cached;

            ResourcePreviewData preview = await Task.Run(
                () => BuildResourcePreview(kind, index, frameIndex, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            // Audio bytes are intentionally not placed in the preview cache. The preview only
            // carries metadata, while GetAudioPayloadAsync loads bytes when playback/export is requested.
            long previewBytes = preview.ImagePng?.LongLength ?? 0;
            long previewCharacters = preview.Text?.Length ?? 0;
            bool cachePreview = previewBytes <= 8L * 1024 * 1024 && previewCharacters <= 2_000_000;
            if (cachePreview)
            {
                _resourcePreviewCache[key] = preview;
                _resourcePreviewCacheOrder.Enqueue(key);
                while (_resourcePreviewCache.Count > MaximumCachedResourcePreviews &&
                       _resourcePreviewCacheOrder.TryDequeue(out string? oldest))
                {
                    if (!string.Equals(oldest, key, StringComparison.Ordinal))
                        _resourcePreviewCache.TryRemove(oldest, out _);
                }
            }

            if (preview.ImagePng is not null)
            {
                _resourceImageOperations++;
                if (previewBytes > 8L * 1024 * 1024 ||
                    kind is ResourceKind.TexturePages or ResourceKind.Rooms ||
                    _resourceImageOperations >= 32)
                {
                    ResetTextureWorkerUnsafe();
                }
            }
            return preview;
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    public async Task WaitForResourcePreviewIdleAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    public Task<AudioPayload> GetAudioPayloadAsync(
        int soundIndex,
        CancellationToken cancellationToken = default)
    {
        return GetAudioPayloadAsync(ResourceKind.Sounds, soundIndex, cancellationToken);
    }

    public async Task<AudioPayload> GetAudioPayloadAsync(
        ResourceKind kind,
        int index,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                () => GetAudioPayload(kind, index, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    public async Task<ResourceExportResult> ExportSelectedResourceAsync(
        ResourceKind kind,
        int index,
        string outputDirectory,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                () => ExportSelectedResource(kind, index, outputDirectory, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    public async Task<ResourceExportResult> ExportResourceCategoryAsync(
        ResourceKind kind,
        string outputDirectory,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                () => ExportResourceCategory(kind, outputDirectory, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    public async Task<ResourceExportResult> ExportAudioGroupAsync(
        int audioGroupIndex,
        string outputDirectory,
        IProgress<DecompileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _resourcePreviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(
                () => ExportAudioGroup(audioGroupIndex, outputDirectory, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    internal ResourceExportResult ExportSelectedResource(
        ResourceKind kind,
        int index,
        string outputDirectory,
        IProgress<DecompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(outputDirectory);
        ResourceEntryInfo entry = GetResourceEntries(kind).FirstOrDefault(item => item.Index == index)
            ?? new ResourceEntryInfo(kind, index, $"{kind} #{index}");
        string safeName = $"{index:D6}_{OutputPathHelper.SafeFileName(entry.Name)}";
        if (kind == ResourceKind.AudioGroups)
            return ExportAudioGroup(index, outputDirectory, progress, cancellationToken);

        int files = 0;
        long bytes = 0;

        void CountFile(string path)
        {
            files++;
            bytes += new FileInfo(path).Length;
        }

        progress?.Report(new DecompileProgress(
            DecompileStage.ExportingResources,
            0,
            1,
            $"Exporting {entry.Name}..."));

        cancellationToken.ThrowIfCancellationRequested();
        string detailsPath = Path.Combine(outputDirectory, safeName + ".details.txt");
        File.WriteAllText(detailsPath, GetResourceDetails(kind, index), new UTF8Encoding(false));
        CountFile(detailsPath);

        switch (kind)
        {
            case ResourceKind.Sprites:
            {
                UndertaleSprite? sprite = GetAt(_data.Sprites, index) as UndertaleSprite;
                if (sprite is not null)
                {
                    string spriteDirectory = Path.Combine(outputDirectory, safeName);
                    Directory.CreateDirectory(spriteDirectory);
                    int frameCount = sprite.Textures?.Count ?? 0;
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        UndertaleTexturePageItem? item = sprite.Textures?[frame]?.Texture;
                        if (item is null)
                            continue;
                        string path = Path.Combine(spriteDirectory, $"frame_{frame:D4}.png");
                        _textureWorker.ExportAsPNG(item, path, entry.Name, includePadding: true);
                        CountFile(path);
                    }

                    if (sprite.CollisionMasks is not null && sprite.CollisionMasks.Count > 0)
                    {
                        (int maskWidth, int maskHeight) = sprite.CalculateMaskDimensions(_data);
                        string maskDirectory = Path.Combine(spriteDirectory, "CollisionMasks");
                        Directory.CreateDirectory(maskDirectory);
                        for (int maskIndex = 0; maskIndex < sprite.CollisionMasks.Count; maskIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            UndertaleSprite.MaskEntry? mask = sprite.CollisionMasks[maskIndex];
                            if (mask?.Data is null)
                                continue;
                            string maskPath = Path.Combine(maskDirectory, $"mask_{maskIndex:D4}.png");
                            TextureWorker.ExportCollisionMaskPNG(mask, maskPath, maskWidth, maskHeight);
                            CountFile(maskPath);
                        }
                    }

                    if (sprite.IsSpineSprite)
                    {
                        if (!string.IsNullOrWhiteSpace(sprite.SpineJSON))
                        {
                            string spineJsonPath = Path.Combine(spriteDirectory, "spine.json");
                            File.WriteAllText(spineJsonPath, sprite.SpineJSON, new UTF8Encoding(false));
                            CountFile(spineJsonPath);
                        }
                        if (!string.IsNullOrWhiteSpace(sprite.SpineAtlas))
                        {
                            string spineAtlasPath = Path.Combine(spriteDirectory, "spine.atlas");
                            File.WriteAllText(spineAtlasPath, sprite.SpineAtlas, new UTF8Encoding(false));
                            CountFile(spineAtlasPath);
                        }
                    }

                    string metadataPath = Path.Combine(spriteDirectory, "sprite.json");
                    WriteJson(metadataPath, new
                    {
                        entry.Name,
                        sprite.Width,
                        sprite.Height,
                        OriginX = sprite.OriginXWrapper,
                        OriginY = sprite.OriginYWrapper,
                        FrameCount = frameCount,
                        SpriteType = sprite.SSpriteType.ToString(),
                        sprite.BBoxMode,
                        sprite.SepMasks
                    });
                    CountFile(metadataPath);
                }
                break;
            }
            case ResourceKind.Backgrounds:
            {
                UndertaleBackground? background = GetAt(_data.Backgrounds, index) as UndertaleBackground;
                if (background?.Texture is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + ".png");
                    _textureWorker.ExportAsPNG(background.Texture, path, entry.Name, includePadding: true);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.Fonts:
            {
                UndertaleFont? font = GetAt(_data.Fonts, index) as UndertaleFont;
                if (font?.Texture is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + "_atlas.png");
                    _textureWorker.ExportAsPNG(font.Texture, path, entry.Name, includePadding: true);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.TexturePageItems:
            {
                UndertaleTexturePageItem? item = GetAt(_data.TexturePageItems, index) as UndertaleTexturePageItem;
                if (item is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + ".png");
                    _textureWorker.ExportAsPNG(item, path, entry.Name, includePadding: true);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.TexturePages:
            {
                UndertaleEmbeddedTexture? texture = GetAt(_data.EmbeddedTextures, index) as UndertaleEmbeddedTexture;
                if (texture is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + ".png");
                    using FileStream stream = File.Create(path);
                    texture.TextureData.Image.SavePng(stream);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.EmbeddedImages:
            {
                UndertaleEmbeddedImage? embeddedImage = GetAt(_data.EmbeddedImages, index) as UndertaleEmbeddedImage;
                if (embeddedImage?.TextureEntry is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + ".png");
                    _textureWorker.ExportAsPNG(embeddedImage.TextureEntry, path, entry.Name, includePadding: true);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.Sounds:
            {
                AudioPayload payload = GetAudioPayload(index, cancellationToken);
                string path = Path.Combine(outputDirectory, safeName + payload.Extension);
                File.WriteAllBytes(path, payload.Data);
                CountFile(path);
                break;
            }
            case ResourceKind.EmbeddedAudio:
            {
                UndertaleEmbeddedAudio? audio = GetAt(_data.EmbeddedAudio, index) as UndertaleEmbeddedAudio;
                if (audio is not null)
                {
                    string extension = DetectAudioFormat(audio.Data).Extension;
                    string path = Path.Combine(outputDirectory, safeName + extension);
                    File.WriteAllBytes(path, audio.Data);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.Rooms:
            {
                ResourcePreviewData preview = BuildRoomPreview(index, cancellationToken);
                if (preview.ImagePng is not null)
                {
                    string previewPath = Path.Combine(outputDirectory, safeName + "_preview.png");
                    File.WriteAllBytes(previewPath, preview.ImagePng);
                    CountFile(previewPath);
                }
                if (preview.Room is not null)
                {
                    string roomPath = Path.Combine(outputDirectory, safeName + ".room.json");
                    WriteJson(roomPath, preview.Room);
                    CountFile(roomPath);
                }
                break;
            }
            case ResourceKind.Objects:
            {
                ResourcePreviewData preview = BuildObjectPreview(index);
                if (preview.ImagePng is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + "_sprite.png");
                    File.WriteAllBytes(path, preview.ImagePng);
                    CountFile(path);
                }
                if (preview.Object is not null)
                {
                    string path = Path.Combine(outputDirectory, safeName + ".object.json");
                    WriteJson(path, preview.Object);
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.Paths:
            {
                ResourcePreviewData preview = BuildPathPreview(index);
                if (!string.IsNullOrWhiteSpace(preview.Text))
                {
                    string path = Path.Combine(outputDirectory, safeName + ".path.txt");
                    File.WriteAllText(path, preview.Text, new UTF8Encoding(false));
                    CountFile(path);
                }
                break;
            }
            case ResourceKind.Timelines:
            {
                ResourcePreviewData preview = BuildTimelinePreview(index);
                if (!string.IsNullOrWhiteSpace(preview.Text))
                {
                    string path = Path.Combine(outputDirectory, safeName + ".timeline.txt");
                    File.WriteAllText(path, preview.Text, new UTF8Encoding(false));
                    CountFile(path);
                }
                break;
            }
            default:
            {
                object? resource = GetRawResource(kind, index);
                string text = ExtractTextResource(resource);
                if (string.IsNullOrWhiteSpace(text))
                    text = ObjectInspector.FormatTree(resource, entry.Name, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string path = Path.Combine(outputDirectory, safeName + ".txt");
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    CountFile(path);
                }
                break;
            }
        }

        progress?.Report(new DecompileProgress(
            DecompileStage.ExportingResources,
            1,
            1,
            $"Exported {entry.Name}."));
        return new ResourceExportResult(outputDirectory, kind, index, entry.Name, files, bytes);
    }

    internal ResourceExportResult ExportResourceCategory(
        ResourceKind kind,
        string outputDirectory,
        IProgress<DecompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        IReadOnlyList<ResourceEntryInfo> entries = GetResourceEntries(kind);
        string categoryName = GetResourceCategoryDirectoryName(kind);
        string categoryDirectory = Path.Combine(outputDirectory, categoryName);
        Directory.CreateDirectory(categoryDirectory);

        int files = 0;
        long bytes = 0;
        int failed = 0;

        for (int position = 0; position < entries.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceEntryInfo entry = entries[position];
            progress?.Report(new DecompileProgress(
                DecompileStage.ExportingResources,
                position,
                entries.Count,
                $"Exporting {kind} [{position + 1:N0}/{entries.Count:N0}] {entry.Name}"));

            try
            {
                ResourceExportResult result = ExportSelectedResource(
                    kind,
                    entry.Index,
                    categoryDirectory,
                    progress: null,
                    cancellationToken);
                files += result.FilesWritten;
                bytes += result.BytesWritten;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                string errorPath = Path.Combine(
                    categoryDirectory,
                    $"{entry.Index:D6}_{OutputPathHelper.SafeFileName(entry.Name)}.error.txt");
                File.WriteAllText(errorPath, exception.ToString(), new UTF8Encoding(false));
                files++;
                bytes += new FileInfo(errorPath).Length;
            }

            if ((position & 0x1F) == 0 && kind is (ResourceKind.Sprites or ResourceKind.Backgrounds or ResourceKind.Fonts or ResourceKind.TexturePageItems or ResourceKind.TexturePages or ResourceKind.EmbeddedImages or ResourceKind.Rooms))
                ResetTextureWorkerUnsafe();
        }

        string summaryPath = Path.Combine(categoryDirectory, "SplitGM-Category-Export.txt");
        File.WriteAllText(
            summaryPath,
            $"SplitGM resource-category export{Environment.NewLine}" +
            $"Category: {kind}{Environment.NewLine}" +
            $"Resources: {entries.Count:N0}{Environment.NewLine}" +
            $"Failed: {failed:N0}{Environment.NewLine}" +
            $"Files written: {files:N0}{Environment.NewLine}" +
            $"Bytes written: {bytes:N0}{Environment.NewLine}",
            new UTF8Encoding(false));
        files++;
        bytes += new FileInfo(summaryPath).Length;

        progress?.Report(new DecompileProgress(
            DecompileStage.ExportingResources,
            entries.Count,
            entries.Count,
            failed == 0
                ? $"Exported all {kind}."
                : $"Exported {kind} with {failed:N0} failed resource(s)."));

        return new ResourceExportResult(
            categoryDirectory,
            kind,
            -1,
            $"All {kind}",
            files,
            bytes);
    }

    private static string GetResourceCategoryDirectoryName(ResourceKind kind)
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
            ResourceKind.EmbeddedAudio => "Embedded-Audio",
            ResourceKind.FilterEffects => "Filter-Effects",
            ResourceKind.AudioGroups => "Audio-Groups",
            _ => kind.ToString()
        };
    }

    internal ResourceExportResult ExportAudioGroup(
        int audioGroupIndex,
        string outputDirectory,
        IProgress<DecompileProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        UndertaleAudioGroup? group = GetAt(_data.AudioGroups, audioGroupIndex) as UndertaleAudioGroup;
        string groupName = group?.Name?.Content ?? $"audiogroup_{audioGroupIndex}";
        string groupDirectory = Path.Combine(
            outputDirectory,
            $"{audioGroupIndex:D4}_{OutputPathHelper.SafeFileName(groupName)}");
        Directory.CreateDirectory(groupDirectory);

        List<(UndertaleSound Sound, int Index)> sounds = _data.Sounds?
            .Select((sound, index) => (Sound: sound, Index: index))
            .Where(item => item.Sound is not null &&
                           ((group is not null && ReferenceEquals(item.Sound.AudioGroup, group)) ||
                            item.Sound.GroupID == audioGroupIndex))
            .ToList() ?? [];

        int files = 0;
        long bytes = 0;
        string detailsPath = Path.Combine(groupDirectory, "audio-group.details.txt");
        File.WriteAllText(detailsPath, ObjectInspector.Format(group, groupName), new UTF8Encoding(false));
        files++;
        bytes += new FileInfo(detailsPath).Length;

        string manifestPath = Path.Combine(groupDirectory, "audio-group.json");
        WriteJson(manifestPath, new
        {
            Name = groupName,
            GroupId = audioGroupIndex,
            RelativePath = group?.Path?.Content,
            SoundCount = sounds.Count,
            Sounds = sounds.Select(item => new
            {
                item.Index,
                Name = item.Sound.Name?.Content,
                AudioId = item.Sound.AudioID,
                GroupId = item.Sound.GroupID,
                Type = item.Sound.Type?.Content,
                File = item.Sound.File?.Content,
                Flags = item.Sound.Flags.ToString()
            }).ToArray()
        });
        files++;
        bytes += new FileInfo(manifestPath).Length;
        for (int position = 0; position < sounds.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (UndertaleSound sound, int soundIndex) = sounds[position];
            progress?.Report(new DecompileProgress(
                DecompileStage.ExportingResources,
                position,
                sounds.Count,
                $"Exporting {groupName}: {sound.Name?.Content ?? soundIndex.ToString()}"));
            try
            {
                AudioPayload payload = GetAudioPayload(soundIndex, cancellationToken);
                string path = Path.Combine(
                    groupDirectory,
                    $"{soundIndex:D6}_{OutputPathHelper.SafeFileName(sound.Name?.Content ?? $"sound_{soundIndex}")}{payload.Extension}");
                File.WriteAllBytes(path, payload.Data);
                files++;
                bytes += payload.Data.LongLength;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                string errorPath = Path.Combine(groupDirectory, $"sound_{soundIndex:D5}.error.txt");
                File.WriteAllText(errorPath, exception.ToString(), new UTF8Encoding(false));
                files++;
                bytes += new FileInfo(errorPath).Length;
            }
        }

        progress?.Report(new DecompileProgress(
            DecompileStage.ExportingResources,
            sounds.Count,
            sounds.Count,
            $"Exported audio group {groupName}."));
        return new ResourceExportResult(groupDirectory, ResourceKind.AudioGroups, audioGroupIndex, groupName, files, bytes);
    }

    private ResourcePreviewData BuildResourcePreview(
        ResourceKind kind,
        int index,
        int frameIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return kind switch
        {
            ResourceKind.Sprites => BuildSpritePreview(index, frameIndex),
            ResourceKind.Backgrounds => BuildBackgroundPreview(index),
            ResourceKind.Fonts => BuildFontPreview(index),
            ResourceKind.TexturePageItems => BuildTexturePageItemPreview(index),
            ResourceKind.TexturePages => BuildTexturePagePreview(index),
            ResourceKind.EmbeddedImages => BuildEmbeddedImagePreview(index),
            ResourceKind.Rooms => BuildRoomPreview(index, cancellationToken),
            ResourceKind.Objects => BuildObjectPreview(index),
            ResourceKind.Sounds => BuildSoundPreview(index, cancellationToken),
            ResourceKind.EmbeddedAudio => BuildEmbeddedAudioPreview(index),
            ResourceKind.AudioGroups => BuildAudioGroupPreview(index),
            ResourceKind.Shaders => BuildTextPreview(kind, index, "Shader source"),
            ResourceKind.Strings => BuildTextPreview(kind, index, "String data"),
            ResourceKind.Paths => BuildPathPreview(index),
            ResourceKind.Timelines => BuildTimelinePreview(index),
            _ => BuildGenericPreview(kind, index, ResourcePreviewKind.Generic, cancellationToken)
        };
    }

    private ResourcePreviewData BuildSpritePreview(int index, int requestedFrame)
    {
        UndertaleSprite? sprite = GetAt(_data.Sprites, index) as UndertaleSprite;
        string name = sprite?.Name?.Content ?? $"Sprite #{index}";
        int frameCount = sprite?.Textures?.Count ?? 0;
        int frame = frameCount == 0 ? 0 : ((requestedFrame % frameCount) + frameCount) % frameCount;
        byte[]? image = null;
        if (sprite?.SSpriteType == UndertaleSprite.SpriteType.Normal && frameCount > 0)
        {
            UndertaleTexturePageItem? item = sprite.Textures?[frame]?.Texture;
            if (item is not null)
                image = TextureItemToPng(item, name, includePadding: true);
        }

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Sprites,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Sprite,
            Subtitle = $"{frameCount:N0} frame(s) • {sprite?.Width ?? 0}×{sprite?.Height ?? 0}",
            Details = ObjectInspector.Format(sprite, name),
            ImagePng = image,
            Sprite = new SpritePreviewInfo(
                frame,
                frameCount,
                Convert.ToInt32(sprite?.Width ?? 0),
                Convert.ToInt32(sprite?.Height ?? 0),
                sprite?.OriginXWrapper ?? 0,
                sprite?.OriginYWrapper ?? 0,
                sprite?.SSpriteType.ToString() ?? "Unknown")
        };
    }

    private ResourcePreviewData BuildBackgroundPreview(int index)
    {
        UndertaleBackground? background = GetAt(_data.Backgrounds, index) as UndertaleBackground;
        string name = background?.Name?.Content ?? $"Background #{index}";
        byte[]? image = background?.Texture is null
            ? null
            : TextureItemToPng(background.Texture, name, includePadding: true);
        return CreateImagePreview(ResourceKind.Backgrounds, index, name, background, image, "Background / tileset");
    }

    private ResourcePreviewData BuildFontPreview(int index)
    {
        UndertaleFont? font = GetAt(_data.Fonts, index) as UndertaleFont;
        string name = font?.Name?.Content ?? $"Font #{index}";
        byte[]? image = font?.Texture is null
            ? null
            : TextureItemToPng(font.Texture, name, includePadding: true);
        return CreateImagePreview(ResourceKind.Fonts, index, name, font, image, "Font atlas and glyph data");
    }

    private ResourcePreviewData BuildTexturePageItemPreview(int index)
    {
        UndertaleTexturePageItem? item = GetAt(_data.TexturePageItems, index) as UndertaleTexturePageItem;
        string name = $"Texture Page Item {index}";
        byte[]? image = item is null
            ? null
            : TextureItemToPng(item, name, includePadding: true);
        return CreateImagePreview(
            ResourceKind.TexturePageItems,
            index,
            name,
            item,
            image,
            item is null
                ? "Texture page region"
                : $"Source {item.SourceWidth}×{item.SourceHeight} • target {item.TargetWidth}×{item.TargetHeight}");
    }

    private ResourcePreviewData BuildTexturePagePreview(int index)
    {
        UndertaleEmbeddedTexture? texture = GetAt(_data.EmbeddedTextures, index) as UndertaleEmbeddedTexture;
        string name = $"Texture Page {index}";
        byte[]? image = null;
        if (texture is not null)
        {
            using IMagickImage<byte> page = _textureWorker.GetEmbeddedTexture(texture).Clone();
            image = MagickToPng(page);
        }
        return CreateImagePreview(ResourceKind.TexturePages, index, name, texture, image, "Embedded texture page");
    }

    private ResourcePreviewData BuildEmbeddedImagePreview(int index)
    {
        UndertaleEmbeddedImage? embeddedImage = GetAt(_data.EmbeddedImages, index) as UndertaleEmbeddedImage;
        string name = embeddedImage?.Name?.Content ?? $"Embedded Image {index}";
        byte[]? image = embeddedImage?.TextureEntry is null
            ? null
            : TextureItemToPng(embeddedImage.TextureEntry, name, includePadding: true);
        return CreateImagePreview(
            ResourceKind.EmbeddedImages,
            index,
            name,
            embeddedImage,
            image,
            "GameMaker embedded image");
    }

    private ResourcePreviewData BuildObjectPreview(int index)
    {
        UndertaleGameObject? gameObject = GetAt(_data.GameObjects, index) as UndertaleGameObject;
        string name = gameObject?.Name?.Content ?? $"Object #{index}";
        UndertaleSprite? sprite = gameObject?.Sprite;
        byte[]? image = null;
        if (sprite?.SSpriteType == UndertaleSprite.SpriteType.Normal &&
            sprite.Textures?.FirstOrDefault()?.Texture is UndertaleTexturePageItem item)
        {
            image = TextureItemToPng(item, sprite.Name?.Content ?? name, includePadding: true);
        }

        List<ObjectEventInfo> events = [];
        if (gameObject?.Events is not null)
        {
            for (int eventTypeIndex = 0; eventTypeIndex < gameObject.Events.Count; eventTypeIndex++)
            {
                UndertalePointerList<UndertaleGameObject.Event>? subEvents = gameObject.Events[eventTypeIndex];
                if (subEvents is null)
                    continue;
                foreach (UndertaleGameObject.Event eventEntry in subEvents)
                {
                    if (eventEntry is null)
                        continue;
                    UndertaleGameObject.EventAction? action = eventEntry.Actions?.FirstOrDefault();
                    string codeName = action?.CodeId?.Name?.Content ?? "(no code entry)";
                    events.Add(new ObjectEventInfo(
                        ((EventType)eventTypeIndex).ToString(),
                        eventEntry.EventSubtype,
                        codeName,
                        eventEntry.Actions?.Count ?? 0,
                        FindCodeIndexByName(codeName)));
                }
            }
        }

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Objects,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Object,
            Subtitle = sprite is null
                ? $"No assigned sprite • {events.Count:N0} event(s)"
                : $"Sprite: {sprite.Name?.Content} • {events.Count:N0} event(s)",
            Details = ObjectInspector.Format(gameObject, name),
            ImagePng = image,
            Object = gameObject is null ? null : new ObjectPreviewInfo
            {
                SpriteName = sprite?.Name?.Content,
                ParentObjectName = gameObject.ParentId?.Name?.Content,
                CollisionMaskName = gameObject.TextureMaskId?.Name?.Content,
                Visible = gameObject.Visible,
                Solid = gameObject.Solid,
                Persistent = gameObject.Persistent,
                Depth = gameObject.Depth,
                Events = events
            }
        };
    }

    private ResourcePreviewData BuildSoundPreview(int index, CancellationToken cancellationToken)
    {
        UndertaleSound? sound = GetAt(_data.Sounds, index) as UndertaleSound;
        string name = sound?.Name?.Content ?? $"Sound #{index}";
        AudioPreviewInfo audio;
        try
        {
            AudioPayload payload = GetAudioPayload(index, cancellationToken);
            audio = new AudioPreviewInfo(
                payload.Format,
                payload.Extension,
                payload.Source,
                payload.AudioGroup,
                payload.GroupId,
                payload.AudioId,
                payload.Data.LongLength,
                true,
                payload.Source.StartsWith("External file", StringComparison.Ordinal)
                    ? ResolveExternalSoundPath(sound)
                    : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            (string format, string extension) = DetectAudioFormat(sound?.AudioFile?.Data);
            audio = new AudioPreviewInfo(
                format,
                extension,
                "Unavailable",
                sound?.AudioGroup?.Name?.Content ?? "Unknown",
                sound?.GroupID ?? -1,
                sound?.AudioID ?? -1,
                0,
                false,
                exception.Message);
        }

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Sounds,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Audio,
            Subtitle = $"{audio.Format} • {audio.Source} • {FormatBytes(audio.DataLength)}",
            Details = ObjectInspector.Format(sound, name),
            Audio = audio
        };
    }

    private ResourcePreviewData BuildEmbeddedAudioPreview(int index)
    {
        UndertaleEmbeddedAudio? audio = GetAt(_data.EmbeddedAudio, index) as UndertaleEmbeddedAudio;
        string name = $"Embedded Audio {index}";
        (string format, string extension) = DetectAudioFormat(audio?.Data);
        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.EmbeddedAudio,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Audio,
            Subtitle = $"{format} • {FormatBytes(audio?.Data?.LongLength ?? 0)}",
            Details = ObjectInspector.Format(audio, name),
            Audio = new AudioPreviewInfo(
                format,
                extension,
                "Main data archive",
                "Embedded Audio",
                _data.GetBuiltinSoundGroupID(),
                index,
                audio?.Data?.LongLength ?? 0,
                audio?.Data is { Length: > 0 },
                null)
        };
    }

    private ResourcePreviewData BuildAudioGroupPreview(int index)
    {
        UndertaleAudioGroup? group = GetAt(_data.AudioGroups, index) as UndertaleAudioGroup;
        string name = group?.Name?.Content ?? $"Audio Group #{index}";
        int soundCount = _data.Sounds?.Count(sound => sound is not null &&
            ((group is not null && ReferenceEquals(sound.AudioGroup, group)) || sound.GroupID == index)) ?? 0;
        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.AudioGroups,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Audio,
            Subtitle = $"{soundCount:N0} sound resource(s)",
            Details = ObjectInspector.Format(group, name),
            Audio = new AudioPreviewInfo(
                "Audio group",
                ".dat",
                group?.Path?.Content ?? $"audiogroup{index}.dat",
                name,
                index,
                -1,
                0,
                false,
                ResolveAudioGroupPath(group, index))
        };
    }

    private ResourcePreviewData BuildRoomPreview(int index, CancellationToken cancellationToken)
    {
        UndertaleRoom? room = GetAt(_data.Rooms, index) as UndertaleRoom;
        string name = room?.Name?.Content ?? $"Room #{index}";
        if (room is null)
            return BuildGenericPreview(ResourceKind.Rooms, index, ResourcePreviewKind.Room, cancellationToken);

        List<RoomLayerInfo> layers = [];
        Dictionary<uint, string> instanceLayerNames = [];
        List<RoomTileInfo> tiles = [];

        if (room.Layers is not null && room.Layers.Count > 0)
        {
            for (int layerIndex = 0; layerIndex < room.Layers.Count; layerIndex++)
            {
                UndertaleRoom.Layer? layer = room.Layers[layerIndex];
                if (layer is null)
                    continue;
                string layerName = layer.LayerName?.Content ?? $"Layer {layerIndex}";
                int itemCount = CountLayerItems(layer);
                layers.Add(new RoomLayerInfo(
                    layerIndex,
                    layerName,
                    layer.LayerType.ToString(),
                    layer.LayerDepth,
                    layer.IsVisible,
                    layer.XOffset,
                    layer.YOffset,
                    itemCount));

                if (layer.InstancesData?.Instances is not null)
                {
                    foreach (UndertaleRoom.GameObject instance in layer.InstancesData.Instances)
                        instanceLayerNames[instance.InstanceID] = layerName;
                }
                if (layer.AssetsData?.LegacyTiles is not null)
                {
                    foreach (UndertaleRoom.Tile tile in layer.AssetsData.LegacyTiles)
                        tiles.Add(ToRoomTileInfo(tile, layerName));
                }
            }
        }
        else
        {
            layers.Add(new RoomLayerInfo(0, "Legacy room resources", "Legacy", 0, true, 0, 0,
                (room.GameObjects?.Count ?? 0) + (room.Tiles?.Count ?? 0) + (room.Backgrounds?.Count ?? 0)));
        }

        if (room.Tiles is not null)
        {
            foreach (UndertaleRoom.Tile tile in room.Tiles)
                tiles.Add(ToRoomTileInfo(tile, "Legacy tiles"));
        }

        List<RoomInstanceInfo> instances = [];
        if (room.GameObjects is not null)
        {
            foreach (UndertaleRoom.GameObject instance in room.GameObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                instances.Add(new RoomInstanceInfo(
                    instance.InstanceID,
                    instance.ObjectDefinition?.Name?.Content ?? "(empty instance)",
                    instanceLayerNames.GetValueOrDefault(instance.InstanceID, "Legacy / unassigned"),
                    instance.X,
                    instance.Y,
                    instance.ScaleX,
                    instance.ScaleY,
                    instance.Rotation,
                    instance.ImageIndex,
                    instance.ObjectDefinition?.Sprite?.Name?.Content,
                    instance.CreationCode?.Name?.Content,
                    instance.ObjectDefinition is null ? -1 : IndexOfReference(_data.GameObjects, instance.ObjectDefinition)));
            }
        }

        (byte[]? image, int rendered, int skipped) = RenderRoomToPng(room, cancellationToken);
        RoomPreviewInfo roomInfo = new()
        {
            Width = room.Width,
            Height = room.Height,
            Speed = room.Speed,
            Persistent = room.Persistent,
            Layers = layers.OrderBy(layer => layer.Depth).ToArray(),
            Instances = instances,
            Tiles = tiles,
            RenderedObjectCount = rendered,
            SkippedObjectCount = skipped
        };

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Rooms,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Room,
            Subtitle = $"{room.Width:N0}×{room.Height:N0} • {layers.Count:N0} layers • {instances.Count:N0} instances",
            Details = ObjectInspector.Format(room, name),
            ImagePng = image,
            Room = roomInfo
        };
    }

    private ResourcePreviewData BuildPathPreview(int index)
    {
        UndertalePath? path = GetAt(_data.Paths, index) as UndertalePath;
        string name = path?.Name?.Content ?? $"Path #{index}";
        StringBuilder text = new();
        text.AppendLine(name);
        text.AppendLine(new string('=', Math.Min(name.Length, 72)));
        text.AppendLine($"Smooth: {path?.IsSmooth ?? false}");
        text.AppendLine($"Closed: {path?.IsClosed ?? false}");
        text.AppendLine($"Precision: {path?.Precision ?? 0}");
        text.AppendLine($"Points: {path?.Points?.Count ?? 0}");
        text.AppendLine();
        text.AppendLine("Index	X	Y	Speed");
        if (path?.Points is not null)
        {
            for (int pointIndex = 0; pointIndex < path.Points.Count; pointIndex++)
            {
                UndertalePath.PathPoint? point = path.Points[pointIndex];
                if (point is null)
                    continue;
                text.Append(pointIndex).Append('\t')
                    .Append(point.X.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .AppendLine(point.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Paths,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Path,
            Subtitle = $"{(path?.Points?.Count ?? 0):N0} point(s) • {(path?.IsClosed == true ? "closed" : "open")}",
            Details = ObjectInspector.Format(path, name),
            Text = text.ToString()
        };
    }

    private ResourcePreviewData BuildTimelinePreview(int index)
    {
        UndertaleTimeline? timeline = GetAt(_data.Timelines, index) as UndertaleTimeline;
        string name = timeline?.Name?.Content ?? $"Timeline #{index}";
        StringBuilder text = new();
        text.AppendLine(name);
        text.AppendLine(new string('=', Math.Min(name.Length, 72)));
        text.AppendLine($"Moments: {timeline?.Moments?.Count ?? 0}");
        text.AppendLine();
        text.AppendLine("Step	Actions	Code entries");
        if (timeline?.Moments is not null)
        {
            foreach (UndertaleTimeline.UndertaleTimelineMoment moment in timeline.Moments)
            {
                if (moment is null)
                    continue;
                string codeNames = moment.Event is null
                    ? string.Empty
                    : string.Join(", ", moment.Event
                        .Where(action => action?.CodeId?.Name?.Content is not null)
                        .Select(action => action.CodeId.Name.Content));
                text.Append(moment.Step).Append('\t')
                    .Append(moment.Event?.Count ?? 0).Append('\t')
                    .AppendLine(codeNames);
            }
        }

        return new ResourcePreviewData
        {
            ResourceKind = ResourceKind.Timelines,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Text,
            Subtitle = $"{(timeline?.Moments?.Count ?? 0):N0} timeline moment(s)",
            Details = ObjectInspector.Format(timeline, name),
            Text = text.ToString()
        };
    }

    private ResourcePreviewData BuildTextPreview(ResourceKind kind, int index, string subtitle)
    {
        object? resource = GetRawResource(kind, index);
        string name = GetResourceEntries(kind).FirstOrDefault(entry => entry.Index == index)?.Name
                      ?? $"{kind} #{index}";
        string text = ExtractTextResource(resource);
        return new ResourcePreviewData
        {
            ResourceKind = kind,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Text,
            Subtitle = subtitle,
            Details = ObjectInspector.Format(resource, name),
            Text = text
        };
    }

    private ResourcePreviewData BuildGenericPreview(
        ResourceKind kind,
        int index,
        ResourcePreviewKind previewKind,
        CancellationToken cancellationToken)
    {
        object? resource = GetRawResource(kind, index);
        string name = GetResourceEntries(kind).FirstOrDefault(entry => entry.Index == index)?.Name
                      ?? $"{kind} #{index}";
        string text = ExtractTextResource(resource);
        if (string.IsNullOrWhiteSpace(text))
            text = ObjectInspector.FormatTree(resource, name, cancellationToken);
        return new ResourcePreviewData
        {
            ResourceKind = kind,
            ResourceIndex = index,
            Name = name,
            PreviewKind = previewKind,
            Subtitle = $"{kind} resource #{index:N0}",
            Details = ObjectInspector.Format(resource, name),
            Text = text
        };
    }

    private static ResourcePreviewData CreateImagePreview(
        ResourceKind kind,
        int index,
        string name,
        object? resource,
        byte[]? image,
        string subtitle)
    {
        return new ResourcePreviewData
        {
            ResourceKind = kind,
            ResourceIndex = index,
            Name = name,
            PreviewKind = ResourcePreviewKind.Image,
            Subtitle = subtitle,
            Details = ObjectInspector.Format(resource, name),
            ImagePng = image
        };
    }

    private AudioPayload GetAudioPayload(ResourceKind kind, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (kind == ResourceKind.Sounds)
            return GetAudioPayload(index, cancellationToken);
        if (kind == ResourceKind.EmbeddedAudio)
        {
            UndertaleEmbeddedAudio? audio = GetAt(_data.EmbeddedAudio, index) as UndertaleEmbeddedAudio
                ?? throw new ArgumentOutOfRangeException(nameof(index));
            byte[] data = audio.Data ?? throw new FileNotFoundException($"Embedded audio #{index} has no data.");
            (string format, string extension) = DetectAudioFormat(data);
            return new AudioPayload(
                data,
                format,
                extension,
                "Main data archive",
                "Embedded Audio",
                _data.GetBuiltinSoundGroupID(),
                index,
                $"embedded_audio_{index:D6}{extension}");
        }
        throw new NotSupportedException($"{kind} is not a directly playable audio resource.");
    }

    private AudioPayload GetAudioPayload(int soundIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UndertaleSound? sound = GetAt(_data.Sounds, soundIndex) as UndertaleSound
            ?? throw new ArgumentOutOfRangeException(nameof(soundIndex));
        string name = sound.Name?.Content ?? $"sound_{soundIndex}";
        string groupName = sound.AudioGroup?.Name?.Content ?? $"audiogroup{sound.GroupID}";

        byte[]? data = null;
        string source = "Unavailable";

        // GameMaker's streamed/external case has neither the embedded nor compressed flag.
        // In that case AudioFile may still contain an unrelated placeholder reference, so
        // resolve the file on disk first rather than trusting AudioFile.
        bool streamedExternal =
            !sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsEmbedded) &&
            !sound.Flags.HasFlag(UndertaleSound.AudioEntryFlags.IsCompressed);

        if (streamedExternal)
        {
            string? externalPath = ResolveExternalSoundPath(sound);
            if (!string.IsNullOrWhiteSpace(externalPath) && File.Exists(externalPath))
            {
                data = File.ReadAllBytes(externalPath);
                source = "External file";
            }
        }
        else if (sound.GroupID > _data.GetBuiltinSoundGroupID())
        {
            // Non-default audio groups live in their own GameMaker data files.
            UndertaleData? groupData = GetAudioGroupData(sound, cancellationToken);
            if (groupData?.EmbeddedAudio is not null &&
                sound.AudioID >= 0 && sound.AudioID < groupData.EmbeddedAudio.Count)
            {
                data = groupData.EmbeddedAudio[sound.AudioID]?.Data;
                source = "Audio group file";
            }
            else
            {
                source = "Audio group file (missing entry)";
            }
        }
        else if (sound.AudioFile?.Data is { Length: > 0 } embedded)
        {
            data = embedded;
            source = "Main data archive";
        }

        // Some unusual builds have inconsistent flags. A final external-file fallback is
        // harmless and makes the viewer more tolerant without substituting unrelated bytes.
        if ((data is null || data.Length == 0) && !streamedExternal)
        {
            string? externalPath = ResolveExternalSoundPath(sound);
            if (!string.IsNullOrWhiteSpace(externalPath) && File.Exists(externalPath))
            {
                data = File.ReadAllBytes(externalPath);
                source = "External file (fallback)";
            }
        }

        if (data is null || data.Length == 0)
            throw new FileNotFoundException($"Audio data for {name} could not be located.");

        (string format, string extension) = DetectAudioFormat(data);
        return new AudioPayload(
            data,
            format,
            extension,
            source,
            groupName,
            sound.GroupID,
            sound.AudioID,
            OutputPathHelper.SafeFileName(name) + extension);
    }

    private UndertaleData? GetAudioGroupData(UndertaleSound sound, CancellationToken cancellationToken)
    {
        int groupId = sound.GroupID;
        lock (_audioGroupLock)
        {
            if (_audioGroupData.TryGetValue(groupId, out UndertaleData? cached))
                return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? path = ResolveAudioGroupPath(sound.AudioGroup, groupId);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            lock (_audioGroupLock)
                _audioGroupData[groupId] = null;
            return null;
        }

        UndertaleData? loaded;
        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            loaded = UndertaleIO.Read(stream);
        lock (_audioGroupLock)
            _audioGroupData[groupId] = loaded;
        return loaded;
    }

    private string? ResolveAudioGroupPath(UndertaleAudioGroup? group, int groupId)
    {
        string? fileName = group?.Path?.Content;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"audiogroup{groupId}.dat";
        string baseDirectory = Path.GetDirectoryName(_resolvedInput.OriginalPath)
                               ?? Path.GetDirectoryName(_resolvedInput.DataPath)
                               ?? Environment.CurrentDirectory;
        return ResolvePathInsideGameDirectory(baseDirectory, fileName);
    }

    private string? ResolveExternalSoundPath(UndertaleSound? sound)
    {
        string? fileName = sound?.File?.Content;
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        if (!Path.HasExtension(fileName))
            fileName += ".ogg";
        string baseDirectory = Path.GetDirectoryName(_resolvedInput.OriginalPath)
                               ?? Path.GetDirectoryName(_resolvedInput.DataPath)
                               ?? Environment.CurrentDirectory;
        return ResolvePathInsideGameDirectory(baseDirectory, fileName);
    }

    private static string? ResolvePathInsideGameDirectory(string baseDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return null;

        string fullBase = Path.GetFullPath(baseDirectory);
        string fullPath = Path.GetFullPath(Path.Combine(fullBase, relativePath));
        string relative = Path.GetRelativePath(fullBase, fullPath);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return null;
        }
        return fullPath;
    }

    private (byte[]? Image, int Rendered, int Skipped) RenderRoomToPng(
        UndertaleRoom room,
        CancellationToken cancellationToken)
    {
        const int maximumWidth = 1600;
        const int maximumHeight = 1000;
        if (room.Width == 0 || room.Height == 0)
            return (null, 0, 0);

        // UMT initializes parent links before its room renderer uses calculated background
        // offsets and stretch values. SplitGM must do the same for data files loaded outside
        // the UMT editor, otherwise GMS2 background layers can resolve to empty/zero geometry.
        room.SetupRoom(calculateGridWidth: false, calculateGridHeight: false);

        double scale = Math.Min(1.0, Math.Min(maximumWidth / (double)room.Width, maximumHeight / (double)room.Height));
        int canvasWidth = Math.Max(1, (int)Math.Round(room.Width * scale));
        int canvasHeight = Math.Max(1, (int)Math.Round(room.Height * scale));
        MagickColor backgroundColor = ToMagickColor(room.BackgroundColor, room.DrawBackgroundColor ? (byte)255 : (byte)0);
        using MagickImage canvas = new(backgroundColor, (uint)canvasWidth, (uint)canvasHeight);
        int rendered = 0;
        int skipped = 0;

        void RenderInstance(UndertaleRoom.GameObject instance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                UndertaleSprite? sprite = instance.ObjectDefinition?.Sprite;
                int frameCount = sprite?.Textures?.Count ?? 0;
                if (sprite is null || sprite.SSpriteType != UndertaleSprite.SpriteType.Normal || frameCount == 0)
                {
                    skipped++;
                    return;
                }
                int frame = Math.Clamp(instance.WrappedImageIndex, 0, frameCount - 1);
                UndertaleTexturePageItem? item = sprite.Textures?[frame]?.Texture;
                if (item is null)
                {
                    skipped++;
                    return;
                }
                using IMagickImage<byte> image = _textureWorker.GetTextureFor(item, sprite.Name?.Content ?? "room instance", includePadding: true);
                int width = Math.Max(1, (int)Math.Round(image.Width * Math.Abs(instance.ScaleX) * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * Math.Abs(instance.ScaleY) * scale));
                image.InterpolativeResize((uint)width, (uint)height, PixelInterpolateMethod.Nearest);
                if (Math.Abs(instance.Rotation) > 0.001f)
                {
                    image.BackgroundColor = MagickColors.Transparent;
                    image.Rotate(-instance.Rotation);
                }
                int x = (int)Math.Round(instance.XOffset * scale);
                int y = (int)Math.Round(instance.YOffset * scale);
                canvas.Composite(image, x, y, CompositeOperator.Over);
                rendered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skipped++;
            }
        }

        void RenderSpriteAsset(UndertaleRoom.SpriteInstance instance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                UndertaleSprite? sprite = instance.Sprite;
                int frameCount = sprite?.Textures?.Count ?? 0;
                if (sprite is null || sprite.SSpriteType != UndertaleSprite.SpriteType.Normal || frameCount == 0)
                {
                    skipped++;
                    return;
                }

                int frame = Math.Clamp(instance.WrappedFrameIndex, 0, frameCount - 1);
                UndertaleTexturePageItem? item = sprite.Textures?[frame]?.Texture;
                if (item is null)
                {
                    skipped++;
                    return;
                }

                using IMagickImage<byte> image = _textureWorker.GetTextureFor(
                    item,
                    sprite.Name?.Content ?? instance.Name?.Content ?? "room sprite asset",
                    includePadding: true);
                int width = Math.Max(1, (int)Math.Round(image.Width * Math.Abs(instance.ScaleX) * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * Math.Abs(instance.ScaleY) * scale));
                image.InterpolativeResize((uint)width, (uint)height, PixelInterpolateMethod.Nearest);
                if (Math.Abs(instance.Rotation) > 0.001f)
                {
                    image.BackgroundColor = MagickColors.Transparent;
                    image.Rotate(-instance.Rotation);
                }

                int x = (int)Math.Round(instance.XOffset * scale);
                int y = (int)Math.Round(instance.YOffset * scale);
                canvas.Composite(image, x, y, CompositeOperator.Over);
                rendered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skipped++;
            }
        }

        void CompositeBackgroundImage(
            IMagickImage<byte> image,
            bool stretch,
            bool tiledHorizontally,
            bool tiledVertically,
            double sourceX,
            double sourceY)
        {
            int width = stretch
                ? canvasWidth
                : Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = stretch
                ? canvasHeight
                : Math.Max(1, (int)Math.Round(image.Height * scale));
            if (image.Width != width || image.Height != height)
                image.InterpolativeResize((uint)width, (uint)height, PixelInterpolateMethod.Nearest);

            int startX = stretch ? 0 : (int)Math.Round(sourceX * scale);
            int startY = stretch ? 0 : (int)Math.Round(sourceY * scale);
            if (tiledHorizontally)
            {
                while (startX > 0)
                    startX -= width;
                while (startX + width <= 0)
                    startX += width;
            }
            if (tiledVertically)
            {
                while (startY > 0)
                    startY -= height;
                while (startY + height <= 0)
                    startY += height;
            }

            int endX = tiledHorizontally ? canvasWidth : startX + 1;
            int endY = tiledVertically ? canvasHeight : startY + 1;
            int compositeCount = 0;
            const int maximumBackgroundComposites = 16384;
            for (int y = startY; y < endY && compositeCount < maximumBackgroundComposites; y += height)
            {
                for (int x = startX; x < endX && compositeCount < maximumBackgroundComposites; x += width)
                {
                    canvas.Composite(image, x, y, CompositeOperator.Over);
                    compositeCount++;
                    if (!tiledHorizontally)
                        break;
                }
                if (!tiledVertically)
                    break;
            }
        }

        void RenderTileLayer(UndertaleRoom.Layer layer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                UndertaleRoom.Layer.LayerTilesData? tileLayer = layer.TilesData;
                UndertaleBackground? tileset = tileLayer?.Background;
                UndertaleTexturePageItem? texture = tileset?.Texture;
                uint tileWidth = tileset?.GMS2TileWidth ?? 0;
                uint tileHeight = tileset?.GMS2TileHeight ?? 0;
                uint columns = tileset?.GMS2TileColumns ?? 0;
                if (tileLayer?.TileData is null || tileset is null || texture is null ||
                    tileWidth == 0 || tileHeight == 0 || columns == 0)
                {
                    skipped++;
                    return;
                }

                using IMagickImage<byte> tilesetImage = _textureWorker.GetTextureFor(
                    texture,
                    tileset.Name?.Content ?? "room tileset",
                    includePadding: false);

                int outputBorderX = checked((int)tileset.GMS2OutputBorderX);
                int outputBorderY = checked((int)tileset.GMS2OutputBorderY);
                int separationX = checked((int)tileset.GMS2TileSeparationX);
                int separationY = checked((int)tileset.GMS2TileSeparationY);
                int sourceStepX = checked((int)tileWidth + (outputBorderX * 2) + separationX);
                int sourceStepY = checked((int)tileHeight + (outputBorderY * 2) + separationY);
                int destinationWidth = Math.Max(1, (int)Math.Round(tileWidth * scale));
                int destinationHeight = Math.Max(1, (int)Math.Round(tileHeight * scale));
                int maximumTileId = tileset.GMS2TileIds is { Count: > 0 }
                    ? checked((int)tileset.GMS2TileIds.Max(item => item.ID))
                    : tileset.GMS2TileCount == 0 ? 0 : checked((int)(tileset.GMS2TileCount - 1));

                const int maximumTilesToInspect = 500_000;
                int inspected = 0;
                for (int tileY = 0; tileY < tileLayer.TileData.Length && inspected < maximumTilesToInspect; tileY++)
                {
                    uint[]? row = tileLayer.TileData[tileY];
                    if (row is null)
                        continue;
                    for (int tileX = 0; tileX < row.Length && inspected < maximumTilesToInspect; tileX++)
                    {
                        inspected++;
                        if ((inspected & 0x7FF) == 0)
                            cancellationToken.ThrowIfCancellationRequested();

                        uint encodedId = row[tileX];
                        if (encodedId == 0 || encodedId == uint.MaxValue)
                            continue;
                        uint realId = encodedId & 0x0FFFFFFF;
                        if (realId > maximumTileId)
                        {
                            skipped++;
                            continue;
                        }

                        int destinationX = (int)Math.Round((layer.XOffset + (tileX * tileWidth)) * scale);
                        int destinationY = (int)Math.Round((layer.YOffset + (tileY * tileHeight)) * scale);
                        if (destinationX >= canvasWidth || destinationY >= canvasHeight ||
                            destinationX + destinationWidth <= 0 || destinationY + destinationHeight <= 0)
                            continue;

                        int sourceColumn = checked((int)(realId % columns));
                        int sourceRow = checked((int)(realId / columns));
                        int sourceX = checked(outputBorderX + (sourceColumn * sourceStepX));
                        int sourceY = checked(outputBorderY + (sourceRow * sourceStepY));
                        if (sourceX < 0 || sourceY < 0 ||
                            sourceX + tileWidth > tilesetImage.Width || sourceY + tileHeight > tilesetImage.Height)
                        {
                            skipped++;
                            continue;
                        }

                        using IMagickImage<byte> tile = tilesetImage.CloneArea(sourceX, sourceY, tileWidth, tileHeight);
                        switch (encodedId >> 28)
                        {
                            case 1:
                                tile.Flop();
                                break;
                            case 2:
                                tile.Flip();
                                break;
                            case 3:
                                tile.Flop();
                                tile.Flip();
                                break;
                            case 4:
                                tile.Rotate(90);
                                break;
                            case 5:
                                tile.Rotate(90);
                                tile.Flip();
                                break;
                            case 6:
                                tile.Rotate(90);
                                tile.Flop();
                                break;
                            case 7:
                                tile.Rotate(90);
                                tile.Flop();
                                tile.Flip();
                                break;
                        }
                        if (tile.Width != destinationWidth || tile.Height != destinationHeight)
                            tile.InterpolativeResize((uint)destinationWidth, (uint)destinationHeight, PixelInterpolateMethod.Nearest);
                        canvas.Composite(tile, destinationX, destinationY, CompositeOperator.Over);
                        rendered++;
                    }
                }

                if (inspected >= maximumTilesToInspect)
                    skipped++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skipped++;
            }
        }

        void RenderBackgroundLayer(UndertaleRoom.Layer layer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                UndertaleRoom.Layer.LayerBackgroundData? background = layer.BackgroundData;
                if (background is null || !background.Visible)
                    return;

                UndertaleSprite? sprite = background.Sprite;
                UndertaleTexturePageItem? item = sprite?.Textures?.FirstOrDefault()?.Texture;
                if (sprite is null || item is null)
                {
                    // GMS2 can use a sprite-less background layer as the room color layer.
                    byte alpha = (byte)(background.Color >> 24);
                    if (background.Color != 0 && alpha != 0)
                    {
                        using MagickImage colorLayer = new(ToMagickColor(background.Color, alpha), (uint)canvasWidth, (uint)canvasHeight);
                        canvas.Composite(colorLayer, 0, 0, CompositeOperator.Over);
                        rendered++;
                    }
                    return;
                }
                if (sprite.SSpriteType != UndertaleSprite.SpriteType.Normal)
                    return;

                // Backgrounds are texture-page regions, not ordinary padded sprite frames.
                // Using padding here can throw for valid GM background frames and caused the
                // layer to be silently skipped in v0.3.x.
                using IMagickImage<byte> image = _textureWorker.GetTextureFor(
                    item,
                    sprite.Name?.Content ?? "room background",
                    includePadding: false);

                double x = layer.XOffset + item.TargetX - sprite.OriginXWrapper;
                double y = layer.YOffset + item.TargetY - sprite.OriginYWrapper;
                CompositeBackgroundImage(
                    image,
                    background.Stretch,
                    background.TiledHorizontally,
                    background.TiledVertically,
                    x,
                    y);
                rendered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skipped++;
            }
        }

        if (room.Layers is not null && room.Layers.Count > 0)
        {
            foreach (UndertaleRoom.Layer layer in room.Layers
                         .Where(layer => layer is not null && layer.IsVisible)
                         .OrderByDescending(layer => layer.LayerDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (layer.LayerType == UndertaleRoom.LayerType.Background)
                    RenderBackgroundLayer(layer);
                else if (layer.LayerType == UndertaleRoom.LayerType.Tiles)
                    RenderTileLayer(layer);
                else if (layer.InstancesData?.Instances is not null)
                {
                    foreach (UndertaleRoom.GameObject instance in layer.InstancesData.Instances)
                        RenderInstance(instance);
                }
                else if (layer.AssetsData?.Sprites is not null)
                {
                    foreach (UndertaleRoom.SpriteInstance spriteAsset in layer.AssetsData.Sprites)
                        RenderSpriteAsset(spriteAsset);
                }
            }
        }
        else
        {
            if (room.Backgrounds is not null)
            {
                foreach (UndertaleRoom.Background background in room.Backgrounds)
                {
                    if (background is null || !background.Enabled || background.BackgroundDefinition?.Texture is null)
                        continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    using IMagickImage<byte> image = _textureWorker.GetTextureFor(
                        background.BackgroundDefinition.Texture,
                        background.BackgroundDefinition.Name?.Content ?? "room background",
                        includePadding: false);
                    CompositeBackgroundImage(
                        image,
                        background.Stretch,
                        background.TiledHorizontally,
                        background.TiledVertically,
                        background.XOffset,
                        background.YOffset);
                    rendered++;
                }
            }
            if (room.GameObjects is not null)
            {
                foreach (UndertaleRoom.GameObject instance in room.GameObjects)
                    RenderInstance(instance);
            }
        }

        return (MagickToPng(canvas), rendered, skipped);
    }

    private static int CountLayerItems(UndertaleRoom.Layer layer)
    {
        return layer.LayerType switch
        {
            UndertaleRoom.LayerType.Instances => layer.InstancesData?.Instances?.Count ?? 0,
            UndertaleRoom.LayerType.Tiles => ClampCount(
                (ulong)(layer.TilesData?.TilesX ?? 0) * (layer.TilesData?.TilesY ?? 0)),
            UndertaleRoom.LayerType.Background => layer.BackgroundData?.Sprite is null ? 0 : 1,
            UndertaleRoom.LayerType.Assets =>
                (layer.AssetsData?.LegacyTiles?.Count ?? 0) +
                (layer.AssetsData?.Sprites?.Count ?? 0) +
                (layer.AssetsData?.Sequences?.Count ?? 0) +
                (layer.AssetsData?.ParticleSystems?.Count ?? 0) +
                (layer.AssetsData?.TextItems?.Count ?? 0),
            _ => 0
        };
    }

    private static int ClampCount(ulong count)
    {
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static RoomTileInfo ToRoomTileInfo(UndertaleRoom.Tile tile, string layerName)
    {
        return new RoomTileInfo(
            tile.InstanceID,
            tile.ObjectDefinition?.Name?.Content ?? "(unknown tile asset)",
            layerName,
            tile.X,
            tile.Y,
            tile.SourceX,
            tile.SourceY,
            tile.Width,
            tile.Height,
            tile.ScaleX,
            tile.ScaleY,
            tile.TileDepth);
    }

    private byte[] TextureItemToPng(UndertaleTexturePageItem item, string name, bool includePadding)
    {
        using IMagickImage<byte> image = _textureWorker.GetTextureFor(item, name, includePadding);
        return MagickToPng(image);
    }

    private static byte[] MagickToPng(IMagickImage<byte> image)
    {
        using MemoryStream stream = new();
        image.Write(stream, MagickFormat.Png32);
        return stream.ToArray();
    }

    private static MagickColor ToMagickColor(uint gameMakerColor, byte alpha)
    {
        byte red = (byte)(gameMakerColor & 0xFF);
        byte green = (byte)((gameMakerColor >> 8) & 0xFF);
        byte blue = (byte)((gameMakerColor >> 16) & 0xFF);
        return MagickColor.FromRgba(red, green, blue, alpha);
    }

    private static (string Format, string Extension) DetectAudioFormat(byte[]? data)
    {
        if (data is { Length: >= 4 })
        {
            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F')
                return ("WAV", ".wav");
            if (data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S')
                return ("OGG Vorbis", ".ogg");
            if (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
                return ("MP3", ".mp3");
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
                return ("MP3", ".mp3");
        }
        return ("Unknown audio", ".bin");
    }

    private static string ExtractTextResource(object? resource)
    {
        if (resource is null)
            return string.Empty;
        if (resource is UndertaleString undertaleString)
            return undertaleString.Content ?? string.Empty;
        if (resource is string direct)
            return direct;

        StringBuilder output = new();
        foreach (PropertyInfo property in resource.GetType()
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.GetIndexParameters().Length == 0)
                     .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
        {
            object? value;
            try
            {
                value = property.GetValue(resource);
            }
            catch
            {
                continue;
            }
            string? text = value switch
            {
                string stringValue => stringValue,
                UndertaleString stringValue => stringValue.Content,
                byte[] bytes when property.Name.Contains("Source", StringComparison.OrdinalIgnoreCase) ||
                                  property.Name.Contains("Code", StringComparison.OrdinalIgnoreCase) =>
                    Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(text))
                continue;
            output.AppendLine($"// {property.Name}");
            output.AppendLine(text);
            output.AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }


    internal void TrimResourceImageCache()
    {
        ThrowIfDisposed();
        _resourcePreviewGate.Wait();
        try
        {
            ResetTextureWorkerUnsafe();
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }

    private void ResetTextureWorkerUnsafe()
    {
        _textureWorker.Dispose();
        _textureWorker = new TextureWorker();
        _resourceImageOperations = 0;
    }

    private void DisposeResourcePreviewState()
    {
        _resourcePreviewGate.Wait();
        try
        {
            _resourcePreviewCache.Clear();
            while (_resourcePreviewCacheOrder.TryDequeue(out _)) { }
            _textureWorker.Dispose();
            lock (_audioGroupLock)
            {
                foreach (UndertaleData? group in _audioGroupData.Values)
                    group?.Dispose();
                _audioGroupData.Clear();
            }
        }
        finally
        {
            _resourcePreviewGate.Release();
        }
    }
}

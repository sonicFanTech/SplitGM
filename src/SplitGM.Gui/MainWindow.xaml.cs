#nullable enable

using Microsoft.Win32;
using SplitGM.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SplitGM.Gui;

public partial class MainWindow : Window
{
    private const int ExplorerPageSize = 750;

    private readonly GameProjectLoader _loader = new();
    private readonly GameMakerDecompilerService _exporter = new();
    private readonly ObservableCollection<ExplorerNode> _treeNodes = [];
    private readonly AppLogWriter _appLog = new();
    private readonly AudioPreviewPlayer _audioPlayer = new();
    private readonly DispatcherTimer _spriteTimer;

    private GameProjectSession? _session;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CodeViewResult? _currentCodeView;
    private ResourcePreviewData? _currentResourcePreview;
    private ResourceKind? _currentResourceKind;
    private int _currentResourceIndex = -1;
    private int _currentSpriteFrame;
    private string? _lastOutputDirectory;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        InitializeV04();
        InitializeV05();
        ResourceTree.ItemsSource = _treeNodes;
        CodeDocumentRenderer.Configure(GmlViewer, CodeDocumentMode.Gml);
        CodeDocumentRenderer.Configure(AssemblyViewer, CodeDocumentMode.Assembly);
        CodeDocumentRenderer.Configure(ResourceTextViewer, CodeDocumentMode.Plain);

        if (CodeDocumentRenderer.HighlightingLoadError is string highlightingError)
            AppendLog(LogMessage.Warning($"Syntax highlighting was disabled: {highlightingError}"));

        CodeDocumentRenderer.SetText(
            GmlViewer,
            "// Open a GameMaker VM game to begin.\n// SplitGM v0.5.0 adds stable .splitgmproj output and experimental reconstructed .yyp project generation.",
            CodeDocumentMode.Gml,
            includeLineNumbers: true);
        CodeDocumentRenderer.SetText(
            AssemblyViewer,
            "; VM assembly will appear here for the selected code entry.",
            CodeDocumentMode.Assembly,
            includeLineNumbers: true);
        CodeDocumentRenderer.SetText(
            ResourceTextViewer,
            string.Empty,
            CodeDocumentMode.Plain,
            includeLineNumbers: false);

        DetailsTextBox.Text = BuildWelcomeText();
        PreviewPropertiesTextBox.Text = BuildWelcomeText();
        MainTabControl.SelectedItem = DetailsTab;
        AppendLog(LogMessage.Info($"{SplitGmProduct.Name} {SplitGmProduct.DisplayVersion} started."));
        AppendLog(LogMessage.Info($"Session log: {_appLog.LogPath}"));

        _spriteTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _spriteTimer.Tick += SpriteTimer_Tick;

        Loaded += async (_, _) =>
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
                await LoadGameAsync(args[1]);
        };
    }

    private async void OpenGameButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Open a GameMaker game",
            Filter = "GameMaker files|data.win;*.win;*.unx;*.ios;*.droid;*.android;*.game;*.exe|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            await LoadGameAsync(dialog.FileName);
    }

    private void CloseGameButton_Click(object sender, RoutedEventArgs e)
    {
        CloseCurrentGame();
    }

    private async Task LoadGameAsync(string path)
    {
        if (_isBusy)
            return;

        CloseCurrentGame(clearDisplay: false);
        SetBusy(true, "Loading game...");
        ProgressBar.Value = 0;
        CurrentItemTitle.Text = "Loading GameMaker data...";
        CurrentItemSubtitle.Text = Path.GetFileName(path);
        DetailsTextBox.Text = string.Empty;
        MainTabControl.SelectedItem = ActivityTab;
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Loading GameMaker data",
            "SplitGM is resolving the input, reading GameMaker chunks, detecting the runtime, and building resource indexes.",
            Path.GetFullPath(path));

        Progress<DecompileProgress> progress = CreateDetailedProgress();
        Progress<LogMessage> log = CreateDetailedLog();

        try
        {
            AppendLog(LogMessage.Info($"Opening: {Path.GetFullPath(path)}"));
            _session = await _loader.LoadAsync(path, progress, log, _operationCancellation.Token);

            PopulateRootTree();
            ShowGeneralInformation();
            UpdateCompatibilityBadge();
            CloseGameButton.IsEnabled = true;
            ExportButton.IsEnabled = true;
            CopyDiagnosticButton.IsEnabled = true;
            SearchButton.IsEnabled = !_session.Info.IsYyc;
            StatusTextBlock.Text = "Game loaded.";
            StatusDetailTextBlock.Text =
                $"{_session.ResourceCounts.RootCodeEntries:N0} code • " +
                $"{_session.ResourceCounts.Objects:N0} objects • " +
                $"{_session.ResourceCounts.Rooms:N0} rooms • " +
                $"{_session.ResourceCounts.Sprites:N0} sprites";
            ProgressBar.Value = 100;
            _settings.LastOpenedGame = Path.GetFullPath(path);
            _settings.Save();
            OpenLastGameMenuItem.IsEnabled = true;
            AppendLog(LogMessage.Success($"Loaded {_session.Info.DisplayName}."));
            CompleteOperationWindow(true, $"Loaded {_session.Info.DisplayName} with {_session.ResourceCounts.RootCodeEntries:N0} code entries.");
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogMessage.Warning("Game loading was cancelled."));
            StatusTextBlock.Text = "Load cancelled.";
            ResetWelcomeDisplay();
            CompleteOperationWindow(false, "Game loading cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            StatusTextBlock.Text = "Failed to load the game.";
            ResetWelcomeDisplay();
            CompleteOperationWindow(false, "Game loading failed. See the operation log for details.");
            MessageBox.Show(this, exception.Message, "SplitGM load error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void PopulateRootTree()
    {
        if (_session is null)
            return;

        _treeNodes.Clear();
        _treeNodes.Add(new ExplorerNode
        {
            DisplayName = "Game Information",
            Icon = "i",
            Kind = ExplorerNodeKind.Information
        });

        ExplorerNode codeRoot = new()
        {
            DisplayName = "Code",
            Icon = "<> ",
            Kind = ExplorerNodeKind.Group,
            Count = _session.CodeEntries.Count,
            IsLoaded = true
        };

        foreach (CodeCategory category in Enum.GetValues<CodeCategory>())
        {
            int count = _session.CodeEntries.Count(entry => entry.Category == category);
            if (count == 0)
                continue;

            ExplorerNode categoryNode = new()
            {
                DisplayName = FriendlyCategoryName(category),
                Icon = CategoryIcon(category),
                Kind = ExplorerNodeKind.Group,
                Count = count,
                CodeCategory = category,
                IsLazy = true
            };
            categoryNode.AddLoadingPlaceholder();
            codeRoot.Children.Add(categoryNode);
        }
        _treeNodes.Add(codeRoot);

        AddResourceRoot(ResourceKind.Objects, _session.ResourceCounts.Objects, "◆");
        AddResourceRoot(ResourceKind.Rooms, _session.ResourceCounts.Rooms, "▦");
        AddResourceRoot(ResourceKind.Sprites, _session.ResourceCounts.Sprites, "▧");
        AddResourceRoot(ResourceKind.Sounds, _session.ResourceCounts.Sounds, "♪");
        AddResourceRoot(ResourceKind.EmbeddedAudio, _session.ResourceCounts.EmbeddedAudio, "♫");
        AddResourceRoot(ResourceKind.AudioGroups, _session.ResourceCounts.AudioGroups, "♬");
        AddResourceRoot(ResourceKind.Fonts, _session.ResourceCounts.Fonts, "A");
        AddResourceRoot(ResourceKind.Shaders, _session.ResourceCounts.Shaders, "◇");
        AddResourceRoot(ResourceKind.Backgrounds, _session.ResourceCounts.Backgrounds, "▤");
        AddResourceRoot(ResourceKind.Paths, _session.ResourceCounts.Paths, "⌁");
        AddResourceRoot(ResourceKind.Timelines, _session.ResourceCounts.Timelines, "↦");
        AddResourceRoot(ResourceKind.Extensions, _session.ResourceCounts.Extensions, "+");
        AddResourceRoot(ResourceKind.Sequences, _session.ResourceCounts.Sequences, "▶");
        AddResourceRoot(ResourceKind.AnimationCurves, _session.ResourceCounts.AnimationCurves, "∿");
        AddResourceRoot(ResourceKind.ParticleSystems, _session.ResourceCounts.ParticleSystems, "✦");
        AddResourceRoot(ResourceKind.ParticleSystemEmitters, _session.ResourceCounts.ParticleSystemEmitters, "✧");
        AddResourceRoot(ResourceKind.TextureGroups, _session.ResourceCounts.TextureGroups, "▥");
        AddResourceRoot(ResourceKind.TexturePageItems, _session.ResourceCounts.TexturePageItems, "▨");
        AddResourceRoot(ResourceKind.TexturePages, _session.ResourceCounts.TexturePages, "▩");
        AddResourceRoot(ResourceKind.EmbeddedImages, _session.ResourceCounts.EmbeddedImages, "▧");
        AddResourceRoot(ResourceKind.FilterEffects, _session.ResourceCounts.FilterEffects, "◈");
        AddResourceRoot(ResourceKind.Strings, _session.ResourceCounts.Strings, "\"");
        AddResourceRoot(ResourceKind.Functions, _session.ResourceCounts.Functions, "ƒ");
        AddResourceRoot(ResourceKind.Variables, _session.ResourceCounts.Variables, "x");

        ResourceCounts counts = _session.ResourceCounts;
        int totalResources = counts.Objects + counts.Rooms + counts.Sprites + counts.Sounds +
            counts.Fonts + counts.Shaders + counts.Backgrounds + counts.Paths + counts.Timelines +
            counts.Extensions + counts.AudioGroups + counts.Sequences + counts.AnimationCurves +
            counts.ParticleSystems + counts.ParticleSystemEmitters + counts.TextureGroups +
            counts.TexturePageItems + counts.TexturePages + counts.EmbeddedImages +
            counts.EmbeddedAudio + counts.FilterEffects + counts.Strings + counts.Functions + counts.Variables;
        ResourceTreeCountText.Text = $"{totalResources:N0} resources";
    }

    private void AddResourceRoot(ResourceKind kind, int count, string icon)
    {
        if (count <= 0)
            return;

        ExplorerNode node = new()
        {
            DisplayName = FriendlyResourceName(kind),
            Icon = icon,
            Kind = ExplorerNodeKind.Group,
            Count = count,
            ResourceKind = kind,
            IsLazy = true
        };
        node.AddLoadingPlaceholder();
        _treeNodes.Add(node);
    }

    private async void ResourceTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem treeItem ||
            treeItem.DataContext is not ExplorerNode node ||
            !node.IsLazy || node.IsLoaded || _session is null)
        {
            return;
        }

        node.IsLoaded = true;
        node.Children.Clear();
        StatusTextBlock.Text = $"Loading {node.DisplayName}...";

        try
        {
            if (node.CodeCategory is CodeCategory category)
            {
                IReadOnlyList<CodeEntryInfo> entries = _session.GetCodeEntries(category);
                if (!node.IsRangePage && entries.Count > ExplorerPageSize)
                {
                    AddCodeRangePages(node, entries.Count, category);
                }
                else
                {
                    int start = node.IsRangePage ? node.RangeStart : 0;
                    int end = node.IsRangePage
                        ? Math.Min(entries.Count, start + node.RangeCount)
                        : entries.Count;
                    for (int index = start; index < end; index++)
                    {
                        CodeEntryInfo entry = entries[index];
                        node.Children.Add(new ExplorerNode
                        {
                            DisplayName = entry.Name,
                            Icon = "•",
                            Kind = ExplorerNodeKind.CodeEntry,
                            Index = entry.Index,
                            CodeCategory = entry.Category
                        });
                        if ((index - start + 1) % 250 == 0)
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    }
                }
            }
            else if (node.ResourceKind is ResourceKind kind)
            {
                IReadOnlyList<ResourceEntryInfo> entries = _session.GetResourceEntries(kind);
                if (!node.IsRangePage && entries.Count > ExplorerPageSize)
                {
                    AddResourceRangePages(node, entries.Count, kind);
                }
                else
                {
                    int start = node.IsRangePage ? node.RangeStart : 0;
                    int end = node.IsRangePage
                        ? Math.Min(entries.Count, start + node.RangeCount)
                        : entries.Count;
                    for (int index = start; index < end; index++)
                    {
                        ResourceEntryInfo entry = entries[index];
                        node.Children.Add(new ExplorerNode
                        {
                            DisplayName = entry.Name,
                            Icon = "•",
                            Kind = ExplorerNodeKind.ResourceEntry,
                            Index = entry.Index,
                            ResourceKind = entry.Kind
                        });
                        if ((index - start + 1) % 250 == 0)
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                    }
                }
            }
            StatusTextBlock.Text = "Ready.";
        }
        catch (Exception exception)
        {
            node.IsLoaded = false;
            AppendLog(LogMessage.Error(exception.ToString()));
            node.Children.Add(new ExplorerNode
            {
                DisplayName = "Failed to load this group",
                Icon = "!",
                Kind = ExplorerNodeKind.Placeholder
            });
            StatusTextBlock.Text = "Resource group failed to load.";
        }
    }

    private static void AddCodeRangePages(ExplorerNode parent, int totalCount, CodeCategory category)
    {
        for (int start = 0; start < totalCount; start += ExplorerPageSize)
        {
            int count = Math.Min(ExplorerPageSize, totalCount - start);
            ExplorerNode page = new()
            {
                DisplayName = $"Items {start + 1:N0}–{start + count:N0}",
                Icon = "▹",
                Kind = ExplorerNodeKind.Group,
                Count = count,
                CodeCategory = category,
                RangeStart = start,
                RangeCount = count,
                IsLazy = true
            };
            page.AddLoadingPlaceholder();
            parent.Children.Add(page);
        }
    }

    private static void AddResourceRangePages(ExplorerNode parent, int totalCount, ResourceKind kind)
    {
        for (int start = 0; start < totalCount; start += ExplorerPageSize)
        {
            int count = Math.Min(ExplorerPageSize, totalCount - start);
            ExplorerNode page = new()
            {
                DisplayName = $"Items {start + 1:N0}–{start + count:N0}",
                Icon = "▹",
                Kind = ExplorerNodeKind.Group,
                Count = count,
                ResourceKind = kind,
                RangeStart = start,
                RangeCount = count,
                IsLazy = true
            };
            page.AddLoadingPlaceholder();
            parent.Children.Add(page);
        }
    }

    private async void ResourceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_session is null || e.NewValue is not ExplorerNode node)
            return;

        _selectedExplorerNode = node;
        AnalyzeSelectedMenuItem.IsEnabled = node.Kind is ExplorerNodeKind.CodeEntry or ExplorerNodeKind.ResourceEntry;

        switch (node.Kind)
        {
            case ExplorerNodeKind.Information:
                ShowGeneralInformation();
                break;
            case ExplorerNodeKind.CodeEntry:
                await DisplayCodeEntryAsync(node.Index);
                break;
            case ExplorerNodeKind.ResourceEntry when node.ResourceKind is ResourceKind kind:
                await DisplayResourceAsync(kind, node.Index, node.DisplayName);
                break;
            case ExplorerNodeKind.Group:
                ShowGroupDetails(node);
                break;
        }
    }

    private async Task DisplayCodeEntryAsync(int codeIndex, string? preferredSource = null)
    {
        if (_session is null)
            return;

        CancelCurrentPreview();
        StopMediaPreview();
        _currentCodeView = null;
        _currentResourceKind = null;
        _currentResourceIndex = -1;
        _currentResourcePreview = null;
        AnalyzeSelectedMenuItem.IsEnabled = true;
        ExportSelectedButton.IsEnabled = false;

        _previewCancellation = new CancellationTokenSource();
        CancellationToken token = _previewCancellation.Token;
        CodeEntryInfo entry = _session.CodeEntries[codeIndex];
        CurrentItemTitle.Text = entry.Name;
        CurrentItemSubtitle.Text = "Decompiling in a background worker...";
        StatusTextBlock.Text = $"Opening {entry.Name}...";
        CodeDocumentRenderer.SetLoadingText(GmlViewer, "// Decompiling, please wait...", CodeDocumentMode.Gml);
        CodeDocumentRenderer.SetLoadingText(AssemblyViewer, "; Preparing VM assembly...", CodeDocumentMode.Assembly);
        MainTabControl.SelectedItem = preferredSource?.Equals("Assembly", StringComparison.OrdinalIgnoreCase) == true
            ? AssemblyTab
            : GmlTab;

        try
        {
            CodeViewResult view = await _session.GetCodeViewAsync(codeIndex, token);
            token.ThrowIfCancellationRequested();
            _currentCodeView = view;
            CurrentItemTitle.Text = view.Entry.Name;
            CurrentItemSubtitle.Text =
                $"{FriendlyCategoryName(view.Entry.Category)} • {view.Entry.InstructionCount:N0} instructions • " +
                $"GML {(view.GmlSucceeded ? "OK" : "FAILED / assembly fallback")}";

            // Let WPF paint the loading state before replacing a potentially multi-megabyte document.
            await Dispatcher.Yield(DispatcherPriority.Background);
            RenderCurrentCodeView();
            DetailsTextBox.Text = view.Details;
            StatusTextBlock.Text = view.GmlSucceeded
                ? "Code entry opened."
                : "GML failed for this entry; VM assembly fallback is available.";
        }
        catch (OperationCanceledException)
        {
            // A newer selection superseded this preview.
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            CurrentItemSubtitle.Text = "Unable to open this code entry.";
            DetailsTextBox.Text = exception.ToString();
            MainTabControl.SelectedItem = DetailsTab;
            StatusTextBlock.Text = "Code preview failed.";
        }
    }

    private async Task DisplayResourceAsync(ResourceKind kind, int index, string name, int? frameOverride = null)
    {
        if (_session is null)
            return;

        CancelCurrentPreview();
        _audioPlayer.Stop();
        _currentCodeView = null;
        AnalyzeSelectedMenuItem.IsEnabled = true;
        _currentResourceKind = kind;
        _currentResourceIndex = index;
        if (frameOverride is int frame)
            _currentSpriteFrame = frame;
        else
            _currentSpriteFrame = 0;

        _previewCancellation = new CancellationTokenSource();
        CancellationToken token = _previewCancellation.Token;
        CurrentItemTitle.Text = name;
        CurrentItemSubtitle.Text = $"Loading {FriendlyResourceName(kind)} preview...";
        PreviewStatusText.Text = "Loading preview in a background worker...";
        PreviewImage.Source = null;
        PreviewEmptyText.Visibility = Visibility.Visible;
        ResourceTextViewer.Visibility = Visibility.Collapsed;
        PreviewImageScrollViewer.Visibility = Visibility.Visible;
        PreviewPropertiesTextBox.Text = "Loading...";
        ExportSelectedButton.IsEnabled = false;
        ResetSpecializedPreviewPanels();
        MainTabControl.SelectedItem = PreviewTab;
        StatusTextBlock.Text = $"Rendering {name}...";

        try
        {
            ResourcePreviewData preview = await _session.GetResourcePreviewAsync(
                kind,
                index,
                _currentSpriteFrame,
                token);
            token.ThrowIfCancellationRequested();
            if (_currentResourceKind != kind || _currentResourceIndex != index)
                return;

            _currentResourcePreview = preview;
            _currentSpriteFrame = preview.Sprite?.FrameIndex ?? _currentSpriteFrame;
            ApplyResourcePreview(preview);
            ExportSelectedButton.IsEnabled = true;
            StatusTextBlock.Text = "Resource preview ready.";
        }
        catch (OperationCanceledException)
        {
            // New selection or closing the game.
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            CurrentItemSubtitle.Text = "The specialized preview failed; raw properties remain available.";
            PreviewPropertiesTextBox.Text = exception.ToString();
            DetailsTextBox.Text = _session.GetResourceDetails(kind, index);
            MainTabControl.SelectedItem = DetailsTab;
            StatusTextBlock.Text = "Resource preview failed.";
            ExportSelectedButton.IsEnabled = true;
        }
    }

    private void ApplyResourcePreview(ResourcePreviewData preview)
    {
        CurrentItemTitle.Text = preview.Name;
        CurrentItemSubtitle.Text = preview.Subtitle ?? $"{FriendlyResourceName(preview.ResourceKind)} resource #{preview.ResourceIndex:N0}";
        DetailsTextBox.Text = preview.Details;
        PreviewPropertiesTextBox.Text = BuildPreviewProperties(preview);
        PreviewStatusText.Text = preview.Subtitle ?? "Preview ready.";

        if (!string.IsNullOrWhiteSpace(preview.Text) && preview.ImagePng is null)
        {
            PreviewImageScrollViewer.Visibility = Visibility.Collapsed;
            ResourceTextViewer.Visibility = Visibility.Visible;
            CodeDocumentRenderer.SetText(
                ResourceTextViewer,
                preview.Text,
                preview.ResourceKind == ResourceKind.Shaders ? CodeDocumentMode.Gml : CodeDocumentMode.Plain,
                LineNumbersCheckBox.IsChecked == true);
        }
        else
        {
            ResourceTextViewer.Visibility = Visibility.Collapsed;
            PreviewImageScrollViewer.Visibility = Visibility.Visible;
            PreviewImage.Source = BitmapSourceFactory.FromBytes(preview.ImagePng);
            PreviewEmptyText.Visibility = preview.ImagePng is null ? Visibility.Visible : Visibility.Collapsed;
        }

        if (preview.Sprite is not null)
        {
            SpriteControlsPanel.Visibility = Visibility.Visible;
            SpriteFrameText.Text = preview.Sprite.FrameCount == 0
                ? "No raster frames"
                : $"Frame {preview.Sprite.FrameIndex + 1:N0} / {preview.Sprite.FrameCount:N0}";
        }

        if (preview.PreviewKind == ResourcePreviewKind.Audio)
        {
            AudioControlsPanel.Visibility = Visibility.Visible;
            PlayAudioButton.IsEnabled = preview.Audio?.DataAvailable == true &&
                                        preview.ResourceKind is ResourceKind.Sounds or ResourceKind.EmbeddedAudio;
            ExportAudioGroupButton.Visibility = preview.ResourceKind is ResourceKind.Sounds or ResourceKind.AudioGroups
                ? Visibility.Visible
                : Visibility.Collapsed;
            ExportAudioGroupMenuItem.IsEnabled = preview.ResourceKind is ResourceKind.Sounds or ResourceKind.AudioGroups;
        }

        if (preview.Object is not null)
        {
            ObjectEventsTab.Visibility = Visibility.Visible;
            ObjectEventsGrid.ItemsSource = preview.Object.Events;
        }

        if (preview.Room is not null)
        {
            RoomLayersTab.Visibility = Visibility.Visible;
            RoomInstancesTab.Visibility = Visibility.Visible;
            RoomTilesTab.Visibility = Visibility.Visible;
            RoomLayersGrid.ItemsSource = preview.Room.Layers;
            RoomInstancesGrid.ItemsSource = preview.Room.Instances;
            RoomTilesGrid.ItemsSource = preview.Room.Tiles;
        }
    }

    private static string BuildPreviewProperties(ResourcePreviewData preview)
    {
        StringBuilder output = new();
        output.AppendLine(preview.Name);
        output.AppendLine(new string('=', Math.Min(preview.Name.Length, 72)));
        output.AppendLine($"Resource type: {preview.ResourceKind}");
        output.AppendLine($"Resource index: {preview.ResourceIndex}");
        output.AppendLine($"Preview type: {preview.PreviewKind}");
        if (!string.IsNullOrWhiteSpace(preview.Subtitle))
            output.AppendLine($"Summary: {preview.Subtitle}");

        if (preview.Sprite is not null)
        {
            output.AppendLine();
            output.AppendLine("Sprite preview");
            output.AppendLine("--------------");
            output.AppendLine($"Dimensions: {preview.Sprite.Width}×{preview.Sprite.Height}");
            output.AppendLine($"Origin: {preview.Sprite.OriginX}, {preview.Sprite.OriginY}");
            output.AppendLine($"Frames: {preview.Sprite.FrameCount:N0}");
            output.AppendLine($"Sprite type: {preview.Sprite.SpriteType}");
        }

        if (preview.Object is not null)
        {
            output.AppendLine();
            output.AppendLine("Object preview");
            output.AppendLine("--------------");
            output.AppendLine($"Sprite: {preview.Object.SpriteName ?? "(none)"}");
            output.AppendLine($"Parent: {preview.Object.ParentObjectName ?? "(none)"}");
            output.AppendLine($"Collision mask: {preview.Object.CollisionMaskName ?? "(default)"}");
            output.AppendLine($"Visible: {preview.Object.Visible}");
            output.AppendLine($"Solid: {preview.Object.Solid}");
            output.AppendLine($"Persistent: {preview.Object.Persistent}");
            output.AppendLine($"Depth: {preview.Object.Depth}");
            output.AppendLine($"Events: {preview.Object.Events.Count:N0}");
        }

        if (preview.Audio is not null)
        {
            output.AppendLine();
            output.AppendLine("Audio preview");
            output.AppendLine("-------------");
            output.AppendLine($"Format: {preview.Audio.Format}");
            output.AppendLine($"Source: {preview.Audio.Source}");
            output.AppendLine($"Audio group: {preview.Audio.AudioGroup}");
            output.AppendLine($"Group ID: {preview.Audio.GroupId}");
            output.AppendLine($"Audio ID: {preview.Audio.AudioId}");
            output.AppendLine($"Data available: {preview.Audio.DataAvailable}");
            output.AppendLine($"Data length: {FormatBytes(preview.Audio.DataLength)}");
            if (!string.IsNullOrWhiteSpace(preview.Audio.ExternalPath))
                output.AppendLine($"Path / status: {preview.Audio.ExternalPath}");
        }

        if (preview.Room is not null)
        {
            output.AppendLine();
            output.AppendLine("Room preview");
            output.AppendLine("------------");
            output.AppendLine($"Dimensions: {preview.Room.Width}×{preview.Room.Height}");
            output.AppendLine($"Speed: {preview.Room.Speed}");
            output.AppendLine($"Persistent: {preview.Room.Persistent}");
            output.AppendLine($"Layers: {preview.Room.Layers.Count:N0}");
            output.AppendLine($"Instances: {preview.Room.Instances.Count:N0}");
            output.AppendLine($"Legacy / asset tiles listed: {preview.Room.Tiles.Count:N0}");
            output.AppendLine($"Preview items rendered: {preview.Room.RenderedObjectCount:N0}");
            output.AppendLine($"Preview items skipped: {preview.Room.SkippedObjectCount:N0}");
        }

        output.AppendLine();
        output.AppendLine("Raw public properties");
        output.AppendLine("---------------------");
        output.Append(preview.Details);
        return output.ToString();
    }

    private void ShowGeneralInformation()
    {
        if (_session is null)
            return;

        CancelCurrentPreview();
        StopMediaPreview();
        _currentCodeView = null;
        _currentResourcePreview = null;
        _currentResourceKind = null;
        _currentResourceIndex = -1;
        ExportSelectedButton.IsEnabled = false;
        CurrentItemTitle.Text = _session.Info.DisplayName;
        CurrentItemSubtitle.Text =
            $"GameMaker {_session.Info.GameMakerVersion} • bytecode {_session.Info.BytecodeVersion} • {_session.Info.RuntimeType}";
        DetailsTextBox.Text = _session.GetGeneralInformationText();
        CodeDocumentRenderer.SetText(GmlViewer, "// Select a code entry to view reconstructed GML.", CodeDocumentMode.Gml,
            LineNumbersCheckBox.IsChecked == true);
        CodeDocumentRenderer.SetText(AssemblyViewer, "; Select a code entry to view VM assembly.", CodeDocumentMode.Assembly,
            LineNumbersCheckBox.IsChecked == true);
        MainTabControl.SelectedItem = DetailsTab;
        ApplyCodeViewOptions();
    }

    private void ShowGroupDetails(ExplorerNode node)
    {
        CancelCurrentPreview();
        StopMediaPreview();
        _currentCodeView = null;
        _currentResourcePreview = null;
        _currentResourceKind = null;
        _currentResourceIndex = -1;
        ExportSelectedButton.IsEnabled = false;
        CurrentItemTitle.Text = node.DisplayName;
        CurrentItemSubtitle.Text = $"{node.Count:N0} entries";
        DetailsTextBox.Text =
            $"{node.DisplayName}\r\n{new string('=', Math.Min(node.DisplayName.Length, 72))}\r\n\r\n" +
            $"Entries: {node.Count:N0}\r\n\r\nExpand this group in the resource tree to browse its contents.";
        MainTabControl.SelectedItem = DetailsTab;
    }

    private void RenderCurrentCodeView()
    {
        if (_currentCodeView is null)
            return;

        bool lineNumbers = LineNumbersCheckBox.IsChecked == true;
        CodeDocumentRenderer.SetText(GmlViewer, _currentCodeView.Gml, CodeDocumentMode.Gml, lineNumbers);
        CodeDocumentRenderer.SetText(AssemblyViewer, _currentCodeView.Assembly, CodeDocumentMode.Assembly, lineNumbers);
        ApplyCodeViewOptions();
    }

    private void CodeViewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ApplyCodeViewOptions();
    }

    private void ApplyCodeViewOptions()
    {
        bool wrap = WordWrapCheckBox.IsChecked == true;
        bool lineNumbers = LineNumbersCheckBox.IsChecked == true;

        // Word wrapping and line-number measurement can dominate WPF's UI thread for
        // multi-megabyte documents. Keep the requested settings for ordinary entries,
        // but automatically use the lightweight path for unusually large output.
        GmlViewer.WordWrap = wrap && GmlViewer.Document.TextLength <= 1_000_000;
        AssemblyViewer.WordWrap = wrap && AssemblyViewer.Document.TextLength <= 1_000_000;
        ResourceTextViewer.WordWrap = wrap && ResourceTextViewer.Document.TextLength <= 1_000_000;
        GmlViewer.ShowLineNumbers = lineNumbers && GmlViewer.Document.TextLength <= 2_000_000;
        AssemblyViewer.ShowLineNumbers = lineNumbers && AssemblyViewer.Document.TextLength <= 2_000_000;
        ResourceTextViewer.ShowLineNumbers = lineNumbers && ResourceTextViewer.Document.TextLength <= 2_000_000;
    }

    private async void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeSpriteFrameAsync(-1);
    }

    private async void NextFrameButton_Click(object sender, RoutedEventArgs e)
    {
        await ChangeSpriteFrameAsync(1);
    }

    private void SpritePlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_spriteTimer.IsEnabled)
        {
            _spriteTimer.Stop();
            SpritePlayButton.Content = "Play";
        }
        else if (_currentResourcePreview?.Sprite is { FrameCount: > 1 })
        {
            _spriteTimer.Start();
            SpritePlayButton.Content = "Pause";
        }
    }

    private async void SpriteTimer_Tick(object? sender, EventArgs e)
    {
        await ChangeSpriteFrameAsync(1);
    }

    private async Task ChangeSpriteFrameAsync(int delta)
    {
        if (_session is null || _currentResourceKind != ResourceKind.Sprites ||
            _currentResourcePreview?.Sprite is not { FrameCount: > 0 } sprite)
        {
            return;
        }

        bool resumePlayback = _spriteTimer.IsEnabled;
        int next = ((_currentSpriteFrame + delta) % sprite.FrameCount + sprite.FrameCount) % sprite.FrameCount;
        await DisplayResourceAsync(ResourceKind.Sprites, _currentResourceIndex, _currentResourcePreview.Name, next);
        if (resumePlayback && _currentResourcePreview?.Sprite is { FrameCount: > 1 })
        {
            _spriteTimer.Start();
            SpritePlayButton.Content = "Pause";
        }
    }

    private async void PlayAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentResourceKind is not ResourceKind kind || _currentResourceIndex < 0)
            return;
        if (kind is not (ResourceKind.Sounds or ResourceKind.EmbeddedAudio))
            return;

        try
        {
            StatusTextBlock.Text = "Loading audio data...";
            AudioPayload payload = await _session.GetAudioPayloadAsync(kind, _currentResourceIndex);
            _audioPlayer.Play(payload);
            StatusTextBlock.Text = $"Playing {payload.SuggestedFileName}.";
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            MessageBox.Show(this, exception.Message, "Audio preview error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Audio preview failed.";
        }
    }

    private void StopAudioButton_Click(object sender, RoutedEventArgs e)
    {
        _audioPlayer.Stop();
        StatusTextBlock.Text = "Audio stopped.";
    }

    private async void ExportSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentResourceKind is not ResourceKind kind || _currentResourceIndex < 0 || _isBusy)
            return;

        OpenFolderDialog dialog = new()
        {
            Title = $"Export {CurrentItemTitle.Text}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, "Exporting selected resource...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Export selected resource",
            $"Exporting {CurrentItemTitle.Text} and its recoverable metadata/assets.",
            dialog.FolderName);
        Progress<DecompileProgress> progress = CreateDetailedProgress();

        try
        {
            CancelCurrentPreview();
            await _session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            ResourceExportResult result = await _session.ExportSelectedResourceAsync(
                kind,
                _currentResourceIndex,
                dialog.FolderName,
                progress,
                _operationCancellation.Token);
            _lastOutputDirectory = result.OutputPath;
            OpenOutputButton.IsEnabled = true;
            StatusTextBlock.Text = $"Exported {result.ResourceName}.";
            StatusDetailTextBlock.Text = $"{result.FilesWritten:N0} files • {FormatBytes(result.BytesWritten)}";
            CompleteOperationWindow(true, $"Exported {result.FilesWritten:N0} file(s), {FormatBytes(result.BytesWritten)}.");
            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputPath))
                Process.Start(new ProcessStartInfo(result.OutputPath) { UseShellExecute = true });
            MessageBox.Show(this,
                $"Exported {result.ResourceName}.\n\nFiles written: {result.FilesWritten:N0}\nData written: {FormatBytes(result.BytesWritten)}",
                "SplitGM selected resource export",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Selected resource export cancelled.";
            CompleteOperationWindow(false, "Selected resource export cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            MessageBox.Show(this, exception.Message, "Resource export error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Selected resource export failed.";
            CompleteOperationWindow(false, "Selected resource export failed.");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void ExportAudioGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentResourcePreview is null || _isBusy)
            return;

        int groupIndex = _currentResourceKind == ResourceKind.AudioGroups
            ? _currentResourceIndex
            : _currentResourcePreview.Audio?.GroupId ?? -1;
        if (groupIndex < 0 || groupIndex >= _session.ResourceCounts.AudioGroups)
        {
            MessageBox.Show(this, "This sound does not resolve to an exportable audio-group resource.",
                "Audio group unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = "Export whole audio group",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, "Exporting audio group...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Export audio group",
            "SplitGM is loading the selected audio-group data and exporting every recoverable sound.",
            dialog.FolderName);
        Progress<DecompileProgress> progress = CreateDetailedProgress();

        try
        {
            CancelCurrentPreview();
            await _session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            ResourceExportResult result = await _session.ExportAudioGroupAsync(
                groupIndex,
                dialog.FolderName,
                progress,
                _operationCancellation.Token);
            _lastOutputDirectory = result.OutputPath;
            OpenOutputButton.IsEnabled = true;
            StatusTextBlock.Text = $"Exported audio group {result.ResourceName}.";
            StatusDetailTextBlock.Text = $"{result.FilesWritten:N0} files • {FormatBytes(result.BytesWritten)}";
            CompleteOperationWindow(true, $"Exported {result.FilesWritten:N0} audio file(s), {FormatBytes(result.BytesWritten)}.");
            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputPath))
                Process.Start(new ProcessStartInfo(result.OutputPath) { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Audio-group export cancelled.";
            CompleteOperationWindow(false, "Audio-group export cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            MessageBox.Show(this, exception.Message, "Audio-group export error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Audio-group export failed.";
            CompleteOperationWindow(false, "Audio-group export failed.");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAllCodeAsync();

    private async void CodeSearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SearchAllCodeAsync();
    }

    private async Task SearchAllCodeAsync()
    {
        if (_session is null || _isBusy)
            return;

        string query = CodeSearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            MessageBox.Show(this, "Enter text to search for.", "SplitGM search", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Searching all code...");
        _operationCancellation = new CancellationTokenSource();
        MainTabControl.SelectedItem = SearchTab;
        SearchResultsGrid.ItemsSource = null;
        SearchSummaryText.Text = $"Searching for \"{query}\"...";
        Progress<DecompileProgress> progress = new(update =>
        {
            StatusTextBlock.Text = update.Message;
            ProgressBar.Value = update.Total > 0 ? update.Percentage : 0;
        });

        try
        {
            IReadOnlyList<CodeSearchResult> results = await _session.SearchCodeAsync(
                query, 1000, progress, _operationCancellation.Token);
            SearchResultsGrid.ItemsSource = results;
            SearchSummaryText.Text = results.Count >= 1000
                ? $"Showing the first {results.Count:N0} results for \"{query}\"."
                : $"Found {results.Count:N0} results for \"{query}\".";
            StatusTextBlock.Text = "Search complete.";
            ProgressBar.Value = 100;
            AppendLog(LogMessage.Info($"Code search for \"{query}\" returned {results.Count:N0} results."));
        }
        catch (OperationCanceledException)
        {
            SearchSummaryText.Text = "Search cancelled.";
            StatusTextBlock.Text = "Search cancelled.";
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            SearchSummaryText.Text = "Search failed.";
            MessageBox.Show(this, exception.Message, "SplitGM search error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private async void SearchResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResultsGrid.SelectedItem is CodeSearchResult result)
            await DisplayCodeEntryAsync(result.CodeIndex, result.Source);
    }

    private async void FilterResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyResourceFilterAsync();
    }

    private async void ResourceFilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await ApplyResourceFilterAsync();
    }

    private void ClearResourceFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        ResourceFilterTextBox.Clear();
        PopulateRootTree();
        StatusTextBlock.Text = "Resource filter cleared.";
    }

    private async Task ApplyResourceFilterAsync()
    {
        GameProjectSession? session = _session;
        if (session is null || _isBusy)
            return;

        string query = ResourceFilterTextBox.Text.Trim();
        if (query.Length == 0)
        {
            PopulateRootTree();
            return;
        }

        const int maximumMatches = 2000;
        SetBusy(true, $"Filtering resources for \"{query}\"...");
        _operationCancellation = new CancellationTokenSource();
        CancellationToken token = _operationCancellation.Token;

        try
        {
            (IReadOnlyList<ExplorerNode> nodes, int totalMatches) = await Task.Run(
                () => BuildFilteredTree(session, query, maximumMatches, token),
                token);
            token.ThrowIfCancellationRequested();

            _treeNodes.Clear();
            foreach (ExplorerNode node in nodes)
                _treeNodes.Add(node);

            ResourceTreeCountText.Text = totalMatches >= maximumMatches
                ? $"first {totalMatches:N0} matches"
                : $"{totalMatches:N0} matches";
            StatusTextBlock.Text = totalMatches == 0
                ? $"No resources matched \"{query}\"."
                : $"Resource filter found {totalMatches:N0} matches.";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Resource filter cancelled.";
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            StatusTextBlock.Text = "Resource filter failed.";
            MessageBox.Show(this, exception.Message, "Resource filter error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private static (IReadOnlyList<ExplorerNode> Nodes, int TotalMatches) BuildFilteredTree(
        GameProjectSession session,
        string query,
        int maximumMatches,
        CancellationToken cancellationToken)
    {
        List<ExplorerNode> nodes = [];
        int totalMatches = 0;

        foreach (IGrouping<CodeCategory, CodeEntryInfo> group in session.CodeEntries
                     .Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(entry => entry.Category))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodeEntryInfo[] matchingEntries = group.Take(maximumMatches - totalMatches).ToArray();
            if (matchingEntries.Length == 0)
                continue;

            ExplorerNode parent = new()
            {
                DisplayName = "Code / " + FriendlyCategoryName(group.Key),
                Icon = CategoryIcon(group.Key),
                Kind = ExplorerNodeKind.Group,
                Count = matchingEntries.Length,
                IsLoaded = true
            };
            foreach (CodeEntryInfo entry in matchingEntries)
            {
                parent.Children.Add(new ExplorerNode
                {
                    DisplayName = entry.Name,
                    Icon = "•",
                    Kind = ExplorerNodeKind.CodeEntry,
                    Index = entry.Index,
                    CodeCategory = entry.Category
                });
                totalMatches++;
            }
            nodes.Add(parent);
            if (totalMatches >= maximumMatches)
                return (nodes, totalMatches);
        }

        foreach (ResourceKind kind in Enum.GetValues<ResourceKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceEntryInfo[] matches = session.GetResourceEntries(kind)
                .Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(maximumMatches - totalMatches)
                .ToArray();
            if (matches.Length == 0)
                continue;

            ExplorerNode parent = new()
            {
                DisplayName = FriendlyResourceName(kind),
                Icon = "◆",
                Kind = ExplorerNodeKind.Group,
                Count = matches.Length,
                IsLoaded = true
            };
            foreach (ResourceEntryInfo entry in matches)
            {
                parent.Children.Add(new ExplorerNode
                {
                    DisplayName = entry.Name,
                    Icon = "•",
                    Kind = ExplorerNodeKind.ResourceEntry,
                    Index = entry.Index,
                    ResourceKind = entry.Kind
                });
            }
            nodes.Add(parent);
            totalMatches += matches.Length;
            if (totalMatches >= maximumMatches)
                break;
        }

        return (nodes, totalMatches);
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _isBusy)
            return;

        string defaultDirectory = string.IsNullOrWhiteSpace(_settings.DefaultExportDirectory)
            ? BuildDefaultExportDirectory(_session.Info)
            : Path.Combine(_settings.DefaultExportDirectory,
                OutputPathHelper.SafeFileName(_session.Info.DisplayName) + "_SplitGM_Project");
        OpenFolderDialog dialog = new()
        {
            Title = "Select the SplitGM reconstructed-project folder",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(defaultDirectory))
                ? Path.GetDirectoryName(defaultDirectory)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        string selected = dialog.FolderName;
        bool overwrite = Directory.Exists(selected) && Directory.EnumerateFileSystemEntries(selected).Any();
        if (overwrite && _settings.ConfirmOverwrite)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "The selected folder is not empty.\n\nSplitGM can only replace it if it is an earlier SplitGM output folder. Continue with overwrite enabled?",
                "Overwrite SplitGM output", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        SetBusy(true, "Exporting reconstructed project and resources...");
        _operationCancellation = new CancellationTokenSource();
        StartOperationWindow(
            "Export full reconstructed project",
            "SplitGM is decompiling VM code, exporting resources, and writing project indexes and diagnostics.",
            selected);
        MainTabControl.SelectedItem = ActivityTab;
        Progress<DecompileProgress> progress = CreateDetailedProgress();
        Progress<LogMessage> log = CreateDetailedLog();

        try
        {
            CancelCurrentPreview();
            await _session.WaitForResourcePreviewIdleAsync(_operationCancellation.Token);
            DecompileResult result = await _exporter.ExportAsync(
                _session,
                new ProjectExportOptions(
                    selected,
                    overwrite,
                    _settings.ExportAssembly,
                    _settings.ExportIndexes,
                    _settings.ExportResources),
                progress,
                log,
                _operationCancellation.Token);

            _lastOutputDirectory = result.OutputDirectory;
            OpenOutputButton.IsEnabled = true;
            ProgressBar.Value = 100;
            StatusTextBlock.Text = "Project export completed.";
            StatusDetailTextBlock.Text =
                $"{result.SuccessfulEntries:N0} GML succeeded • {result.FailedEntries:N0} failed • {result.Elapsed:g}";
            CompleteOperationWindow(true,
                $"Export completed: {result.SuccessfulEntries:N0} GML succeeded, {result.FailedEntries:N0} failed.");
            if (_settings.OpenOutputAfterExport && Directory.Exists(result.OutputDirectory))
                Process.Start(new ProcessStartInfo(result.OutputDirectory) { UseShellExecute = true });
            MessageBox.Show(this,
                _session.Info.IsYyc
                    ? "Resource extraction and metadata export completed. This is a YYC game, so VM GML was unavailable."
                    : $"SplitGM project export completed.\n\nGML succeeded: {result.SuccessfulEntries:N0}\nGML failed: {result.FailedEntries:N0}\nWarnings: {result.WarningCount:N0}",
                "SplitGM export", MessageBoxButton.OK,
                result.FailedEntries == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            AppendLog(LogMessage.Warning("Project export was cancelled."));
            StatusTextBlock.Text = "Export cancelled.";
            CompleteOperationWindow(false, "Project export cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(LogMessage.Error(exception.ToString()));
            MessageBox.Show(this, exception.Message, "SplitGM export error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "Project export failed.";
            CompleteOperationWindow(false, "Project export failed.");
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputDirectory) || !Directory.Exists(_lastOutputDirectory))
            return;
        Process.Start(new ProcessStartInfo { FileName = _lastOutputDirectory, UseShellExecute = true });
    }

    private void CopyDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null)
            return;
        StringBuilder report = new();
        report.AppendLine(_session.GetCompatibilityReportText());
        report.AppendLine();
        report.AppendLine("Environment");
        report.AppendLine("-----------");
        report.AppendLine($"SplitGM version: {SplitGmProduct.Version}");
        report.AppendLine($".NET runtime: {Environment.Version}");
        report.AppendLine($"Operating system: {Environment.OSVersion}");
        report.AppendLine($"Session log file: {Path.GetFileName(_appLog.LogPath)}");
        Clipboard.SetText(report.ToString());
        StatusTextBlock.Text = "Diagnostic report copied to the clipboard.";
    }

    private void CopyCurrentViewButton_Click(object sender, RoutedEventArgs e)
    {
        string text = MainTabControl.SelectedItem switch
        {
            TabItem tab when tab == PreviewTab => ResourceTextViewer.Visibility == Visibility.Visible
                ? ResourceTextViewer.Text
                : PreviewPropertiesTextBox.Text,
            TabItem tab when tab == GmlTab => GmlViewer.Text,
            TabItem tab when tab == AssemblyTab => AssemblyViewer.Text,
            TabItem tab when tab == DetailsTab => DetailsTextBox.Text,
            TabItem tab when tab == ActivityTab => LogTextBox.Text,
            _ when SearchResultsGrid.SelectedItem is CodeSearchResult result =>
                $"{result.CodeName}:{result.LineNumber} [{result.Source}] {result.Snippet}",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            StatusTextBlock.Text = "Current view copied to the clipboard.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _operationCancellation?.Cancel();
        _previewCancellation?.Cancel();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
            await LoadGameAsync(files[0]);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _operationCancellation?.Cancel();
        SaveWindowSettings();
        CloseOperationWindow(force: true);
        CloseReconstructionWindow(force: true);
        CancelCurrentPreview();
        StopMediaPreview();
        _audioPlayer.Dispose();
        GameProjectSession? sessionToDispose = _session;
        _session = null;
        if (sessionToDispose is not null)
            _ = Task.Run(sessionToDispose.Dispose);
        _appLog.Dispose();
    }

    private void CloseCurrentGame(bool clearDisplay = true)
    {
        CancelCurrentPreview();
        StopMediaPreview();
        _currentCodeView = null;
        _currentResourcePreview = null;
        _currentResourceKind = null;
        _currentResourceIndex = -1;
        _selectedExplorerNode = null;
        RelationshipGrid.ItemsSource = null;
        RelationshipSummaryTextBox.Text = "Load a game and select a code entry or resource to analyze relationships.";
        GameProjectSession? sessionToDispose = _session;
        _session = null;
        if (sessionToDispose is not null)
        {
            // A very large Underanalyzer decompile cannot be interrupted halfway through.
            // Dispose on a worker so closing/replacing the current game never blocks WPF's UI thread.
            _ = Task.Run(sessionToDispose.Dispose).ContinueWith(
                task => _appLog.Write(LogMessage.Error("Background game disposal failed: " + task.Exception)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        _treeNodes.Clear();
        SearchResultsGrid.ItemsSource = null;
        CloseGameButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ExportResourceTypeMenuItem.IsEnabled = false;
        ExportSelectedButton.IsEnabled = false;
        CopyDiagnosticButton.IsEnabled = false;
        AnalyzeSelectedMenuItem.IsEnabled = false;
        UnusedCandidatesMenuItem.IsEnabled = false;
        ExportAudioGroupMenuItem.IsEnabled = false;
        SearchButton.IsEnabled = false;
        CompatibilityBadge.Text = "NO GAME LOADED";
        CompatibilityBadge.Foreground = (Brush)FindResource("MutedTextBrush");
        ResourceTreeCountText.Text = string.Empty;
        StatusDetailTextBlock.Text = string.Empty;
        if (clearDisplay)
            ResetWelcomeDisplay();
    }

    private void ResetWelcomeDisplay()
    {
        CurrentItemTitle.Text = "Welcome to SplitGM v0.5.0";
        CurrentItemSubtitle.Text = "Open a GameMaker game to browse every recoverable resource.";
        DetailsTextBox.Text = BuildWelcomeText();
        PreviewPropertiesTextBox.Text = BuildWelcomeText();
        CodeDocumentRenderer.SetText(GmlViewer, "// Open a game and select a code entry.", CodeDocumentMode.Gml, true);
        CodeDocumentRenderer.SetText(AssemblyViewer, "; Open a game and select a code entry.", CodeDocumentMode.Assembly, true);
        PreviewImage.Source = null;
        PreviewEmptyText.Visibility = Visibility.Visible;
        ResetSpecializedPreviewPanels();
        MainTabControl.SelectedItem = DetailsTab;
        StatusTextBlock.Text = "Ready. Open or drop a GameMaker file.";
        ProgressBar.Value = 0;
    }

    private void ResetSpecializedPreviewPanels()
    {
        SpriteControlsPanel.Visibility = Visibility.Collapsed;
        AudioControlsPanel.Visibility = Visibility.Collapsed;
        ExportAudioGroupButton.Visibility = Visibility.Collapsed;
        ExportAudioGroupMenuItem.IsEnabled = false;
        ObjectEventsTab.Visibility = Visibility.Collapsed;
        RoomLayersTab.Visibility = Visibility.Collapsed;
        RoomInstancesTab.Visibility = Visibility.Collapsed;
        RoomTilesTab.Visibility = Visibility.Collapsed;
        ObjectEventsGrid.ItemsSource = null;
        RoomLayersGrid.ItemsSource = null;
        RoomInstancesGrid.ItemsSource = null;
        RoomTilesGrid.ItemsSource = null;
        PreviewInformationTabs.SelectedIndex = 0;
        _spriteTimer.Stop();
        SpritePlayButton.Content = "Play";
    }

    private void StopMediaPreview()
    {
        _spriteTimer.Stop();
        SpritePlayButton.Content = "Play";
        _audioPlayer.Stop();
    }

    private void CancelCurrentPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        OpenGameButton.IsEnabled = !busy;
        CloseGameButton.IsEnabled = !busy && _session is not null;
        ExportButton.IsEnabled = !busy && _session is not null;
        ReconstructedYypMenuItem.IsEnabled = !busy && _session is not null;
        ExportResourceTypeMenuItem.IsEnabled = !busy && _session is not null;
        ExportSelectedButton.IsEnabled = !busy && _session is not null && _currentResourceKind is not null;
        SearchButton.IsEnabled = !busy && _session is not null && !_session.Info.IsYyc;
        AnalyzeSelectedMenuItem.IsEnabled = !busy && _session is not null &&
            (_currentCodeView is not null || (_currentResourceKind is not null && _currentResourceIndex >= 0));
        UnusedCandidatesMenuItem.IsEnabled = !busy && _session is not null;
        ExportAudioGroupMenuItem.IsEnabled = !busy && _session is not null &&
            _currentResourcePreview?.PreviewKind == ResourcePreviewKind.Audio &&
            _currentResourceKind is ResourceKind.Sounds or ResourceKind.AudioGroups;
        ResourceTree.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        if (!string.IsNullOrWhiteSpace(status))
            StatusTextBlock.Text = status;
        if (!busy && ProgressBar.Value < 100)
            ProgressBar.Value = 0;
    }

    private void UpdateCompatibilityBadge()
    {
        if (_session is null)
            return;
        CompatibilityBadge.Text = _session.Info.Compatibility switch
        {
            GameCompatibility.Compatible => "VM COMPATIBLE",
            GameCompatibility.YycNoVmCode => "YYC — RESOURCES ONLY",
            GameCompatibility.UnsupportedBytecode => "UNSUPPORTED BYTECODE",
            GameCompatibility.NoCodeEntries => "NO VM CODE",
            _ => "LIMITED SUPPORT"
        };
        CompatibilityBadge.Foreground = (Brush)FindResource(_session.Info.Compatibility switch
        {
            GameCompatibility.Compatible => "SuccessBrush",
            GameCompatibility.YycNoVmCode => "WarningBrush",
            GameCompatibility.UnsupportedBytecode => "ErrorBrush",
            _ => "WarningBrush"
        });
    }

    private void AppendLog(LogMessage message)
    {
        _appLog.Write(message);
        string line = $"[{message.Timestamp:HH:mm:ss.fff}] {message.Level.ToString().ToUpperInvariant(),-7} {message.Text}";
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static string BuildDefaultExportDirectory(GameProjectInfo info)
    {
        string baseName = OutputPathHelper.SafeFileName(
            string.IsNullOrWhiteSpace(info.DisplayName) ? info.GameName : info.DisplayName);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            baseName + "_SplitGM_Project");
    }

    private static string FriendlyCategoryName(CodeCategory category) => category switch
    {
        CodeCategory.Scripts => "Scripts",
        CodeCategory.ObjectEvents => "Object Events",
        CodeCategory.RoomCode => "Room Code",
        CodeCategory.GlobalInit => "Global Initialization",
        CodeCategory.Timelines => "Timeline Code",
        _ => "Other Code"
    };

    private static string CategoryIcon(CodeCategory category) => category switch
    {
        CodeCategory.Scripts => "S",
        CodeCategory.ObjectEvents => "O",
        CodeCategory.RoomCode => "R",
        CodeCategory.GlobalInit => "G",
        CodeCategory.Timelines => "T",
        _ => "?"
    };

    private static string FriendlyResourceName(ResourceKind kind) => kind switch
    {
        ResourceKind.Backgrounds => "Backgrounds / Tilesets",
        ResourceKind.AudioGroups => "Audio Groups",
        ResourceKind.EmbeddedAudio => "Embedded Audio",
        ResourceKind.AnimationCurves => "Animation Curves",
        ResourceKind.ParticleSystems => "Particle Systems",
        ResourceKind.ParticleSystemEmitters => "Particle Emitters",
        ResourceKind.TextureGroups => "Texture Groups",
        ResourceKind.TexturePageItems => "Texture Page Items",
        ResourceKind.TexturePages => "Texture Pages",
        ResourceKind.EmbeddedImages => "Embedded Images",
        ResourceKind.FilterEffects => "Filter Effects",
        _ => kind.ToString()
    };

    private static string BuildWelcomeText()
    {
        return "SplitGM-VM Decompiler v0.5.0\r\n" +
               "================================\r\n\r\n" +
               "This release adds relationship navigation, progress windows, settings, and a cleaner menu-driven interface.\r\n\r\n" +
               "• Browse resources without modifying the game.\r\n" +
               "• Preview sprite frames, rooms, object sprites, backgrounds, fonts, and texture pages.\r\n" +
               "• Inspect room layers, instances, and recoverable tile records.\r\n" +
               "• Play WAV/OGG/MP3 audio and export one sound or a complete audio group.\r\n" +
               "• View large GML and VM assembly with AvalonEdit and background decompilation.\r\n" +
               "• Extract all recoverable resources alongside the organized reconstructed project.\r\n" +
               "• Double-click object events and room instances to open connected GML code.\r\n" +
               "• Analyze callers, callees, room transitions, inheritance, asset references, globals, and unused candidates.\r\n" +
               "• Configure SplitGM through SplitGM_Settings.ini.\r\n\r\n" +
               "SplitGM uses UndertaleModLib and Underanalyzer from UndertaleModTool under GPLv3.";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}

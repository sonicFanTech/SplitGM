using System.Collections.ObjectModel;
using SplitGM.Core;

namespace SplitGM.Gui;

public enum ExplorerNodeKind
{
    Information,
    Group,
    CodeEntry,
    ResourceEntry,
    Placeholder
}

public sealed class ExplorerNode
{
    public required string DisplayName { get; init; }
    public string Icon { get; init; } = "•";
    public ExplorerNodeKind Kind { get; init; }
    public int Count { get; init; }
    public int Index { get; init; } = -1;
    public CodeCategory? CodeCategory { get; init; }
    public ResourceKind? ResourceKind { get; init; }
    public int RangeStart { get; init; } = -1;
    public int RangeCount { get; init; }
    public bool IsRangePage => RangeStart >= 0;
    public bool IsLazy { get; init; }
    public bool IsLoaded { get; set; }
    public ObservableCollection<ExplorerNode> Children { get; } = [];

    public string CountText => Count > 0 ? $"{Count:N0}" : string.Empty;

    public void AddLoadingPlaceholder()
    {
        if (!IsLazy || Children.Count > 0)
            return;

        Children.Add(new ExplorerNode
        {
            DisplayName = "Loading...",
            Icon = "…",
            Kind = ExplorerNodeKind.Placeholder
        });
    }
}

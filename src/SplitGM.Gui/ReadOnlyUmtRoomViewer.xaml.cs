using SplitGM.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SplitGM.Gui;

/// <summary>
/// A deliberately read-only room surface based on UndertaleModTool's room-viewer
/// interaction model: nearest-neighbor rendering, scroll/pan-friendly layout,
/// independent zoom, fit-to-window, and an optional room grid. All destructive
/// editor commands and mutation bindings are intentionally omitted.
/// </summary>
public partial class ReadOnlyUmtRoomViewer : UserControl
{
    private RoomPreviewInfo? _room;
    private BitmapSource? _bitmap;

    public ReadOnlyUmtRoomViewer()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateGridBrush();
        SizeChanged += (_, _) =>
        {
            if (GridCheckBox.IsChecked == true)
                UpdateGridBrush();
        };
    }

    public void LoadRoom(byte[]? png, RoomPreviewInfo? room)
    {
        _room = room;
        _bitmap = BitmapSourceFactory.FromBytes(png);
        RoomImage.Source = _bitmap;
        RoomEmptyText.Visibility = _bitmap is null ? Visibility.Visible : Visibility.Collapsed;

        if (room is null)
        {
            RoomSummaryText.Text = "Read-only UMT room preview";
        }
        else
        {
            RoomSummaryText.Text = $"{room.Width:N0}×{room.Height:N0} • {room.Layers.Count:N0} layers • " +
                                   $"{room.Instances.Count:N0} instances • {room.Tiles.Count:N0} tiles";
        }

        RoomSurface.Width = Math.Max(1, _bitmap?.PixelWidth ?? 1);
        RoomSurface.Height = Math.Max(1, _bitmap?.PixelHeight ?? 1);
        ZoomSlider.Value = 1;
        UpdateGridBrush();
    }

    public void Clear() => LoadRoom(null, null);

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RoomScaleTransform is null)
            return;
        double zoom = Math.Clamp(e.NewValue, 0.1, 8);
        RoomScaleTransform.ScaleX = zoom;
        RoomScaleTransform.ScaleY = zoom;
        UpdateGridBrush();
    }

    private void FitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bitmap is null || RoomScrollViewer.ViewportWidth <= 1 || RoomScrollViewer.ViewportHeight <= 1)
            return;

        double availableWidth = Math.Max(1, RoomScrollViewer.ViewportWidth - 24);
        double availableHeight = Math.Max(1, RoomScrollViewer.ViewportHeight - 24);
        ZoomSlider.Value = Math.Clamp(Math.Min(
            availableWidth / _bitmap.PixelWidth,
            availableHeight / _bitmap.PixelHeight), 0.1, 8);
    }

    private void ActualSizeButton_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value = 1;

    private void GridCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        GridOverlay.Visibility = GridCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateGridBrush();
    }

    private void RoomScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value + (e.Delta > 0 ? 0.1 : -0.1), 0.1, 8);
    }

    private void UpdateGridBrush()
    {
        if (GridOverlay is null || GridCheckBox?.IsChecked != true)
            return;

        double zoom = Math.Max(0.1, ZoomSlider?.Value ?? 1);
        double logicalCell = _room is null || _room.Width <= 640 ? 16 : 32;
        double cell = Math.Max(4, logicalCell);
        DrawingGroup group = new();
        using (DrawingContext context = group.Open())
        {
            Pen pen = new(new SolidColorBrush(Color.FromArgb(96, 115, 175, 214)), 1 / zoom);
            pen.Freeze();
            context.DrawLine(pen, new Point(0, 0), new Point(cell, 0));
            context.DrawLine(pen, new Point(0, 0), new Point(0, cell));
        }
        group.Freeze();

        DrawingBrush brush = new(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        GridOverlay.Background = brush;
    }
}

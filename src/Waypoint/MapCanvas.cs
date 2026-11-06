using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

public sealed class MapCanvas : FrameworkElement
{
    static readonly Dictionary<PackIconKind, Geometry> Icons = new();
    MapData? _map;
    List<MapMarker> _markers = [];
    BitmapSource? _walk;
    double _scale = 1, _x, _y;
    Point? _drag;
    bool _moved;
    internal string? Highlight { get; set; }
    internal MapMarkerKey? SelectedMarker { get; set; }
    MapLayerSettings _layers = new();
    internal IReadOnlyList<MapMarker> VisibleMarkers => _markers;
    internal event Action<IReadOnlyList<MapMarker>, Point>? MarkerChoicesRequested;
    internal event Action<string>? Inspected;
    internal event Action<string?>? TileSelected;
    internal event Action<MapMarkerKey>? MarkerSelected;

    public MapCanvas() { SizeChanged += (_, _) => Fit(); Cursor = Cursors.Arrow; LostMouseCapture += (_, _) => { _drag = null; Cursor = Cursors.Arrow; }; }

    internal void SetMap(MapData map)
    {
        _map = map; Highlight = null; SelectedMarker = null;
        RefreshMarkers();
        var pixels = new byte[map.Walk.Length * 4];
        var open = ((SolidColorBrush)Theme.Brush("Shell.Good")).Color;
        var blocked = ((SolidColorBrush)Theme.Brush("Shell.Panel")).Color;
        for (int i = 0; i < map.Walk.Length; i++)
        {
            var color = map.Walk[i] >= 2 ? open : blocked;
            pixels[4*i] = color.B; pixels[4*i+1] = color.G; pixels[4*i+2] = color.R; pixels[4*i+3] = 255;
        }
        _walk = BitmapSource.Create(map.WalkWidth, map.WalkHeight, 96, 96, PixelFormats.Bgra32, null, pixels, map.WalkWidth * 4);
        _walk.Freeze(); Fit();
    }

    internal void SetLayers(MapLayerSettings layers)
    {
        _layers = layers;
        RefreshMarkers();
        InvalidateVisual();
    }

    void RefreshMarkers()
    {
        _markers = _map is null ? [] : MapMarkers.Create(_map, _layers.InternalMarkers, _layers.MonsterPacks, _layers.Encounters, _layers.Markers);
        if (SelectedMarker is { } key && !_markers.Any(m => m.Key == key)) SelectedMarker = null;
    }

    internal void SelectMarker(MapMarker marker)
    {
        Highlight = null; SelectedMarker = marker.Key;
        MarkerSelected?.Invoke(marker.Key);
        Inspected?.Invoke($"{marker.Label} ({marker.X}, {marker.Y})");
        InvalidateVisual();
    }

    internal void Fit()
    {
        if (_map is null || ActualWidth < 40 || ActualHeight < 40) return;
        double size = (_map.Width + _map.Height) / Math.Sqrt(2);
        _scale = Math.Min((ActualWidth - 32) / size, (ActualHeight - 32) / size);
        _x = ActualWidth / 2 + (_map.Height - _map.Width) * _scale / (2 * Math.Sqrt(2));
        _y = (ActualHeight + size * _scale) / 2;
        InvalidateVisual();
    }

    Matrix Projection()
    {
        double s = _scale / Math.Sqrt(2);
        return new Matrix(s, -s, -s, -s, _x, _y);
    }

    internal void CenterOn(int x, int y)
    {
        double s = _scale / Math.Sqrt(2);
        _x = ActualWidth / 2 - (x - y) / 23.0 * s;
        _y = ActualHeight / 2 + (x + y) / 23.0 * s;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Theme.Brush("Shell.Background"), null, new Rect(RenderSize));
        if (_map is null || _walk is null) return;
        dc.PushTransform(new MatrixTransform(Projection()));
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        if (_layers.Walkability) dc.DrawImage(_walk, new Rect(0, 0, _map.Width, _map.Height));
        else dc.DrawRectangle(Theme.Brush("Shell.Panel"), null, new Rect(0, 0, _map.Width, _map.Height));
        var grid = new Pen(Theme.Brush("Shell.Border"), 0.5 / _scale);
        if (_layers.Grid) {
            for (int x = 0; x <= _map.Width; x++) dc.DrawLine(grid, new Point(x, 0), new Point(x, _map.Height));
            for (int y = 0; y <= _map.Height; y++) dc.DrawLine(grid, new Point(0, y), new Point(_map.Width, y));
        }
        if (Highlight is not null)
            foreach (var tile in _map.Tiles.Where(t => t.Path == Highlight)) dc.DrawRectangle(Theme.Brush("Shell.Accent"), null, new Rect(tile.X, tile.Y, 1, 1));
        dc.Pop();
        var projection = Projection();
        if (_layers.TeleportLinks)
        {
            var linkPen = new Pen(Theme.Brush("Shell.TextSecondary"), 1.5) { DashStyle = DashStyles.Dash };
            foreach (var link in MapNavigation.TeleportLinks(_map))
                dc.DrawLine(linkPen, projection.Transform(new Point(link.X / 23.0, link.Y / 23.0)),
                    projection.Transform(new Point(link.TargetX / 23.0, link.TargetY / 23.0)));
        }
        void Mark(MapMarker marker)
        {
            var at = projection.Transform(new Point(marker.X / 23.0, marker.Y / 23.0));
            if (!Icons.TryGetValue(marker.Icon, out var geometry))
            {
                var icon = new PackIcon { Kind = marker.Icon };
                if (string.IsNullOrEmpty(icon.Data)) return;
                geometry = Geometry.Parse(icon.Data); geometry.Freeze(); Icons[marker.Icon] = geometry;
            }
            if (SelectedMarker == marker.Key)
                dc.DrawEllipse(null, new Pen(Theme.Brush("Shell.Text"), 2.5), at, 14, 14);
            dc.DrawEllipse(Theme.Brush("Shell.Background"), marker.Framed ? new Pen(Theme.Brush(marker.Brush), 1.5) : null, at, 10, 10);
            dc.PushTransform(new TranslateTransform(at.X - 8, at.Y - 8));
            dc.PushTransform(new ScaleTransform(16.0 / 24, 16.0 / 24));
            dc.DrawGeometry(Theme.Brush(marker.Brush), null, geometry);
            dc.Pop(); dc.Pop();
        }
        {
            foreach (var marker in _markers.Where(m => m.Key != SelectedMarker)) Mark(marker);
            if (SelectedMarker is { } selected && _markers.FirstOrDefault(m => m.Key == selected) is { } active) Mark(active);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_map is null) return;
        var p = e.GetPosition(this); double before = _scale;
        _scale = Math.Clamp(_scale * Math.Exp(e.Delta / 700.0), .25, 100);
        _x = p.X - (p.X - _x) * _scale / before; _y = p.Y - (p.Y - _y) * _scale / before;
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { _drag = e.GetPosition(this); _moved = false; CaptureMouse(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not { } previous || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this); var d = p - previous;
        if (Math.Abs(d.X) + Math.Abs(d.Y) > 1) { _moved = true; Cursor = Cursors.Hand; }
        _x += d.X; _y += d.Y; _drag = p; InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture(); _drag = null; Cursor = Cursors.Arrow;
        if (_moved || _map is null) return;
        var inverse = Projection(); inverse.Invert(); var p = inverse.Transform(e.GetPosition(this));
        var choices = MapMarkers.FindAll(_markers.OrderBy(m => m.Key == SelectedMarker).ToArray(), p.X, p.Y, 12 / _scale);
        if (choices.Count > 1) { MarkerChoicesRequested?.Invoke(choices, e.GetPosition(this)); return; }
        if (choices.Count == 1) { SelectMarker(choices[0]); return; }
        SelectedMarker = null;
        var tile = _map.Tiles.FirstOrDefault(t => t.X == (int)Math.Floor(p.X) && t.Y == (int)Math.Floor(p.Y));
        if (tile is null || MapContent.IsBlankTile(tile.Path))
        { Highlight = null; TileSelected?.Invoke(null); Inspected?.Invoke(""); InvalidateVisual(); return; }
        Highlight = tile.Path; TileSelected?.Invoke(tile.Path); Inspected?.Invoke($"{tile.Path} ({tile.X}, {tile.Y}) · {tile.Orientation} · {tile.Height}"); InvalidateVisual();
    }
}

readonly record struct MapMarkerKey(string Kind, int Index);
sealed record MapMarker(MapMarkerKey Key, int X, int Y, string Label, PackIconKind Icon, string Brush, bool Framed = false);

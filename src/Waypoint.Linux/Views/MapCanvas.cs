using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Material.Icons;

namespace Waypoint;

/// <summary>The map viewer's map: the generated tiles and walkability at 45 degrees, the markers over them, zoom at the
/// pointer, drag to move, a click on a marker or a tile. The Linux copy of src/Waypoint/MapCanvas.cs, in Avalonia.</summary>
sealed class MapCanvas : Control
{
    static readonly Dictionary<MaterialIconKind, Geometry> Icons = [];
    MapData? _map;
    List<MapMarker> _markers = [];
    WriteableBitmap? _walk;
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

    static readonly Cursor Arrow = new(StandardCursorType.Arrow), Hand = new(StandardCursorType.Hand);

    public MapCanvas()
    {
        ClipToBounds = true;
        Cursor = Arrow;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Fit(); };
        // Another scheme: the walkability is drawn again in its colours.
        void Repaint() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_map is { } map) SetMap(map, keepView: true); else InvalidateVisual(); });
        AttachedToVisualTree += (_, _) => AppTheme.Changed += Repaint;
        DetachedFromVisualTree += (_, _) => AppTheme.Changed -= Repaint;
    }

    internal void SetMap(MapData map, bool keepView = false)
    {
        if (!keepView) { Highlight = null; SelectedMarker = null; }
        _map = map;
        RefreshMarkers();
        var open = Colour("Shell.Good");
        var blocked = Colour("Shell.Panel");
        _walk?.Dispose();
        _walk = new WriteableBitmap(new PixelSize(map.WalkWidth, map.WalkHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = _walk.Lock())
        {
            var row = new byte[map.WalkWidth * 4];
            for (int y = 0; y < map.WalkHeight; y++)
            {
                for (int x = 0; x < map.WalkWidth; x++)
                {
                    var color = map.Walk[y * map.WalkWidth + x] >= 2 ? open : blocked;
                    row[4 * x] = color.B; row[4 * x + 1] = color.G; row[4 * x + 2] = color.R; row[4 * x + 3] = 255;
                }
                Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
            }
        }
        if (keepView) InvalidateVisual(); else Fit();
    }

    static Color Colour(string key) => AppTheme.Brush(key) is ISolidColorBrush brush ? brush.Color : Colors.Gray;

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
        if (_map is null || Bounds.Width < 40 || Bounds.Height < 40) return;
        double size = (_map.Width + _map.Height) / Math.Sqrt(2);
        _scale = Math.Min((Bounds.Width - 32) / size, (Bounds.Height - 32) / size);
        _x = Bounds.Width / 2 + (_map.Height - _map.Width) * _scale / (2 * Math.Sqrt(2));
        _y = (Bounds.Height + size * _scale) / 2;
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
        _x = Bounds.Width / 2 - (x - y) / 23.0 * s;
        _y = Bounds.Height / 2 + (x + y) / 23.0 * s;
    }

    /// <summary>A Material Design icon as a geometry on its 24-unit square.</summary>
    static Geometry? IconGeometry(MaterialIconKind kind)
    {
        if (Icons.TryGetValue(kind, out var known)) return known;
        string data = MaterialIconDataProvider.GetData(kind);
        if (string.IsNullOrEmpty(data)) return null;
        return Icons[kind] = Geometry.Parse(data);
    }

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(AppTheme.Brush("Shell.Background"), null, new Rect(Bounds.Size));
        if (_map is null || _walk is null) return;
        var projection = Projection();
        using (dc.PushTransform(projection))
        {
            if (_layers.Walkability) dc.DrawImage(_walk, new Rect(0, 0, _walk.PixelSize.Width, _walk.PixelSize.Height), new Rect(0, 0, _map.Width, _map.Height));
            else dc.DrawRectangle(AppTheme.Brush("Shell.Panel"), null, new Rect(0, 0, _map.Width, _map.Height));
            var grid = new Pen(AppTheme.Brush("Shell.Border"), 0.5 / _scale);
            if (_layers.Grid)
            {
                for (int x = 0; x <= _map.Width; x++) dc.DrawLine(grid, new Point(x, 0), new Point(x, _map.Height));
                for (int y = 0; y <= _map.Height; y++) dc.DrawLine(grid, new Point(0, y), new Point(_map.Width, y));
            }
            if (Highlight is not null)
                foreach (var tile in _map.Tiles.Where(t => t.Path == Highlight)) dc.DrawRectangle(AppTheme.Brush("Shell.Accent"), null, new Rect(tile.X, tile.Y, 1, 1));
        }
        if (_layers.TeleportLinks)
        {
            var linkPen = new Pen(AppTheme.Brush("Shell.TextSecondary"), 1.5) { DashStyle = DashStyle.Dash };
            foreach (var link in MapNavigation.TeleportLinks(_map))
                dc.DrawLine(linkPen, projection.Transform(new Point(link.X / 23.0, link.Y / 23.0)),
                    projection.Transform(new Point(link.TargetX / 23.0, link.TargetY / 23.0)));
        }
        void Mark(MapMarker marker)
        {
            var at = projection.Transform(new Point(marker.X / 23.0, marker.Y / 23.0));
            if (IconGeometry(marker.Icon) is not { } geometry) return;
            if (SelectedMarker == marker.Key)
                dc.DrawEllipse(null, new Pen(AppTheme.Brush("Shell.Text"), 2.5), at, 14, 14);
            dc.DrawEllipse(AppTheme.Brush("Shell.Background"), marker.Framed ? new Pen(AppTheme.Brush(marker.Brush), 1.5) : null, at, 10, 10);
            using (dc.PushTransform(Matrix.CreateScale(16.0 / 24, 16.0 / 24) * Matrix.CreateTranslation(at.X - 8, at.Y - 8)))
                dc.DrawGeometry(AppTheme.Brush(marker.Brush), null, geometry);
        }
        foreach (var marker in _markers.Where(m => m.Key != SelectedMarker)) Mark(marker);
        if (SelectedMarker is { } selected && _markers.FirstOrDefault(m => m.Key == selected) is { } active) Mark(active);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_map is null) return;
        var p = e.GetPosition(this); double before = _scale;
        _scale = Math.Clamp(_scale * Math.Exp(e.Delta.Y * 120 / 700.0), .25, 100);
        _x = p.X - (p.X - _x) * _scale / before; _y = p.Y - (p.Y - _y) * _scale / before;
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this); _moved = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } previous || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this); var d = p - previous;
        if (Math.Abs(d.X) + Math.Abs(d.Y) > 1) { _moved = true; Cursor = Hand; }
        _x += d.X; _y += d.Y; _drag = p; InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null; Cursor = Arrow;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        bool moved = _moved;
        _drag = null; Cursor = Arrow;
        e.Pointer.Capture(null);
        if (moved || _map is null || !Projection().TryInvert(out var inverse)) return;
        var p = inverse.Transform(e.GetPosition(this));
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

/// <summary>The area list's order: by name, then by Id in natural order ("Act 2" before "Act 10"), as Windows' own
/// StrCmpLogicalW sorts the Windows list.</summary>
sealed class MapAreaComparer(bool byName) : IComparer<MapArea>
{
    public int Compare(MapArea? left, MapArea? right)
    {
        if (left is null || right is null) return 0;
        int name = byName ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name) : 0;
        return name != 0 ? name : MapAreaSearch.Natural(left.Id, right.Id);
    }
}

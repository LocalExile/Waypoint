using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;

namespace Waypoint;

/// <summary>
/// The day's Labyrinth as a map, as poelab.com draws it but from the checkout's own Gateway (<see cref="LabyrinthDay"/>):
/// each tier's rooms at their places in the in-game map, the connections, the shortest way to Izaro's last fight, and what
/// each room holds (Argus, gauntlets and puzzles, Darkshrines, keys and their doors, Izaro's mechanics).
/// </summary>
public partial class LabLayoutWindow : FormWindow
{
    // The map's units: a section is 3 wide (x 0-3) and 2 high (y 0-2); the next one starts 4 further on.
    const double Unit = 110, MapLeft = 90, MapTop = 56, Room = 44, IconSize = 20;

    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    LabyrinthDay? _day;
    DateOnly _today = LabyrinthDay.Today;
    int _loads;
    bool _settingDay;
    // The view: a scale and offset of the map in its frame. Fitted until the player zooms or moves it.
    double _scale = 1, _offsetX, _offsetY;
    bool _fitted = true;
    Point? _drag;
    bool _moved;
    object? _pressed;
    // The connections the player marked, per day and tier (drawn green over the rooms, as the game shows a way).
    readonly Dictionary<(uint Seed, int Tier), HashSet<(int, int)>> _marked = [];
    readonly Popup _popup = new() { Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true };
    IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>> _sides = new Dictionary<int, IReadOnlyDictionary<int, double>>();

    internal LabLayoutWindow()
    {
        InitializeComponent();
        Standalone = true;
        BuildLegend();
        _clock.Tick += (_, _) => Tick();
        Loaded += (_, _) => { SetDay(_today); _clock.Start(); };
        Closed += (_, _) => _clock.Stop();
        L.Current.PropertyChanged += LanguageChanged;
        Closed += (_, _) => L.Current.PropertyChanged -= LanguageChanged;
    }

    void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        BuildLegend();
        Draw();
    }

    DateOnly Shown => Day.SelectedDate is DateTime d ? DateOnly.FromDateTime(d) : _today;

    void SetDay(DateOnly day)
    {
        _settingDay = true;
        Day.SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        _settingDay = false;
        _ = LoadAsync(day);
    }

    void PreviousDay(object sender, RoutedEventArgs e) => SetDay(Shown.AddDays(-1));
    void NextDay(object sender, RoutedEventArgs e) => SetDay(Shown.AddDays(1));
    void TodayClicked(object sender, RoutedEventArgs e) => SetDay(_today);

    void DayChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_settingDay && IsLoaded) _ = LoadAsync(Shown);
    }

    void TierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tiers) return;
        _fitted = true;
        Draw();
    }

    // The layout changes at UTC midnight: the countdown, and today's map moves on with it.
    void Tick()
    {
        var now = LabyrinthDay.Today;
        if (now != _today)
        {
            bool following = Shown == _today;
            _today = now;
            if (following) { SetDay(now); return; }
        }
        ShowReset();
    }

    void ShowReset()
    {
        bool today = Shown == _today;
        TodayButton.IsEnabled = !today;
        var left = DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow;
        ResetText.Text = today ? L.Text("A new layout in {0}", $"{(int)left.TotalHours}:{left.Minutes:00}") : "";
        ResetText.Visibility = today ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task LoadAsync(DateOnly day)
    {
        int load = ++_loads;
        Busy.Visibility = Visibility.Visible;
        Problem.Visibility = Visibility.Collapsed;
        ShowReset();
        try
        {
            var loaded = await LabyrinthDay.LoadAsync(day);
            if (load != _loads) return;
            _day = loaded;
            _fitted = true;
            Draw();
        }
        catch (Exception ex)
        {
            if (load != _loads) return;
            _day = null;
            Draw();
            Problem.Text = ex.Message;
            Problem.Visibility = Visibility.Visible;
        }
        finally
        {
            if (load == _loads) Busy.Visibility = Visibility.Collapsed;
        }
    }

    LabTier? Tier => _day?.Tiers.FirstOrDefault(t => t.Tier == Tiers.SelectedIndex + 1);

    // Theme colours by reference, so a scheme change reaches the drawn map.
    static T Themed<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    static Line Stroke(double x1, double y1, double x2, double y2, string brush, double thickness) =>
        Themed(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }, Shape.StrokeProperty, brush);

    static (string Brush, PackIconKind Icon) Look(LabFeature feature) => feature switch
    {
        LabFeature.Argus => ("Shell.Error", PackIconKind.ShieldSword),
        LabFeature.TrapGauntlet => ("Shell.Accent", PackIconKind.RunFast),
        LabFeature.EscortGauntlet => ("Shell.Accent", PackIconKind.Cart),
        LabFeature.Puzzle => ("Shell.Accent", PackIconKind.Puzzle),
        LabFeature.Darkshrine => ("Shell.Good", PackIconKind.Candle),
        LabFeature.SilverKey => ("Shell.TextSecondary", PackIconKind.KeyVariant),
        LabFeature.SilverCache => ("Shell.TextSecondary", PackIconKind.TreasureChest),
        LabFeature.GoldKey => ("Shell.Warning", PackIconKind.KeyVariant),
        LabFeature.GoldDoor => ("Shell.Warning", PackIconKind.DoorClosedLock),
        LabFeature.Reliquary => ("Shell.Warning", PackIconKind.DiamondStone),
        _ => ("Shell.TextSecondary", PackIconKind.Gate),
    };

    static Point Centre(LabRoom room) => new(MapLeft + (room.Slot * 4 + room.X) * Unit, MapTop + room.Y * Unit);

    void Draw()
    {
        Map.Children.Clear();
        Notes.Children.Clear();
        var tier = Tier;
        TierTitle.Text = tier is null ? "" : TierName(tier);
        DayText.Text = _day is null ? "" : _day.Date is { } date
            ? L.Text("{0} (UTC), seed {1}", date.ToString("D", L.Current.Culture), _day.Seed)
            : L.Text("Seed {0}, fixed by the server (Game_LAB_SEED)", _day.Seed);
        ShowReset();
        if (tier is null || tier.Rooms.Count == 0) return;

        int slots = tier.Rooms.Max(r => r.Slot) + 1;
        Map.Width = MapLeft * 2 + ((slots - 1) * 4 + 3) * Unit;
        Map.Height = MapTop + 2 * Unit + 40;
        var (wayRooms, wayLinks, length) = tier.ShortestWay();
        _sides = tier.DoorSides();

        // The sections, faintly, and the way in from the plaza.
        for (int slot = 1; slot < slots; slot++)
        {
            double x = MapLeft + (slot * 4 - 0.5) * Unit;
            var line = Stroke(x, MapTop - 30, x, MapTop + 2 * Unit + 20, "Shell.Border", 1.5);
            line.StrokeDashArray = [4, 4];
            Map.Children.Add(line);
        }
        // The way in from the plaza, under the first room.
        var first = Centre(tier.Rooms[0]);
        var plaza = first + (DoorDirection(tier.Rooms[0], -1) ?? new Vector(0, 1)) * (0.6 * Unit);
        var door = DoorPoint(tier.Rooms[0], -1);
        Map.Children.Add(Stroke(plaza.X, plaza.Y, door.X, door.Y, "Shell.Accent", 5));
        Place(Mark(PackIconKind.DoorOpen, "Shell.Accent", 26), plaza.X - 13, plaza.Y - 13, L.Text("Door to the Aspirants' Plaza"));

        // The connections, the shortest way's on top.
        foreach (bool onWay in (bool[])[false, true])
            foreach (var room in tier.Rooms)
                foreach (int to in room.Links.Where(l => l > room.Index && l < tier.Rooms.Count).Distinct())
                {
                    if (wayLinks.Contains((room.Index, to)) != onWay) continue;
                    // From door to door, as poelab.com draws them; a wider clear line over it takes the click.
                    Point a = DoorPoint(room, to), b = DoorPoint(tier.Rooms[to], room.Index);
                    Map.Children.Add(Stroke(a.X, a.Y, b.X, b.Y, onWay ? "Shell.Accent" : "Shell.Border", onWay ? 5 : 3));
                    Map.Children.Add(HitLine(a, b, (room.Index, to)));
                }

        foreach (var room in tier.Rooms) DrawRoom(tier, room, wayRooms, wayLinks);

        // The marked connections, green over the rooms, with their doors.
        foreach (var (from, to) in Marked(tier))
        {
            if (from >= tier.Rooms.Count || to >= tier.Rooms.Count) continue;
            Point a = DoorPoint(tier.Rooms[from], to), b = DoorPoint(tier.Rooms[to], from);
            Map.Children.Add(Stroke(a.X, a.Y, b.X, b.Y, "Shell.Good", 6));
            foreach (var end in (Point[])[a, b])
                Place(Themed(new Ellipse { Width = 13, Height = 13 }, Shape.FillProperty, "Shell.Good"), end.X - 6.5, end.Y - 6.5, null);
            Map.Children.Add(HitLine(a, b, (from, to)));
        }
        WriteNotes(tier, length);
        if (_fitted) Fit();
    }

    /// <summary>The side of its circle a connection leaves a room by (<see cref="LabTier.DoorSides"/>), or null.</summary>
    Vector? DoorDirection(LabRoom room, int to)
    {
        if (_sides.GetValueOrDefault(room.Index) is not { } sides || !sides.TryGetValue(to, out double degrees)) return null;
        double a = degrees * Math.PI / 180;
        return new Vector(Math.Cos(a), -Math.Sin(a));
    }

    /// <summary>Where a connection leaves a room's circle: at its side, else the centre.</summary>
    Point DoorPoint(LabRoom room, int to) =>
        DoorDirection(room, to) is { } v ? Centre(room) + v * (Room / 2) : Centre(room);

    void DrawRoom(LabTier tier, LabRoom room, HashSet<int> wayRooms, HashSet<(int, int)> wayLinks)
    {
        bool onWay = wayRooms.Contains(room.Index);
        var c = Centre(room);
        var body = new Grid { Width = Room, Height = Room, Background = Brushes.Transparent, Tag = new Func<object>(() => RoomTip(room, null)),
            Cursor = System.Windows.Input.Cursors.Hand };
        var circle = Themed(new Ellipse { StrokeThickness = 3 }, Shape.FillProperty, "Shell.Background");
        body.Children.Add(Themed(circle, Shape.StrokeProperty, room.Izaro > 0 ? "Shell.Error" : onWay ? "Shell.Accent" : "Shell.TextSecondary"));
        body.Children.Add(Themed(new TextBlock { Text = room.Number.ToString(CultureInfo.InvariantCulture), FontSize = 17, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "Shell.Text"));
        Place(body, c.X - Room / 2, c.Y - Room / 2, null);

        // Its doors: one dot per side, with where its doors lead.
        var bySide = room.Links.Where(l => l < tier.Rooms.Count).Distinct()
            .Where(to => _sides.GetValueOrDefault(room.Index)?.ContainsKey(to) == true)
            .GroupBy(to => _sides[room.Index][to]);
        foreach (var side in bySide)
        {
            if (DoorDirection(room, side.First()) is not { } v) continue;
            bool way = side.Any(to => to < 0 ? onWay : wayLinks.Contains((Math.Min(room.Index, to), Math.Max(room.Index, to))));
            var at = c + v * (Room / 2);
            var dot = Themed(new Ellipse { Width = 13, Height = 13, StrokeThickness = 2 }, Shape.FillProperty, way ? "Shell.Accent" : "Shell.TextSecondary");
            Themed(dot, Shape.StrokeProperty, "Shell.Background");
            Place(dot, at.X - 6.5, at.Y - 6.5, string.Join("\n", side.Select(to => DoorTip(tier, room, to))));
        }

        // What it holds, in a row over it: Izaro first.
        var marks = new List<(FrameworkElement Mark, string Heading, string Brush)>();
        if (room.Izaro > 0) marks.Add((Mark(PackIconKind.Skull, "Shell.Error", IconSize), L.Text("Izaro"), "Shell.Error"));
        foreach (var feature in room.Secrets.Select(s => s.Feature).OfType<LabFeature>().Distinct())
        {
            var (brush, icon) = Look(feature);
            marks.Add((Mark(icon, brush, IconSize), LabFeatures.Label(feature), brush));
        }
        double x = c.X - marks.Count * (IconSize + 2) / 2;
        foreach (var (mark, heading, brush) in marks)
        {
            mark.Tag = new Func<object>(() => RoomTip(room, (heading, brush)));
            mark.Cursor = System.Windows.Input.Cursors.Hand;
            // The icon's whole square takes the click, not only its strokes.
            ((Control)mark).Background = Brushes.Transparent;
            Place(mark, x, c.Y - Room / 2 - IconSize - 4, null);
            x += IconSize + 2;
        }
    }

    static string DoorTip(LabTier tier, LabRoom room, int to) =>
        to >= 0 && to < tier.Rooms.Count ? L.Text("Door to room {0}: {1}", tier.Rooms[to].Number, tier.Rooms[to].Name ?? "")
        : room.Index == 0 ? L.Text("Door to the Aspirants' Plaza") : L.Text("The way on to Izaro");

    /// <summary>A room's details: what was clicked first (an icon's name in its colour), then the room, its area, Izaro's
    /// fight and what else it holds.</summary>
    object RoomTip(LabRoom room, (string Text, string Brush)? heading)
    {
        var tip = new StackPanel { MaxWidth = 320 };
        if (heading is var (text, brush))
            tip.Children.Add(Themed(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) },
                TextBlock.ForegroundProperty, brush));
        tip.Children.Add(new TextBlock { Text = L.Text("Room {0}: {1}", room.Number, room.Name ?? L.Text("(no area)")), FontWeight = FontWeights.SemiBold });
        if (room.Area is { } area) tip.Children.Add(new TextBlock { Text = area, Opacity = 0.7 });
        if (room.Izaro > 0)
        {
            var mechanics = Tier?.IzaroFights().FirstOrDefault(f => f.Fight == room.Izaro).Mechanics ?? [];
            tip.Children.Add(new TextBlock { Text = mechanics.Count == 0 ? L.Text("Izaro, fight {0}", room.Izaro)
                : L.Text("Izaro, fight {0}: {1}", room.Izaro, string.Join(", ", mechanics)), Margin = new Thickness(0, 4, 0, 0) });
        }
        foreach (var feature in room.Secrets.Select(s => s.Feature).OfType<LabFeature>().Distinct())
            tip.Children.Add(new TextBlock { Text = LabFeatures.Label(feature) });
        return tip;
    }

    static PackIcon Mark(PackIconKind kind, string brush, double size) =>
        Themed(new PackIcon { Kind = kind, Width = size, Height = size }, Control.ForegroundProperty, brush);

    /// <summary>Puts an element on the map; with <paramref name="popup"/>, a click on it shows that text.</summary>
    void Place(FrameworkElement element, double x, double y, string? popup)
    {
        if (popup is not null)
        {
            element.Tag = popup;
            element.Cursor = System.Windows.Input.Cursors.Hand;
        }
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        Map.Children.Add(element);
    }

    static string TierName(LabTier tier) => tier.Tier switch
    {
        1 => L.Text("The Labyrinth"),
        2 => L.Text("The Cruel Labyrinth"),
        3 => L.Text("The Merciless Labyrinth"),
        4 => L.Text("The Eternal Labyrinth"),
        _ => tier.Name,
    };

    // poelab.com's notes: the shortest way, Izaro's fights, the gauntlets and puzzles, the Darkshrines, keys and doors.
    void WriteNotes(LabTier tier, int length)
    {
        Note(L.Text("Shortest way: {0} rooms", length), "Shell.Accent", 10);
        Heading(L.Text("Izaro"));
        var rooms = tier.Rooms.Where(r => r.Izaro > 0).ToDictionary(r => r.Izaro, r => r.Number);
        foreach (var (fight, mechanics) in tier.IzaroFights())
            Note(L.Text("Fight {0} (room {1}): {2}", fight, rooms.GetValueOrDefault(fight), mechanics.Count == 0 ? "—" : string.Join(", ", mechanics)));

        var trials = tier.Rooms.SelectMany(r => r.Secrets.Select(s => s.Feature).OfType<LabFeature>()
                .Where(f => f is LabFeature.TrapGauntlet or LabFeature.EscortGauntlet or LabFeature.Puzzle).Distinct().Select(f => (r.Number, f)))
            .OrderBy(t => t.Number).ToList();
        if (trials.Count > 0)
        {
            Heading(L.Text("Gauntlets and puzzles"));
            foreach (var (number, feature) in trials) Note($"{number} — {LabFeatures.Label(feature)}", Look(feature).Brush);
        }

        Heading(L.Text("In the rooms"));
        foreach (var feature in (LabFeature[])[LabFeature.Argus, LabFeature.Darkshrine, LabFeature.SilverKey, LabFeature.SilverCache,
                     LabFeature.GoldKey, LabFeature.GoldDoor, LabFeature.Reliquary, LabFeature.SecretPassage])
            if (tier.Holding(feature) is { Count: > 0 } numbers)
                Note($"{LabFeatures.Label(feature)}: {string.Join(", ", numbers)}", Look(feature).Brush);

        if (Marked(tier).Count > 0)
        {
            var clear = new Button { Content = L.Text("Clear marks"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
            clear.Click += (_, _) => { Marked(tier).Clear(); Draw(); };
            Notes.Children.Add(clear);
        }
        foreach (string text in (string[])[L.Text("Click a room, an icon or a door for its details, and a connection to mark it green."),
                     L.Text("Built by this checkout's Gateway from the client's data; nothing is downloaded.")])
        {
            var note = new TextBlock { Text = text, Margin = new Thickness(0, 14, 0, 0), FontSize = 11 };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Shell.TextSecondary");
            Notes.Children.Add(note);
        }
    }

    void Heading(string text) => Notes.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Lab.Heading") });

    void Note(string text, string brush = "Shell.Text", double top = 0, double size = 13)
    {
        var line = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 2), FontSize = size };
        line.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Notes.Children.Add(line);
    }

    // The view: fit, zoom at a point, move.
    void ApplyView() => MapView.Matrix = new Matrix(_scale, 0, 0, _scale, _offsetX, _offsetY);

    void Fit()
    {
        _fitted = true;
        if (Map.Width is not > 0 || MapHost.ActualWidth < 40 || MapHost.ActualHeight < 40) return;
        _scale = Math.Min(MapHost.ActualWidth / Map.Width, MapHost.ActualHeight / Map.Height);
        _offsetX = (MapHost.ActualWidth - Map.Width * _scale) / 2;
        _offsetY = (MapHost.ActualHeight - Map.Height * _scale) / 2;
        ApplyView();
    }

    void Zoom(double factor, Point at)
    {
        double scale = Math.Clamp(_scale * factor, 0.2, 8);
        _offsetX = at.X - (at.X - _offsetX) * scale / _scale;
        _offsetY = at.Y - (at.Y - _offsetY) * scale / _scale;
        _scale = scale;
        _fitted = false;
        ApplyView();
    }

    Point FrameCentre => new(MapHost.ActualWidth / 2, MapHost.ActualHeight / 2);

    void MapHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitted) Fit();
    }

    void MapWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        Zoom(Math.Exp(e.Delta / 600.0), e.GetPosition(MapHost));
        e.Handled = true;
    }

    void MapPress(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && Clicked(e.OriginalSource) is null) { Fit(); return; }
        _drag = e.GetPosition(MapHost);
        _moved = false;
        _pressed = e.OriginalSource;
        MapHost.CaptureMouse();
    }

    void MapDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag is not { } from || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var p = e.GetPosition(MapHost);
        if (!_moved && (p - from).Length < 4) return;
        _moved = true;
        _offsetX += p.X - from.X;
        _offsetY += p.Y - from.Y;
        _drag = p;
        _fitted = false;
        ApplyView();
    }

    // A click (no drag): a connection is marked or unmarked, anything else with details shows them.
    void MapRelease(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        bool click = _drag is not null && !_moved;
        _drag = null;
        MapHost.ReleaseMouseCapture();
        if (!click) return;
        switch (Clicked(_pressed))
        {
            case ValueTuple<int, int> link when Tier is { } tier:
                var marked = Marked(tier);
                if (!marked.Remove(link)) marked.Add(link);
                _popup.IsOpen = false;
                Draw();
                break;
            case Func<object> details: ShowPopup(details()); break;
            case string text: ShowPopup(new TextBlock { Text = text }); break;
            default: _popup.IsOpen = false; break;
        }
    }

    /// <summary>What a map element under the pointer stands for (its Tag, or a parent's), or null.</summary>
    object? Clicked(object? source)
    {
        for (var d = source as DependencyObject; d is not null && d != Map; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: { } tag }) return tag;
        return null;
    }

    HashSet<(int, int)> Marked(LabTier tier)
    {
        var key = (_day?.Seed ?? 0, tier.Tier);
        if (!_marked.TryGetValue(key, out var set)) _marked[key] = set = [];
        return set;
    }

    static Line HitLine(Point a, Point b, (int, int) link) => new()
    {
        X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Brushes.Transparent, StrokeThickness = 16, Tag = link,
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    void ShowPopup(object content)
    {
        _popup.IsOpen = false;
        var frame = new Border { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = content as UIElement ?? new TextBlock { Text = content.ToString() }, MaxWidth = 340 };
        Themed(frame, Border.BackgroundProperty, "Shell.Panel");
        Themed(frame, Border.BorderBrushProperty, "Shell.Border");
        Themed(frame, TextElement.ForegroundProperty, "Shell.Text");
        frame.SetResourceReference(TextElement.FontFamilyProperty, "AppFont");
        _popup.Child = frame;
        _popup.PlacementTarget = MapHost;
        _popup.IsOpen = true;
    }

    void ZoomInClicked(object sender, RoutedEventArgs e) => Zoom(1.25, FrameCentre);
    void ZoomOutClicked(object sender, RoutedEventArgs e) => Zoom(0.8, FrameCentre);
    void FitClicked(object sender, RoutedEventArgs e) => Fit();

    void BuildLegend()
    {
        Legend.Children.Clear();
        void Add(FrameworkElement mark, string text)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 4) };
            item.Children.Add(mark);
            item.Children.Add(new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Legend.Children.Add(item);
        }
        var way = new Border { Width = 18, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        way.SetResourceReference(Border.BackgroundProperty, "Shell.Accent");
        Add(way, L.Text("Shortest way"));
        Add(Themed(new Ellipse { Width = 10, Height = 10 }, Shape.FillProperty, "Shell.TextSecondary"), L.Text("Door"));
        Add(Mark(PackIconKind.Skull, "Shell.Error", 16), L.Text("Izaro"));
        foreach (var feature in Enum.GetValues<LabFeature>())
        {
            var (brush, icon) = Look(feature);
            Add(Mark(icon, brush, 16), LabFeatures.Label(feature));
        }
    }
}

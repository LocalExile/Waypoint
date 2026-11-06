using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections;
using System.Runtime.InteropServices;

namespace Waypoint;

public partial class MapViewerWindow : FormWindow
{
    readonly Shared _shared;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    readonly CancellationTokenSource _stop = new();
    readonly DispatcherTimer _seedNoticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    MapViewerHelper? _helper;
    Process? _process;
    ContextMenu? _markerChoices;
    string _baseUrl = "";
    MapData? _data;
    List<MapArea> _areas = [];
    ListCollectionView? _areaView;
    bool _updatingAreaSearch;
    bool _selectingFromCanvas;
    bool _fitWithMouse;
    bool _layersReady;
    readonly Stack<MapRequest> _history = new();
    MapRequest? _currentRequest;
    string? _destinationId;
    string? _markerId;
    readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    internal MapViewerWindow(Shared shared)
    {
        InitializeComponent(); Standalone = true; _shared = shared;
        _seedNoticeTimer.Tick += (_, _) => { _seedNoticeTimer.Stop(); SeedNotice.Visibility = Visibility.Collapsed; };
        var layers = _shared.Settings.MapLayers ?? new MapLayerSettings();
        WalkLayer.IsChecked = layers.Walkability;
        GridLayer.IsChecked = layers.Grid;
        ObjectLayer.IsChecked = layers.Markers;
        LinksLayer.IsChecked = layers.TeleportLinks;
        MonsterLayer.IsChecked = layers.MonsterPacks;
        EncounterLayer.IsChecked = layers.Encounters;
        InternalLayer.IsChecked = layers.InternalMarkers;
        AllTiles.IsChecked = layers.AllTiles;
        _layersReady = true;
        ApplyLayers();
        Areas.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(AreaSearchChanged));
        Generate.IsEnabled = false;
        Map.Inspected += value => Status.Text = value;
        Map.TileSelected += SelectTile;
        Map.MarkerSelected += SelectMarker;
        Map.MarkerChoicesRequested += ShowMarkerChoices;
        Loaded += async (_, _) => await LoadAreasAsync();
        Closed += (_, _) => { if (_markerChoices is not null) _markerChoices.IsOpen = false; _seedNoticeTimer.Stop(); _stop.Cancel(); StopHelper(); _http.Dispose(); };
    }

    void SetBusy(bool busy, string message = "")
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message;
        Areas.IsEnabled = !busy;
        AreaSort.IsEnabled = !busy;
        SeedInputs.IsEnabled = !busy;
        Map.IsEnabled = !busy;
        DisplayOptions.IsEnabled = !busy;
        CopyMapDetails.IsEnabled = !busy && _data is not null;
        if (busy && _markerChoices is not null) _markerChoices.IsOpen = false;
        BackMap.IsEnabled = !busy && _history.Count > 0;
        OpenDestination.IsEnabled = !busy && _destinationId is not null && _areas.Any(a => a.Id.Equals(_destinationId, StringComparison.OrdinalIgnoreCase));
    }

    async Task LoadAreasAsync()
    {
        try
        {
            SetBusy(true, L.Text("Loading areas …"));
            _areas = await Task.Run(async () =>
            {
                await using var stream = File.OpenRead(Path.Combine("data", "tables", "WorldAreas.json"));
                var all = await JsonSerializer.DeserializeAsync<List<MapArea>>(stream, _json, _stop.Token) ?? [];
                return all.Where(a => a.TopologiesKeys is { Length: > 0 }).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();
            }, _stop.Token);
            _areaView = new ListCollectionView(_areas);
            Areas.ItemsSource = _areaView;
            ApplyAreaSort();
            Generate.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) Status.Text = e.Message; }
        finally { if (!_stop.IsCancellationRequested) SetBusy(false); }
    }

    void AreaSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingAreaSearch || _areaView is null || !Areas.IsKeyboardFocusWithin || e.OriginalSource is not TextBox input) return;
        string query = input.Text;
        if (Areas.SelectedItem is MapArea selected && query == selected.Display) return;
        int caret = input.CaretIndex;
        _updatingAreaSearch = true;
        try
        {
            _areaView.Filter = o => o is MapArea area && MapAreaSearch.Matches(area.Id, area.Name, query);
            if (input.Text != query) input.Text = query;
            input.CaretIndex = Math.Min(caret, input.Text.Length);
        }
        finally { _updatingAreaSearch = false; }
        Areas.IsDropDownOpen = true;
    }

    void AreaSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAreaSearch || Areas.SelectedItem is not MapArea area || _areaView is null) return;
        CommitArea(area);
    }

    void CommitArea(MapArea area, bool closeDropdown = false)
    {
        if (_areaView is null) return;
        _updatingAreaSearch = true;
        try
        {
            if (closeDropdown || !Areas.IsDropDownOpen) _areaView.Filter = null;
            Areas.SelectedItem = area;
            Areas.Text = area.Display;
            if (Areas.Template.FindName("PART_EditableTextBox", Areas) is TextBox input) input.Text = area.Display;
            if (closeDropdown) Areas.IsDropDownOpen = false;
        }
        finally { _updatingAreaSearch = false; }
    }

    void AreaDropDownClosed(object? sender, EventArgs e)
    {
        if (_updatingAreaSearch || _areaView is null) return;
        _updatingAreaSearch = true;
        try { _areaView.Filter = null; }
        finally { _updatingAreaSearch = false; }
    }

    void AreaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _areaView is null) return;
        string query = Areas.Template.FindName("PART_EditableTextBox", Areas) is TextBox input ? input.Text : Areas.Text;
        var area = Areas.SelectedItem is MapArea selected && query == selected.Display
            ? selected : MapAreaSearch.Resolve(_areaView.Cast<MapArea>(), query);
        if (area is null) Status.Text = L.Text("Select an area.");
        else { CommitArea(area, closeDropdown: true); Status.Text = ""; }
        e.Handled = true;
    }

    void AreaSortChanged(object sender, SelectionChangedEventArgs e) => ApplyAreaSort();
    void ApplyAreaSort()
    {
        if (_areaView is null || AreaSort is null) return;
        _areaView.CustomSort = new MapAreaComparer(AreaSort.SelectedIndex == 1);
    }

    void RandomSeed(object sender, RoutedEventArgs e) => Seed.Text = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();

    void StopHelper()
    {
        var process = _process;
        _process = null;
        try
        {
            if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(2000); }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process?.Dispose(); _helper?.Dispose(); _helper = null; }
    }

    async Task StartAsync()
    {
        try
        {
            bool ran = await _shared.RunExclusiveAsync(L.Text("Map viewer"), async () =>
            {
                StopHelper();
                _helper = await MapViewerHelper.BuildAsync(Environment.CurrentDirectory, SettingsStore.DataDirectory, _stop.Token);
            });
            if (!ran) throw new InvalidOperationException(L.Text("Something else is using LocalExile. Try again when it is done."));
            _stop.Token.ThrowIfCancellationRequested();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            _baseUrl = $"http://127.0.0.1:{port}";
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory };
            start.ArgumentList.Add(_helper!.AssemblyPath); start.Environment["MAP_VIEWER_PORT"] = port.ToString();
            _process = Process.Start(start) ?? throw new InvalidOperationException("map viewer process");
            bool ready = false;
            for (int i = 0; i < 100 && !ready; i++)
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new InvalidOperationException(L.Text("Map viewer failed to start."));
                try { await _http.GetStringAsync(_baseUrl + "/api/areas", _stop.Token); ready = true; }
                catch (HttpRequestException) { await Task.Delay(100, _stop.Token); }
            }
            if (!ready) throw new InvalidOperationException(L.Text("Map viewer failed to start."));
        }
        catch { StopHelper(); throw; }
    }

    async void GenerateClicked(object sender, RoutedEventArgs e) => await GenerateAsync();
    async void GenerateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Generate.IsEnabled) return;
        e.Handled = true;
        await GenerateAsync();
    }
    async Task<bool> GenerateAsync(bool preserveHistory = false)
    {
        string id = Areas.SelectedItem is MapArea a && Areas.Text == a.Display ? a.Id : Areas.Text.Split(' ', 2)[0];
        if (!_areas.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) { Status.Text = L.Text("Select an area."); return false; }
        if (!uint.TryParse(Seed.Text, out uint seed) || (!string.IsNullOrWhiteSpace(Level.Text) && (!int.TryParse(Level.Text, out int level) || level is < 1 or > 100)))
        { Status.Text = L.Text("Enter a valid seed and area level."); return false; }
        var request = new MapRequest(id, Seed.Text, Level.Text);
        Generate.IsEnabled = false; Status.Text = ""; SetBusy(true, L.Text("Generating map …"));
        try
        {
            if (_process is null || _process.HasExited) await StartAsync();
            string url = $"{_baseUrl}/api/map?id={Uri.EscapeDataString(id)}&seed={seed}";
            if (!string.IsNullOrWhiteSpace(Level.Text)) url += "&level=" + Level.Text;
            using var response = await _http.GetAsync(url, _stop.Token);
            string body = await response.Content.ReadAsStringAsync(_stop.Token);
            if (!response.IsSuccessStatusCode) { using var error = JsonDocument.Parse(body); throw new InvalidOperationException(error.RootElement.GetProperty("error").GetString()); }
            _data = JsonSerializer.Deserialize<MapData>(body, _json) ?? throw new InvalidDataException();
            _data = _data with { CheckoutHead = _helper?.CheckoutHead };
            uint actualSeed = _data.Seed ?? seed;
            Seed.Text = actualSeed.ToString();
            Map.SetMap(_data); TitleText.Text = $"{_data.Area} · {_data.Id} · {actualSeed}";
            _currentRequest = request with { Seed = Seed.Text, Level = _data.Level?.ToString() ?? request.Level };
            if (!preserveHistory) _history.Clear();
            HideMarker();
            _seedNoticeTimer.Stop();
            SeedNotice.Visibility = actualSeed != seed ? Visibility.Visible : Visibility.Collapsed;
            if (actualSeed != seed)
            {
                SeedNotice.Text = L.Text("Seed {0} was replaced with {1}.", seed, actualSeed);
                _seedNoticeTimer.Start();
            }
            Status.Text = string.Join("; ", _data.Warnings); UpdateContentNotice(); UpdateList();
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) Status.Text = e.Message; }
        finally { if (!_stop.IsCancellationRequested) { Generate.IsEnabled = true; SetBusy(false); } }
        return false;
    }

    void FilterChanged(object sender, RoutedEventArgs e) => UpdateList();
    void UpdateList()
    {
        if (_data is null || Filter is null || Tabs is null || Entries is null) return;
        IEnumerable<MapListItem> entries = Tabs.SelectedIndex == 0
            ? _data.Catalog.Where(t => AllTiles.IsChecked || (t.Count > 0 && !MapContent.IsBlankTile(t.Path))).Select(t => new MapListItem($"{Path.GetFileName(t.Path)}  ({t.Count})", t.Path, null, null))
            : Map.VisibleMarkers.Select(m => new MapListItem(MapMarkers.Caption(m), null, m.X, m.Y, m.Key));
        Entries.ItemsSource = entries.Where(x => x.Label.Contains(Filter.Text, StringComparison.OrdinalIgnoreCase) || (x.Path?.Contains(Filter.Text, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (Tabs.SelectedIndex == 1 && Map.SelectedMarker is { } key)
            Entries.SelectedItem = Entries.Items.OfType<MapListItem>().FirstOrDefault(i => i.Marker == key);
    }
    void EntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (Entries.SelectedItem is not MapListItem item) return;
        Map.Highlight = item.Path;
        Map.SelectedMarker = item.Marker;
        if (item.Marker is { } marker) ShowMarker(marker); else HideMarker();
        if (!_selectingFromCanvas && item.X is int x && item.Y is int y) Map.CenterOn(x, y);
        Map.InvalidateVisual(); Status.Text = item.Path ?? $"{item.Label} ({item.X}, {item.Y})";
    }
    void ShowMarkerChoices(IReadOnlyList<MapMarker> markers, Point at)
    {
        if (_markerChoices is not null) _markerChoices.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = Map, Placement = PlacementMode.RelativePoint,
            HorizontalOffset = at.X, VerticalOffset = at.Y, MaxHeight = 360 };
        menu.Items.Add(new MenuItem { Header = L.Text("Choose a marker ({0})", markers.Count), IsEnabled = false });
        foreach (var marker in markers)
        {
            var item = new MenuItem {
                Header = new TextBlock { Text = MapMarkers.Caption(marker), MaxWidth = 330, TextWrapping = TextWrapping.Wrap },
                ToolTip = $"{marker.Label} ({marker.X}, {marker.Y})"
            };
            item.Click += (_, _) => Map.SelectMarker(marker);
            menu.Items.Add(item);
        }
        _markerChoices = menu;
        menu.IsOpen = true;
    }

    void SelectMarker(MapMarkerKey key)
    {
        ShowMarker(key);
        _selectingFromCanvas = true;
        try
        {
            Tabs.SelectedIndex = 1;
            var item = Entries.Items.OfType<MapListItem>().FirstOrDefault(x => x.Marker == key);
            if (item is null && !string.IsNullOrEmpty(Filter.Text))
            {
                Filter.Text = "";
                item = Entries.Items.OfType<MapListItem>().FirstOrDefault(x => x.Marker == key);
            }
            if (item is null) return;
            Entries.SelectedItem = item;
            Entries.ScrollIntoView(item);
        }
        finally { _selectingFromCanvas = false; }
    }
    void SelectTile(string? path)
    {
        HideMarker();
        if (path is null) { Entries.SelectedItem = null; return; }
        Tabs.SelectedIndex = 0;
        var item = Entries.Items.OfType<MapListItem>().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null && !string.IsNullOrEmpty(Filter.Text))
        {
            Filter.Text = "";
            item = Entries.Items.OfType<MapListItem>().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        if (item is null) return;
        Entries.SelectedItem = item;
        Entries.ScrollIntoView(item);
    }
    void FitMouseDown(object sender, MouseButtonEventArgs e) => _fitWithMouse = true;
    void FitKeyDown(object sender, KeyEventArgs e) => _fitWithMouse = false;
    void FitClicked(object sender, RoutedEventArgs e)
    {
        Map.Fit();
        if (_fitWithMouse) Keyboard.ClearFocus();
        _fitWithMouse = false;
    }
    MapLayerSettings LayerChoices => new(WalkLayer.IsChecked, GridLayer.IsChecked, ObjectLayer.IsChecked, LinksLayer.IsChecked,
        MonsterLayer.IsChecked, EncounterLayer.IsChecked, InternalLayer.IsChecked, AllTiles.IsChecked);

    void LayersChanged(object sender, RoutedEventArgs e)
    {
        if (!_layersReady) return;
        ApplyLayers();
        _shared.Settings.MapLayers = LayerChoices;
        _shared.Settings.Save();
    }

    void ApplyLayers()
    {
        if (_markerChoices is not null) _markerChoices.IsOpen = false;
        Map.SetLayers(LayerChoices);
        if (Map.SelectedMarker is null) HideMarker();
        else ShowMarker(Map.SelectedMarker.Value);
        MonsterLegend.Visibility = MonsterLayer.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        EncounterLegend.Visibility = EncounterLayer.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        UpdateContentNotice(); UpdateList();
    }

    void UpdateContentNotice()
    {
        var messages = new List<string>();
        if (_data is not null && (MonsterLayer.IsChecked || EncounterLayer.IsChecked))
        {
            if (MonsterLayer.IsChecked) messages.Add(L.Text("Monster pack anchors: {0}", _data.PackAnchors?.Count ?? 0));
            if (EncounterLayer.IsChecked) messages.Add(L.Text("Encounter anchors: {0}", _data.Encounters?.Count ?? 0));
            messages.Add(L.Text("Authored anchors only; actual spawns may differ. Scattered monsters are not shown."));
            if (MonsterLayer.IsChecked && _data.PackWarnings is { Count: > 0 })
                messages.Add(L.Text("Monster pack data unavailable or incomplete: {0}", string.Join("; ", _data.PackWarnings)));
        }
        ContentNotice.Text = string.Join(" · ", messages);
        ContentNotice.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void DisplayOptionsClicked(object sender, RoutedEventArgs e)
    {
        DisplayOptions.ContextMenu.PlacementTarget = DisplayOptions;
        DisplayOptions.ContextMenu.Placement = PlacementMode.Bottom;
        DisplayOptions.ContextMenu.IsOpen = true;
    }

    void CopyMapDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        try { Clipboard.SetText(MapDetails.Format(_data)); Status.Text = L.Text("Map details copied."); }
        catch (ExternalException) { Status.Text = L.Text("Could not copy to clipboard."); }
    }

    void HideMarker() { MarkerPanel.Visibility = Visibility.Collapsed; _destinationId = null; }
    void CloseMarkerClicked(object sender, RoutedEventArgs e) => HideMarker();
    void CopyMarkerIdClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_markerId)) return;
        try { Clipboard.SetText(_markerId); }
        catch (ExternalException) { Status.Text = L.Text("Could not copy to clipboard."); }
    }

    void ShowMarker(MapMarkerKey key)
    {
        if (_data is null) return;
        var marker = Map.VisibleMarkers.FirstOrDefault(m => m.Key == key);
        if (marker is null) return;
        var details = new List<string>();
        _markerId = null;
        string heading;
        switch (key.Kind)
        {
            case "object":
                var obj = _data.Objects[key.Index];
                heading = obj.Type.Equals("Metadata/MiscellaneousObjects/Waypoint", StringComparison.OrdinalIgnoreCase) ? L.Text("Waypoint")
                    : obj.Type.Equals("Metadata/MiscellaneousObjects/CraftRecipeUnlockMarker", StringComparison.OrdinalIgnoreCase) ? L.Text("Crafting unlock") : Path.GetFileName(obj.Type);
                _markerId = obj.Type;
                details.Add(obj.Type);
                if (!string.IsNullOrWhiteSpace(obj.Model)) details.Add(obj.Model);
                break;
            case "point":
                var point = _data.Points[key.Index];
                if (point.Bosses is { Count: 1 }) _markerId = point.Bosses[0].Id;
                heading = point.Display;
                if (point.Display != point.Name) details.Add(point.Name);
                if (point.Bosses is not null) details.AddRange(point.Bosses.Where(b => !string.IsNullOrWhiteSpace(b.Id)).Select(b => b.Id));
                if (!string.IsNullOrWhiteSpace(point.Room)) details.Add(point.Room);
                break;
            case "pack":
                var anchor = _data.PackAnchors![key.Index];
                heading = MapPacks.Label(_data, anchor);
                details.AddRange(MapPacks.Describe(_data, anchor, InternalLayer.IsChecked));
                if (anchor.PackIds.Count == 1) _markerId = anchor.PackIds[0];
                break;
            case "encounter":
                var encounter = _data.Encounters![key.Index];
                heading = MapEncounters.Label(encounter.Kind);
                details.Add(encounter.IsObject ? L.Text("Placed encounter object; this is not live server state.")
                    : L.Text("Encounter anchor; this does not guarantee an encounter will spawn."));
                if (encounter.IsObject) { _markerId = encounter.Name; details.Add(encounter.Name); }
                else if (InternalLayer.IsChecked) details.Add(encounter.Name);
                if (InternalLayer.IsChecked && !string.IsNullOrWhiteSpace(encounter.Room)) details.Add(encounter.Room);
                break;
            case "exit":
                var exit = _data.Exits[key.Index];
                heading = exit.Kind == "Teleport" ? L.Text("Area teleport") : L.Text("Entrance and exit");
                if (!string.IsNullOrWhiteSpace(exit.Destination)) details.Add($"{exit.Destination} ({exit.DestinationId})");
                else if (!string.IsNullOrWhiteSpace(exit.Label)) details.Add(exit.Label);
                if (exit.TargetX is int tx && exit.TargetY is int ty) details.Add(L.Text("Target: {0}, {1}", tx, ty));
                if (!exit.Enabled) details.Add(L.Text("Disabled"));
                break;
            default:
                var entrance = _data.Entrances[key.Index];
                heading = L.Text("Entrance {0}", entrance.Number);
                if (!string.IsNullOrWhiteSpace(entrance.Destination)) details.Add($"{entrance.Destination} ({entrance.DestinationId})");
                if (!entrance.Enabled) details.Add(L.Text("Arrival only"));
                break;
        }
        _destinationId = MapNavigation.DestinationId(_data, key);
        MarkerHeading.Text = heading;
        MarkerCoordinates.Text = L.Text("Coordinates: {0}, {1}", marker.X, marker.Y);
        MarkerDetails.Text = string.Join("\n", details);
        CopyMarkerId.Visibility = string.IsNullOrWhiteSpace(_markerId) ? Visibility.Collapsed : Visibility.Visible;
        OpenDestination.Visibility = string.IsNullOrWhiteSpace(_destinationId) ? Visibility.Collapsed : Visibility.Visible;
        OpenDestination.IsEnabled = _destinationId is not null && _areas.Any(a => a.Id.Equals(_destinationId, StringComparison.OrdinalIgnoreCase));
        MarkerPanel.Visibility = Visibility.Visible;
    }

    async void OpenDestinationClicked(object sender, RoutedEventArgs e)
    {
        if (_destinationId is null || _currentRequest is null || !Generate.IsEnabled) return;
        var previous = _currentRequest;
        if (await OpenRequestAsync(new MapRequest(_destinationId, previous.Seed, "")))
        {
            _history.Push(previous);
            BackMap.IsEnabled = true;
        }
    }

    async void BackMapClicked(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0 || !Generate.IsEnabled) return;
        if (await OpenRequestAsync(_history.Peek())) _history.Pop();
        BackMap.IsEnabled = _history.Count > 0;
    }

    async Task<bool> OpenRequestAsync(MapRequest request)
    {
        var area = _areas.FirstOrDefault(a => a.Id.Equals(request.AreaId, StringComparison.OrdinalIgnoreCase));
        if (area is null) return false;
        var previous = _currentRequest;
        CommitArea(area, closeDropdown: true); Seed.Text = request.Seed; Level.Text = request.Level;
        if (await GenerateAsync(preserveHistory: true)) return true;
        if (previous is not null && _areas.FirstOrDefault(a => a.Id.Equals(previous.AreaId, StringComparison.OrdinalIgnoreCase)) is { } old)
        { CommitArea(old, closeDropdown: true); Seed.Text = previous.Seed; Level.Text = previous.Level; }
        return false;
    }
}

record MapArea(string Id, string Name, int[]? TopologiesKeys = null) { public string Display => $"{Name}  ({Id})"; }
sealed class MapAreaComparer(bool byName) : IComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string left, string right);
    public int Compare(object? x, object? y)
    {
        if (x is not MapArea left || y is not MapArea right) return 0;
        int name = byName ? StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name) : 0;
        return name != 0 ? name : StrCmpLogicalW(left.Id, right.Id);
    }
}
static class MapAreaSearch
{
    public static bool Matches(string id, string name, string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .All(word => id.Contains(word, StringComparison.OrdinalIgnoreCase) || name.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static MapArea? Resolve(IEnumerable<MapArea> areas, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var candidates = areas.ToList();
        return candidates.FirstOrDefault(a => a.Id.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || a.Display.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(a => Matches(a.Id, a.Name, query));
    }
}
record MapListItem(string Label, string? Path, int? X, int? Y, MapMarkerKey? Marker = null);
record MapRequest(string AreaId, string Seed, string Level);
record MapTile(int X, int Y, string Path, int Orientation, int Height);
record MapCatalog(string Path, int Count);
record MapExit(int X, int Y, string Kind, int Number, string? Destination, string? Label,
    string? DestinationId = null, int? TargetX = null, int? TargetY = null, bool Enabled = true);
record MapEntrance(int X, int Y, int Number, string? Destination = null, string? DestinationId = null, bool Enabled = true);
record MapObject(int X, int Y, string Type, string? Model = null);
record MapBoss(string Id, string Name);
record MapPoint(int X, int Y, string Name, string? Room = null, IReadOnlyList<MapBoss>? Bosses = null, bool BossesAreCandidates = false)
{
    public string Display
    {
        get
        {
            string names = string.Join(" / ", (Bosses ?? []).Select(b => string.IsNullOrWhiteSpace(b.Name) ? b.Id : b.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
            return names.Length == 0 ? Name : BossesAreCandidates ? L.Text("Possible: {0}", names) : names;
        }
    }
}
record MapData(string Area, string Id, int Width, int Height, int WalkWidth, int WalkHeight, byte[] Walk,
    List<MapTile> Tiles, List<MapCatalog> Catalog, List<MapExit> Exits, List<MapEntrance> Entrances, List<MapObject> Objects,
    List<MapPoint> Points, List<string> Warnings, uint? Seed = null, uint? RequestedSeed = null, int? Level = null,
    LocalRepositoryHead? CheckoutHead = null, List<MapPackAnchor>? PackAnchors = null, List<MapSpawnPack>? MonsterPacks = null,
    List<MapEncounterAnchor>? Encounters = null, List<string>? PackWarnings = null);

static class MapNavigation
{
    public static string? DestinationId(MapData map, MapMarkerKey key) => key switch
    {
        { Kind: "exit", Index: >= 0 } when key.Index < map.Exits.Count && map.Exits[key.Index].Kind == "Entrance" => map.Exits[key.Index].DestinationId,
        { Kind: "entrance", Index: >= 0 } when key.Index < map.Entrances.Count => map.Entrances[key.Index].DestinationId,
        _ => null
    };

    public static IEnumerable<(int X, int Y, int TargetX, int TargetY)> TeleportLinks(MapData map) => map.Exits
        .Where(e => e.Kind == "Teleport" && e.TargetX.HasValue && e.TargetY.HasValue)
        .Select(e => (e.X, e.Y, e.TargetX!.Value, e.TargetY!.Value));
}

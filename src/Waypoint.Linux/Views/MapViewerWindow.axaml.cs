using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>The map viewer: the Linux copy of src/Waypoint/MapViewerWindow.xaml.cs, in Avalonia. It builds the same helper
/// against the checkout's generator (<see cref="MapViewerHelper"/>) and asks it for each map.</summary>
partial class MapViewerWindow : FormWindow
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
    bool _updatingAreaSearch;
    bool _selectingFromCanvas;
    bool _layersReady;
    readonly Stack<MapRequest> _history = new();
    MapRequest? _currentRequest;
    string? _destinationId;
    string? _markerId;
    readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    internal MapViewerWindow(Shared shared)
    {
        InitializeComponent(); ShowInTaskbar = true; _shared = shared;
        _seedNoticeTimer.Tick += (_, _) => { _seedNoticeTimer.Stop(); SeedNotice.IsVisible = false; };
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
        Areas.ItemFilter = (query, item) => item is MapArea area && (string.IsNullOrWhiteSpace(query) || query == area.Display || MapAreaSearch.Matches(area.Id, area.Name, query));
        Generate.IsEnabled = false;
        Map.Inspected += value => Status.Text = value;
        Map.TileSelected += SelectTile;
        Map.MarkerSelected += SelectMarker;
        Map.MarkerChoicesRequested += ShowMarkerChoices;
        Opened += async (_, _) => await LoadAreasAsync();
        Closed += (_, _) => { _markerChoices?.Close(); _seedNoticeTimer.Stop(); _stop.Cancel(); StopHelper(); _http.Dispose(); };
    }

    void SetBusy(bool busy, string message = "")
    {
        Busy.IsVisible = busy;
        BusyText.Text = message;
        Areas.IsEnabled = !busy;
        AreaSort.IsEnabled = !busy;
        SeedInputs.IsEnabled = !busy;
        Map.IsEnabled = !busy;
        DisplayOptions.IsEnabled = !busy;
        CopyMapDetails.IsEnabled = !busy && _data is not null;
        if (busy) _markerChoices?.Close();
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
            ApplyAreaSort();
            Generate.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) Status.Text = e.Message; }
        finally { if (!_stop.IsCancellationRequested) SetBusy(false); }
    }

    void AreaSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingAreaSearch || Areas.SelectedItem is not MapArea area) return;
        CommitArea(area);
    }

    void CommitArea(MapArea area, bool closeDropdown = false)
    {
        _updatingAreaSearch = true;
        try
        {
            Areas.SelectedItem = area;
            Areas.Text = area.Display;
            if (closeDropdown) Areas.IsDropDownOpen = false;
        }
        finally { _updatingAreaSearch = false; }
    }

    void AreaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string query = Areas.Text ?? "";
        var area = Areas.SelectedItem is MapArea selected && query == selected.Display
            ? selected : MapAreaSearch.Resolve(_areas, query);
        if (area is null) Status.Text = L.Text("Select an area.");
        else { CommitArea(area, closeDropdown: true); Status.Text = ""; }
        e.Handled = true;
    }

    void AreaSortChanged(object? sender, SelectionChangedEventArgs e) => ApplyAreaSort();
    void ApplyAreaSort()
    {
        if (AreaSort is null || _areas.Count == 0) return;
        var selected = Areas.SelectedItem;
        _updatingAreaSearch = true;
        try
        {
            Areas.ItemsSource = _areas.Order(new MapAreaComparer(AreaSort.SelectedIndex == 1)).ToList();
            Areas.SelectedItem = selected;
        }
        finally { _updatingAreaSearch = false; }
    }

    void RandomSeed(object? sender, RoutedEventArgs e) => Seed.Text = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString();

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

    async void GenerateClicked(object? sender, RoutedEventArgs e) => await GenerateAsync();
    async void GenerateKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Generate.IsEnabled) return;
        e.Handled = true;
        await GenerateAsync();
    }
    async Task<bool> GenerateAsync(bool preserveHistory = false)
    {
        string text = Areas.Text ?? "";
        string id = Areas.SelectedItem is MapArea a && text == a.Display ? a.Id : text.Split(' ', 2)[0];
        if (!_areas.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) { Status.Text = L.Text("Select an area."); return false; }
        if (!uint.TryParse(Seed.Text, out uint seed) || (!string.IsNullOrWhiteSpace(Level.Text) && (!int.TryParse(Level.Text, out int level) || level is < 1 or > 100)))
        { Status.Text = L.Text("Enter a valid seed and area level."); return false; }
        var request = new MapRequest(id, Seed.Text ?? "", Level.Text ?? "");
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
            SeedNotice.IsVisible = actualSeed != seed;
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

    void FilterChanged(object? sender, RoutedEventArgs e) => UpdateList();
    void UpdateList()
    {
        if (_data is null || Filter is null || Tabs is null || Entries is null) return;
        IEnumerable<MapListItem> entries = Tabs.SelectedIndex == 0
            ? _data.Catalog.Where(t => AllTiles.IsChecked == true || (t.Count > 0 && !MapContent.IsBlankTile(t.Path))).Select(t => new MapListItem($"{Path.GetFileName(t.Path)}  ({t.Count})", t.Path, null, null))
            : Map.VisibleMarkers.Select(m => new MapListItem(MapMarkers.Caption(m), null, m.X, m.Y, m.Key));
        string filter = Filter.Text ?? "";
        Entries.ItemsSource = entries.Where(x => x.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || (x.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (Tabs.SelectedIndex == 1 && Map.SelectedMarker is { } key)
            Entries.SelectedItem = Entries.Items.OfType<MapListItem>().FirstOrDefault(i => i.Marker == key);
    }
    void EntrySelected(object? sender, SelectionChangedEventArgs e)
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
        _markerChoices?.Close();
        var menu = new ContextMenu { Placement = PlacementMode.Pointer, MaxHeight = 360 };
        menu.Items.Add(new MenuItem { Header = L.Text("Choose a marker ({0})", markers.Count), IsEnabled = false });
        foreach (var marker in markers)
        {
            var item = new MenuItem { Header = new TextBlock { Text = MapMarkers.Caption(marker), MaxWidth = 330, TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
            ToolTip.SetTip(item, $"{marker.Label} ({marker.X}, {marker.Y})");
            item.Click += (_, _) => Map.SelectMarker(marker);
            menu.Items.Add(item);
        }
        _markerChoices = menu;
        menu.Open(Map);
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
                UpdateList();
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
            UpdateList();
            item = Entries.Items.OfType<MapListItem>().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        if (item is null) return;
        Entries.SelectedItem = item;
        Entries.ScrollIntoView(item);
    }
    void FitClicked(object? sender, RoutedEventArgs e) => Map.Fit();
    MapLayerSettings LayerChoices => new(WalkLayer.IsChecked == true, GridLayer.IsChecked == true, ObjectLayer.IsChecked == true, LinksLayer.IsChecked == true,
        MonsterLayer.IsChecked == true, EncounterLayer.IsChecked == true, InternalLayer.IsChecked == true, AllTiles.IsChecked == true);

    void LayersChanged(object? sender, RoutedEventArgs e)
    {
        if (!_layersReady) return;
        ApplyLayers();
        _shared.Settings.MapLayers = LayerChoices;
        _shared.Settings.Save();
    }

    void ApplyLayers()
    {
        _markerChoices?.Close();
        Map.SetLayers(LayerChoices);
        if (Map.SelectedMarker is null) HideMarker();
        else ShowMarker(Map.SelectedMarker.Value);
        MonsterLegend.IsVisible = MonsterLayer.IsChecked == true;
        EncounterLegend.IsVisible = EncounterLayer.IsChecked == true;
        UpdateContentNotice(); UpdateList();
    }

    void UpdateContentNotice()
    {
        var messages = new List<string>();
        if (_data is not null && (MonsterLayer.IsChecked == true || EncounterLayer.IsChecked == true))
        {
            if (MonsterLayer.IsChecked == true) messages.Add(L.Text("Monster pack anchors: {0}", _data.PackAnchors?.Count ?? 0));
            if (EncounterLayer.IsChecked == true) messages.Add(L.Text("Encounter anchors: {0}", _data.Encounters?.Count ?? 0));
            messages.Add(L.Text("Authored anchors only; actual spawns may differ. Scattered monsters are not shown."));
            if (MonsterLayer.IsChecked == true && _data.PackWarnings is { Count: > 0 })
                messages.Add(L.Text("Monster pack data unavailable or incomplete: {0}", string.Join("; ", _data.PackWarnings)));
        }
        ContentNotice.Text = string.Join(" · ", messages);
        ContentNotice.IsVisible = messages.Count > 0;
    }

    async void CopyMapDetailsClicked(object? sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        Status.Text = await ViewTools.Copy(this, MapDetails.Format(_data)) ? L.Text("Map details copied.") : L.Text("Could not copy to clipboard.");
    }

    void HideMarker() { MarkerPanel.IsVisible = false; _destinationId = null; }
    void CloseMarkerClicked(object? sender, RoutedEventArgs e) => HideMarker();
    async void CopyMarkerIdClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_markerId)) return;
        if (!await ViewTools.Copy(this, _markerId)) Status.Text = L.Text("Could not copy to clipboard.");
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
                details.AddRange(MapPacks.Describe(_data, anchor, InternalLayer.IsChecked == true));
                if (anchor.PackIds.Count == 1) _markerId = anchor.PackIds[0];
                break;
            case "encounter":
                var encounter = _data.Encounters![key.Index];
                heading = MapEncounters.Label(encounter.Kind);
                details.Add(encounter.IsObject ? L.Text("Placed encounter object; this is not live server state.")
                    : L.Text("Encounter anchor; this does not guarantee an encounter will spawn."));
                if (encounter.IsObject) { _markerId = encounter.Name; details.Add(encounter.Name); }
                else if (InternalLayer.IsChecked == true) details.Add(encounter.Name);
                if (InternalLayer.IsChecked == true && !string.IsNullOrWhiteSpace(encounter.Room)) details.Add(encounter.Room);
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
        CopyMarkerId.IsVisible = !string.IsNullOrWhiteSpace(_markerId);
        OpenDestination.IsVisible = !string.IsNullOrWhiteSpace(_destinationId);
        OpenDestination.IsEnabled = _destinationId is not null && _areas.Any(a => a.Id.Equals(_destinationId, StringComparison.OrdinalIgnoreCase));
        MarkerPanel.IsVisible = true;
    }

    async void OpenDestinationClicked(object? sender, RoutedEventArgs e)
    {
        if (_destinationId is null || _currentRequest is null || !Generate.IsEnabled) return;
        var previous = _currentRequest;
        if (await OpenRequestAsync(new MapRequest(_destinationId, previous.Seed, "")))
        {
            _history.Push(previous);
            BackMap.IsEnabled = true;
        }
    }

    async void BackMapClicked(object? sender, RoutedEventArgs e)
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

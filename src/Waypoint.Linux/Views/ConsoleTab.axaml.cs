using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>
/// Dev commands for the character in game. "/" opens the command list (the built Gateway's, with each usage), pinned lines
/// are buttons, ↑↓ is the history. On Windows a checkout's line goes through the client control bridge, which needs Frida
/// and does not work under Proton yet, so here every line is copied for the game's chat, as a release's is on Windows.
/// </summary>
partial class ConsoleTab : UserControl
{
    Shared _shared = null!;
    CommandCatalog _catalog = new([]);
    // Argument values by kind, loaded from the Gateway the first time a line needs them.
    readonly Dictionary<string, IReadOnlyList<ArgumentValue>> _values = [];
    readonly HashSet<string> _loading = [];
    CompletionResult _completion = CompletionResult.None;
    int _historyIndex;
    /// <summary>The line ↑↓ brought back. It opens no list, or ↑↓ would stop going through the history: TextChanged comes
    /// after Text is set here, not inside it as on Windows, so closing the list in History alone is not enough.</summary>
    string? _recalled;
    const int OutputMax = 300_000;
    static readonly FontFamily Mono = new("monospace");

    /// <summary>The Gateway build the last failed load asked: typing "/" does not ask that build again on every key.</summary>
    DateTime? _failedBuild;
    /// <summary>A release's Gateway is a Windows program, and its list could not be read.</summary>
    bool _noReleaseList;

    public ConsoleTab()
    {
        InitializeComponent();
        // Before the box itself takes the keys: ↑↓, Tab and Enter belong to the list and the history.
        Input.AddHandler(KeyDownEvent, InputKey, RoutingStrategies.Tunnel);
        Suggestions.AddHandler(PointerReleasedEvent, SuggestionClicked, RoutingStrategies.Tunnel);
        ShowLanguage();
    }

    /// <summary>The texts Windows gives through {l:Loc}. A line is copied, for the game's chat.</summary>
    void ShowLanguage()
    {
        ReloadButton.Content = L.Text("Reload commands");
        ToolTip.SetTip(ReloadButton, L.Text("GameServer.Gateway commands: the built Gateway's list"));
        State.Text = L.Text("Copy a command, then paste it into the game's chat with Ctrl+V.");
        PinnedLabel.Text = L.Text("Pinned:");
        PinButton.Content = L.Text("★ Pin");
        ToolTip.SetTip(PinButton, L.Text("Pin this line as a button (right-click a pinned button to remove it)"));
        SendButton.Content = L.Text("Copy");
        ToolTip.SetTip(Input, L.Text("/ lists the commands, then each argument's values (areas, monsters, items, gems …): ↑↓ choose, Tab or Enter take one. Without a list ↑↓ go through the history. Text without / is a chat line."));
        ToolTip.SetTip(CommandFilter, L.Text("Filter the commands by name or description"));
        CommandNote.Text = L.Text("Double-click a command to put it in the line.");
        NoCommands.Text = _noReleaseList ? L.Text("This release's command list cannot be read on Linux yet, because its Gateway is a Windows program. You can still type a command and copy it.") : "";
        NoCommands.IsVisible = _noReleaseList;
    }

    public void Attach(Shared shared)
    {
        _shared = shared;
        _historyIndex = shared.Settings.History.Count;
        ShowFavorites();
        _ = LoadCommands();
        L.Current.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowLanguage();
            ShowFavorites();
        });
    }

    bool _reloading;

    async Task LoadCommands()
    {
        _reloading = true;
        try
        {
            _catalog = await CommandCatalog.LoadAsync();
            _failedBuild = null;
            _noReleaseList = false;
            _values.Clear();
            CommandFilterChanged(this, null!);
            Suggest();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            _failedBuild = File.Exists(Repo.ServerDll("Gateway")) ? File.GetLastWriteTimeUtc(Repo.ServerDll("Gateway")) : null;
            _noReleaseList = Repo.IsInstall;
            Append(L.Text("! commands not loaded: {0}", e.Message));
        }
        finally
        {
            _reloading = false;
            ShowLanguage();
        }
    }

    async void ReloadCommands(object? sender, RoutedEventArgs e) => await LoadCommands();

    // --- The line and its suggestions ---

    string Line => Input.Text ?? "";

    void InputChanged(object? sender, TextChangedEventArgs e)
    {
        // A list from a Gateway built before the last Play or Restart Gateway is read again.
        if (Line.StartsWith('/') && !_reloading && _catalog.Stale && _failedBuild != File.GetLastWriteTimeUtc(Repo.ServerDll("Gateway"))) _ = LoadCommands();
        Suggest();
    }

    /// <summary>The suggestions for the line as it is: a command's name, then its arguments' values.</summary>
    void Suggest()
    {
        _completion = Completion.For(Line, _catalog, kind => _values.GetValueOrDefault(kind));
        IReadOnlyList<Suggestion> items = _completion.Items;
        if (_completion.Loading is { } kind)
        {
            items = [new Suggestion("", L.Text("loading {0} values …", kind), "")];
            _ = LoadValues(kind);
        }
        Suggestions.ItemsSource = items;
        SuggestionPopup.IsOpen = items.Count > 0 && Input.IsFocused && Line != _recalled;
        if (items.Count > 0) Suggestions.SelectedIndex = 0;
        Hint.Text = _catalog.Of(Line)?.Usage ?? "";
    }

    async Task LoadValues(string kind)
    {
        if (!_loading.Add(kind)) return;
        try
        {
            _values[kind] = await CommandCatalog.ValuesAsync(kind);
            Suggest();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // Without values the argument is typed by hand; the reason goes to the output once.
            _values[kind] = [];
            Append(L.Text("! no {0} suggestions: {1}", kind, e.Message));
        }
        finally { _loading.Remove(kind); }
    }

    void InputLostFocus(object? sender, RoutedEventArgs e)
    {
        // A click on a suggestion takes the focus first; it closes the popup itself.
        if (!Suggestions.IsPointerOver) SuggestionPopup.IsOpen = false;
    }

    async void InputKey(object? sender, KeyEventArgs e)
    {
        if (SuggestionPopup.IsOpen && Suggestions.ItemCount > 0)
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; return;
                case Key.Up: Move(-1); e.Handled = true; return;
                case Key.Escape: SuggestionPopup.IsOpen = false; e.Handled = true; return;
                case Key.Tab:
                case Key.Enter when Suggestions.SelectedItem is Suggestion s && !Typed().Equals(s.Insert, StringComparison.OrdinalIgnoreCase):
                    Accept(Suggestions.SelectedItem as Suggestion);
                    e.Handled = true;
                    return;
            }
        }
        switch (e.Key)
        {
            case Key.Enter: e.Handled = true; await Send(Line); break;
            case Key.Up: e.Handled = true; History(-1); break;
            case Key.Down: e.Handled = true; History(1); break;
        }
    }

    void Move(int by)
    {
        Suggestions.SelectedIndex = Math.Clamp(Suggestions.SelectedIndex + by, 0, Suggestions.ItemCount - 1);
        if (Suggestions.SelectedItem is { } item) Suggestions.ScrollIntoView(item);
    }

    /// <summary>What is being completed: the line from the suggestions' start.</summary>
    string Typed() => _completion.From <= Line.Length ? Line[_completion.From..].Trim() : "";

    /// <summary>Puts the suggestion in place of what is being completed, with a space for the next argument (whose
    /// suggestions then open).</summary>
    void Accept(Suggestion? suggestion)
    {
        if (suggestion is not { Insert.Length: > 0 }) return;
        Put($"{Line[..Math.Min(_completion.From, Line.Length)]}{suggestion.Insert} ");
    }

    void SuggestionClicked(object? sender, PointerReleasedEventArgs e) => Accept(Suggestions.SelectedItem as Suggestion);

    void Put(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
        Input.Focus();
    }

    void History(int by)
    {
        var history = _shared.Settings.History;
        if (history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + by, 0, history.Count);
        _recalled = _historyIndex == history.Count ? "" : history[_historyIndex];
        Put(_recalled);
        SuggestionPopup.IsOpen = false;
    }

    // --- Copying ---

    async void SendClicked(object? sender, RoutedEventArgs e) => await Send(Line);

    async Task Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        _shared.Settings.Remember(text);
        _shared.Settings.Save();
        _historyIndex = _shared.Settings.History.Count;
        Input.Clear();
        SuggestionPopup.IsOpen = false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            Problem(L.Text("The clipboard is not available."));
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            Append(L.Text("Copied: {0}", text));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            Problem(ex.Message);
        }
    }

    void Problem(string message) => Append($"  ! {message}");

    void Append(string text)
    {
        string all = Output.Text + text + Environment.NewLine;
        if (all.Length > OutputMax) all = all[^(OutputMax / 2)..];
        Output.Text = all;
        Output.CaretIndex = all.Length;
    }

    // --- Pinned lines and the command list ---

    /// <summary>The pinned lines as buttons. A button only puts its line in the box: Enter or Send still sends it, so
    /// a command that can take the client down (an area change) is never one click away.</summary>
    void ShowFavorites()
    {
        Favorites.Children.Clear();
        foreach (string line in _shared.Settings.Favorites)
        {
            var button = new Button { Content = line, FontFamily = Mono, Margin = new(0, 0, 6, 4) };
            ToolTip.SetTip(button, $"{_catalog.Of(line)?.Usage ?? line}\n{L.Text("Puts the line in the box; Enter or Send sends it.")}");
            button.Click += (_, _) => Put(line);
            var remove = new MenuItem { Header = L.Text("Remove") };
            remove.Click += (_, _) => { _shared.Settings.Favorites.Remove(line); _shared.Settings.Save(); ShowFavorites(); };
            button.ContextMenu = new ContextMenu { Items = { remove } };
            Favorites.Children.Add(button);
        }
    }

    void Pin(object? sender, RoutedEventArgs e)
    {
        string line = Line.Trim();
        if (line.Length == 0 || _shared.Settings.Favorites.Contains(line)) return;
        _shared.Settings.Favorites.Add(line);
        _shared.Settings.Save();
        ShowFavorites();
    }

    void CommandFilterChanged(object? sender, TextChangedEventArgs e)
    {
        string filter = (CommandFilter.Text ?? "").Trim();
        CommandList.ItemsSource = filter.Length == 0 ? _catalog.All
            : _catalog.All.Where(c => c.Usage.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void CommandPicked(object? sender, TappedEventArgs e)
    {
        if (CommandList.SelectedItem is ChatCommand c) Put($"/{c.Name} ");
    }
}

using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Waypoint;

/// <summary>
/// Dev commands for the character in game. A line goes to the client control bridge, which hands it to the client as
/// typed chat (0x15), so the Gateway runs it as it runs one from the chat box and the replies come back from its log.
/// "/" opens the command list (the built Gateway's, with each usage), pinned lines are buttons, ↑↓ is the history.
/// </summary>
public partial class ConsoleTab : UserControl
{
    Shared _shared = null!;
    CommandCatalog _catalog = new([]);
    // Argument values by kind, loaded from the Gateway the first time a line needs them.
    readonly Dictionary<string, IReadOnlyList<ArgumentValue>> _values = [];
    readonly HashSet<string> _loading = [];
    CompletionResult _completion = CompletionResult.None;
    int _historyIndex;
    /// <summary>The last game:status, whose summary a language change writes again.</summary>
    JsonObject? _status;
    const int OutputMax = 300_000;

    public ConsoleTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        _historyIndex = shared.Settings.History.Count;
        ShowFavorites();
        ShowRelease();
        _ = LoadCommands();
        L.Current.PropertyChanged += (_, _) =>
        {
            ShowRelease();
            if (_status is not null) State.Text = Summary(_status);
            ShowFavorites();
        };
    }

    /// <summary>A release has no client control bridge to send through: a line is copied, for the game's chat.</summary>
    void ShowRelease()
    {
        if (!Repo.IsInstall) return;
        GameStatusButton.Visibility = Visibility.Collapsed;
        SendButton.Content = L.Text("Copy");
        State.Text = L.Text("Copy a command, then paste it into the game's chat with Ctrl+V.");
    }

    bool _reloading;

    async Task LoadCommands()
    {
        _reloading = true;
        try
        {
            _catalog = await CommandCatalog.LoadAsync();
            _values.Clear();
            CommandFilterChanged(this, null!);
            Suggest();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Append(L.Text("! commands not loaded: {0}", e.Message));
        }
        finally { _reloading = false; }
    }

    async void ReloadCommands(object sender, RoutedEventArgs e) => await LoadCommands();

    // --- The line and its suggestions ---

    void InputChanged(object sender, TextChangedEventArgs e)
    {
        // A list from a Gateway built before the last Play or Restart Gateway is read again.
        if (Input.Text.StartsWith('/') && !_reloading && _catalog.Stale) _ = LoadCommands();
        Suggest();
    }

    /// <summary>The suggestions for the line as it is: a command's name, then its arguments' values.</summary>
    void Suggest()
    {
        _completion = Completion.For(Input.Text, _catalog, kind => _values.GetValueOrDefault(kind));
        IReadOnlyList<Suggestion> items = _completion.Items;
        if (_completion.Loading is { } kind)
        {
            items = [new Suggestion("", L.Text("loading {0} values …", kind), "")];
            _ = LoadValues(kind);
        }
        Suggestions.ItemsSource = items;
        SuggestionPopup.IsOpen = items.Count > 0 && Input.IsKeyboardFocused;
        if (items.Count > 0) Suggestions.SelectedIndex = 0;
        Hint.Text = _catalog.Of(Input.Text)?.Usage ?? "";
    }

    async Task LoadValues(string kind)
    {
        if (!_loading.Add(kind)) return;
        try
        {
            _values[kind] = await CommandCatalog.ValuesAsync(kind);
            Suggest();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // Without values the argument is typed by hand; the reason goes to the output once.
            _values[kind] = [];
            Append(L.Text("! no {0} suggestions: {1}", kind, e.Message));
        }
        finally { _loading.Remove(kind); }
    }

    void InputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // A click on a suggestion takes the focus first; it closes the popup itself.
        if (!Suggestions.IsMouseOver) SuggestionPopup.IsOpen = false;
    }

    async void InputKey(object sender, KeyEventArgs e)
    {
        if (SuggestionPopup.IsOpen && Suggestions.Items.Count > 0)
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
            case Key.Enter: e.Handled = true; await Send(Input.Text); break;
            case Key.Up: e.Handled = true; History(-1); break;
            case Key.Down: e.Handled = true; History(1); break;
        }
    }

    void Move(int by)
    {
        Suggestions.SelectedIndex = Math.Clamp(Suggestions.SelectedIndex + by, 0, Suggestions.Items.Count - 1);
        Suggestions.ScrollIntoView(Suggestions.SelectedItem);
    }

    /// <summary>What is being completed: the line from the suggestions' start.</summary>
    string Typed() => _completion.From <= Input.Text.Length ? Input.Text[_completion.From..].Trim() : "";

    /// <summary>Puts the suggestion in place of what is being completed, with a space for the next argument (whose
    /// suggestions then open).</summary>
    void Accept(Suggestion? suggestion)
    {
        if (suggestion is not { Insert.Length: > 0 }) return;
        Put($"{Input.Text[..Math.Min(_completion.From, Input.Text.Length)]}{suggestion.Insert} ");
    }

    void SuggestionClicked(object sender, MouseButtonEventArgs e) => Accept(Suggestions.SelectedItem as Suggestion);

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
        Put(_historyIndex == history.Count ? "" : history[_historyIndex]);
        SuggestionPopup.IsOpen = false;
    }

    // --- Sending ---

    async void SendClicked(object sender, RoutedEventArgs e) => await Send(Input.Text);

    async Task Send(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        _shared.Settings.Remember(text);
        _shared.Settings.Save();
        _historyIndex = _shared.Settings.History.Count;
        Input.Clear();
        SuggestionPopup.IsOpen = false;
        if (Repo.IsInstall)
        {
            try
            {
                Clipboard.SetText(text);
                Append(L.Text("Copied: {0}", text));
            }
            catch (System.Runtime.InteropServices.ExternalException ex) { Problem(ex.Message); }
            return;
        }
        Append($"> {text}");
        SendButton.IsEnabled = false;
        try
        {
            var (ok, lines) = ClientBridge.Replies(await _shared.Bridge.SendAsync(text));
            if (!ok) Problem(lines[0]);
            else if (lines.Count == 0) Append("  " + L.Text("(no reply)"));
            else foreach (string line in lines) Append($"  {line}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            Problem(ex.Message);
        }
        finally { SendButton.IsEnabled = true; }
    }

    void Problem(string message)
    {
        Append($"  ! {message}");
        if (message.Contains("not running", StringComparison.OrdinalIgnoreCase))
            Append("    " + L.Text("Start the game (Control section), log in and enter an area, then send again."));
    }

    async void GameStatus(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _shared.Bridge.StatusAsync();
            Append($"> game:status\n  {result["result"]?.ToJsonString() ?? result.ToJsonString()}");
            _status = result;
            State.Text = Summary(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.Text.Json.JsonException)
        {
            Problem(ex.Message);
        }
    }

    /// <summary>game:status in a few words: not running, its screen, the character in game.</summary>
    internal static string Summary(JsonObject status)
    {
        if (status["ok"]?.GetValue<bool>() != true) return L.Text("bridge: {0}", status["error"]);
        var r = status["result"];
        if (r?["running"]?.GetValue<bool>() != true) return L.Text("client not running");
        string screen = r["screen"]?.ToString() ?? "?";
        return r["character"] is { } character ? L.Text("client: {0}, {1}", screen, character) : L.Text("client: {0}", screen);
    }

    void Append(string text)
    {
        Output.AppendText(text + Environment.NewLine);
        if (Output.Text.Length > OutputMax) Output.Text = Output.Text[^(OutputMax / 2)..];
        Output.ScrollToEnd();
    }

    // --- Pinned lines and the command list ---

    /// <summary>The pinned lines as buttons. A button only puts its line in the box: Enter or Send still sends it, so
    /// a command that can take the client down (an area change) is never one click away.</summary>
    void ShowFavorites()
    {
        Favorites.Children.Clear();
        foreach (string line in _shared.Settings.Favorites)
        {
            var button = new Button
            {
                Content = line,
                ToolTip = $"{_catalog.Of(line)?.Usage ?? line}\n{L.Text("Puts the line in the box; Enter or Send sends it.")}",
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono"),
            };
            button.Click += (_, _) => Put(line);
            var remove = new MenuItem { Header = L.Text("Remove") };
            remove.Click += (_, _) => { _shared.Settings.Favorites.Remove(line); _shared.Settings.Save(); ShowFavorites(); };
            button.ContextMenu = new ContextMenu { Items = { remove } };
            Favorites.Children.Add(button);
        }
    }

    void Pin(object sender, RoutedEventArgs e)
    {
        string line = Input.Text.Trim();
        if (line.Length == 0 || _shared.Settings.Favorites.Contains(line)) return;
        _shared.Settings.Favorites.Add(line);
        _shared.Settings.Save();
        ShowFavorites();
    }

    void CommandFilterChanged(object sender, TextChangedEventArgs e)
    {
        string filter = CommandFilter.Text.Trim();
        CommandList.ItemsSource = filter.Length == 0 ? _catalog.All
            : _catalog.All.Where(c => c.Usage.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void CommandPicked(object sender, MouseButtonEventArgs e)
    {
        if (CommandList.SelectedItem is ChatCommand c) Put($"/{c.Name} ");
    }
}

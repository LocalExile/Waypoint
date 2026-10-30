using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameServer.Launcher;

/// <summary>
/// Dev commands for the character in game. A line goes to the client control bridge, which hands it to the client as
/// typed chat (0x15), so the Gateway runs it as it runs one from the chat box and the replies come back from its log.
/// "/" opens the command list (the built Gateway's, with each usage), pinned lines are buttons, ↑↓ is the history.
/// </summary>
public partial class ConsoleTab : UserControl
{
    Shared _shared = null!;
    CommandCatalog _catalog = new([]);
    int _historyIndex;
    const int OutputMax = 300_000;

    public ConsoleTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        _historyIndex = shared.Settings.History.Count;
        ShowFavorites();
        _ = LoadCommands();
    }

    async Task LoadCommands()
    {
        try
        {
            _catalog = await CommandCatalog.LoadAsync();
            CommandFilterChanged(this, null!);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Append($"! commands not loaded: {e.Message}");
        }
    }

    async void ReloadCommands(object sender, RoutedEventArgs e) => await LoadCommands();

    // --- The line and its suggestions ---

    void InputChanged(object sender, TextChangedEventArgs e)
    {
        var suggestions = _catalog.Suggest(Input.Text);
        Suggestions.ItemsSource = suggestions;
        SuggestionPopup.IsOpen = suggestions.Count > 0 && Input.IsKeyboardFocused;
        if (suggestions.Count > 0) Suggestions.SelectedIndex = 0;
        Hint.Text = _catalog.Of(Input.Text)?.Usage ?? "";
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
                case Key.Enter when Suggestions.SelectedItem is ChatCommand c && !Input.Text.Equals($"/{c.Name}", StringComparison.OrdinalIgnoreCase):
                    Accept(Suggestions.SelectedItem as ChatCommand);
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

    void Accept(ChatCommand? command)
    {
        if (command is null) return;
        Put($"/{command.Name} ");
        SuggestionPopup.IsOpen = false;
    }

    void SuggestionClicked(object sender, MouseButtonEventArgs e) => Accept(Suggestions.SelectedItem as ChatCommand);

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
        Append($"> {text}");
        SendButton.IsEnabled = false;
        try
        {
            var (ok, lines) = ClientBridge.Replies(await _shared.Bridge.SendAsync(text));
            if (!ok) Problem(lines[0]);
            else if (lines.Count == 0) Append("  (no reply)");
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
            Append("    Start the game (Control tab), log in and enter an area, then send again.");
    }

    async void GameStatus(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _shared.Bridge.StatusAsync();
            Append($"> game:status\n  {result["result"]?.ToJsonString() ?? result.ToJsonString()}");
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
        if (status["ok"]?.GetValue<bool>() != true) return $"bridge: {status["error"]}";
        var r = status["result"];
        if (r?["running"]?.GetValue<bool>() != true) return "client not running";
        string screen = r["screen"]?.ToString() ?? "?";
        return r["character"] is { } character ? $"client: {screen}, {character}" : $"client: {screen}";
    }

    void Append(string text)
    {
        Output.AppendText(text + Environment.NewLine);
        if (Output.Text.Length > OutputMax) Output.Text = Output.Text[^(OutputMax / 2)..];
        Output.ScrollToEnd();
    }

    // --- Pinned lines and the command list ---

    void ShowFavorites()
    {
        Favorites.Children.Clear();
        foreach (string line in _shared.Settings.Favorites)
        {
            var button = new Button { Content = line, ToolTip = _catalog.Of(line)?.Usage ?? line, FontFamily = (System.Windows.Media.FontFamily)FindResource("Mono") };
            button.Click += async (_, _) => await Send(line);
            var remove = new MenuItem { Header = "Remove" };
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

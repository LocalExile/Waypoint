using System.Net;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>Tools for the game itself. The item crafter's window is not ported to Linux yet, so its card says so.</summary>
partial class ToolsTab : UserControl
{
    /// <summary>Simple mode's way to the Console and Logs sections, which its list leaves out.</summary>
    public event Action? ConsoleRequested, LogsRequested;

    public ToolsTab()
    {
        InitializeComponent();
        ShowLanguage();
        L.Current.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(ShowLanguage);
    }

    /// <summary>The texts Windows gives through {l:Loc}.</summary>
    void ShowLanguage()
    {
        Intro.Text = L.Text("Tools for the game itself. Each opens in its own window and works with the character in game through the client control bridge (Python with Frida).");
        CrafterButton.Content = L.Text("Open item crafter");
        CrafterTitle.Text = L.Text("Item crafter");
        CrafterText.Text = L.Text("Make any item: a base, its rarity, modifiers with their values, sockets and influences. Save it to come back to it, and give it to the character in game.");
        CrafterMissing.Text = L.Text("The item crafter is not available on Linux yet.");
        LootFilterButton.Content = L.Text("Open loot filter editor");
        LootFilterTitle.Text = L.Text("Loot filter (experimental)");
        LootFilterText.Text = L.Text("Make your own loot filter, much like FilterBlade. Opens in your browser and writes the filter where the game finds it.");
        ConsoleButton.Content = L.Text("Open console");
        ConsoleTitle.Text = L.Text("Console");
        ConsoleText.Text = L.Text("Look up the server's commands and use one in the game.");
        LogsButton.Content = L.Text("Open logs");
        LogsTitle.Text = L.Text("Logs");
        LogsText.Text = L.Text("What Waypoint, the servers and the game wrote down: where to look when something went wrong.");
    }

    Shared? _shared;

    public void Attach(Shared shared) => _shared = shared;

    /// <summary>Console and Logs cards for Simple mode, whose list has neither; the loot filter editor, experimental, only in
    /// Advanced mode.</summary>
    public void ShowMode(bool simple)
    {
        ConsoleCard.IsVisible = LogsCard.IsVisible = simple;
        LootFilterCard.IsVisible = !simple;
    }

    void OpenConsole(object? sender, RoutedEventArgs e) => ConsoleRequested?.Invoke();

    void OpenLootFilter(object? sender, RoutedEventArgs e)
    {
        try { Ui.Open(LootFilterEditor.Url(_shared?.Settings.ServerPath)); }
        catch (Exception ex) when (ex is HttpListenerException or IOException)
        {
            Dialog.Tell(TopLevel.GetTopLevel(this) as Window, DialogKind.Warning, L.Text("Loot filter"), ex.Message);
        }
    }

    void OpenLogs(object? sender, RoutedEventArgs e) => LogsRequested?.Invoke();
}

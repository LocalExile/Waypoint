using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>Tools for the game itself. The item crafter's window is not ported to Linux yet, so its card says so.</summary>
partial class ToolsTab : UserControl
{
    /// <summary>The player wants a copy of LocalExile that has the crafter.</summary>
    public event Action? NewCopyRequested;

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
        ReleaseText.Text = L.Text("Your version of LocalExile does not have the item crafter yet. A new copy of LocalExile does, and you can bring your characters with you.");
        GetCrafterButton.Content = L.Text("Get the item crafter…");
        CrafterButton.Content = L.Text("Open item crafter");
        CrafterTitle.Text = L.Text("Item crafter");
        CrafterText.Text = L.Text("Make any item: a base, its rarity, modifiers with their values, sockets and influences. Save it to come back to it, and give it to the character in game.");
        CrafterMissing.Text = L.Text("The item crafter is not available on Linux yet.");
    }

    /// <summary>On Windows a release shows ReleaseNote, which offers a new copy for the crafter. A copy has none here yet, so
    /// the offer would have a player move their characters for nothing: it stays hidden until the crafter is ported, and
    /// showing it is all that is left to do for NewCopyRequested.</summary>
    public void Attach(Shared shared) { }

    void GetCrafterClicked(object? sender, RoutedEventArgs e) => NewCopyRequested?.Invoke();
}

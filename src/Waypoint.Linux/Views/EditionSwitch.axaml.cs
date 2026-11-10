using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Waypoint;

/// <summary>Simple mode's Release | Latest switch, at the top of both Play views (<see cref="Editions"/>), as on Windows.</summary>
public partial class EditionSwitch : UserControl
{
    Shared _shared = null!;

    /// <summary>The player has no release yet and wants the setup guide's release route.</summary>
    internal event Action? ReleaseSetupRequested;
    /// <summary>The player has no Latest copy yet and wants the guide's source route.</summary>
    internal event Action? LatestSetupRequested;

    public EditionSwitch() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        L.Current.PropertyChanged += (_, _) => Show();
        Show();
    }

    /// <summary>Developer Mode changed, or a copy was set up: shown or not again.</summary>
    internal void Refresh() { if (_shared is not null) Show(); }

    void Show()
    {
        IsVisible = Editions.Offered(_shared.Settings);
        ReleaseChoice.IsChecked = Repo.IsInstall;
        LatestChoice.IsChecked = !Repo.IsInstall;
        // The crafter's route can leave the copy on the tested version, which Play does not move.
        bool tested = !Repo.IsInstall && SwitchToSource.OnTestedVersion(Environment.CurrentDirectory);
        Caption.Text = Repo.IsInstall ? L.Text("LocalExile's published release. Its characters are its own.")
            : tested ? L.Text("An older version of LocalExile's source, which doesn't update. Its characters are its own.")
            : L.Text("LocalExile's newest changes, updated when you play. Its characters are its own.");
        NewestLink.IsVisible = tested;
    }

    async void NewestClicked(object? sender, RoutedEventArgs e)
    {
        var owner = Ui.Owner(this);
        await Editions.MoveToNewestAsync(_shared, (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons));
        Show();
    }

    void Chosen(object? sender, RoutedEventArgs e) => Choose(sender == ReleaseChoice);

    /// <summary>The player chose a side, here or through the crafter's "Get the item crafter…".</summary>
    internal async void Choose(bool release)
    {
        var owner = Ui.Owner(this);
        var (step, folder) = await Editions.PrepareAsync(_shared, release,
            (kind, title, message, buttons) => Dialog.Show(owner, kind, title, message, null, buttons));
        Show();
        if (step == EditionStep.SetUpRelease) ReleaseSetupRequested?.Invoke();
        else if (step == EditionStep.SetUpLatest) LatestSetupRequested?.Invoke();
        // PrepareAsync has made sure nothing runs.
        else if (step == EditionStep.Switch && owner is not null) await App.SwitchServerAsync(owner, _shared, folder, asked: true);
    }
}

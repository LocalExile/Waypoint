using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Launcher.Core;

namespace Launcher.Desktop;

// The shell exposes a navigation list instead of tab headers. A pane peer walks the visible
// content; TabControlAutomationPeer would hide it because no header containers are presented.
public sealed class PageHost : TabControl
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // There are no header containers to traverse. TabControl's Ctrl+Tab handler assumes
        // one exists and throws; let keys bubble to the shell's routed page commands instead.
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PageHostPeer(this);
    sealed class PageHostPeer(PageHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => "WorkspacePage";
    }
}

public sealed record NavigationPage(string Label, string Glyph)
{
    public string DisplayLabel => L.Text(Label);
    public override string ToString() => DisplayLabel;
}

public static class UiCommands
{
    public static readonly RoutedUICommand Navigate = new("Choose page", "Navigate", typeof(UiCommands));
    public static readonly RoutedUICommand NextPage = new("Next page", "NextPage", typeof(UiCommands));
    public static readonly RoutedUICommand PreviousPage = new("Previous page", "PreviousPage", typeof(UiCommands));
    public static readonly RoutedUICommand Search = new("Search current page", "Search", typeof(UiCommands));
    public static readonly RoutedUICommand Refresh = new("Refresh local status", "Refresh", typeof(UiCommands));
}

/// <summary>Reconciles by identity, retaining item containers, selection and keyboard focus on unchanged rows.</summary>
public static class UiCollection
{
    public static void Reconcile<T, TKey>(ObservableCollection<T> items, IEnumerable<T> incoming,
        Func<T, TKey> key, Func<T, T, bool>? equal = null, Action<T, T>? update = null) where TKey : notnull
    {
        var desired = incoming.ToArray();
        equal ??= EqualityComparer<T>.Default.Equals;
        for (int i = 0; i < desired.Length; i++)
        {
            var identity = key(desired[i]);
            int existing = -1;
            for (int j = i; j < items.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(items[j]), identity)) { existing = j; break; }
            if (existing < 0) items.Insert(i, desired[i]);
            else
            {
                if (existing != i) items.Move(existing, i);
                if (update is not null) update(items[i], desired[i]);
                else if (!equal(items[i], desired[i])) items[i] = desired[i];
            }
        }
        while (items.Count > desired.Length) items.RemoveAt(items.Count - 1);
    }
}

public sealed class ServicePresentation(ServiceRow value) : INotifyPropertyChanged
{
    public ServiceRow Value { get; private set; } = value;
    public override string ToString() => $"{Value.DisplayName}, {Value.DisplayStatus}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshLanguage() => PropertyChanged?.Invoke(this, new(nameof(Value)));
    public void Update(ServiceRow next)
    {
        if (Value == next) return;
        Value = next;
        PropertyChanged?.Invoke(this, new(nameof(Value)));
    }
}

public partial class MainWindow
{
    readonly ObservableCollection<Launcher.Core.ModEntry> visibleMods = [];
    readonly ObservableCollection<ServicePresentation> serviceRows = [];
    readonly ObservableCollection<Launcher.Core.SetupStep> requirementRows = [];
    string announcedStatus = "";

    void RefreshNavigationLabels()
    {
        // A new collection refreshes display bindings without changing the selected page.
        int selected = Math.Max(0, Pages.SelectedIndex);
        if (Navigation.ItemsSource is NavigationPage[] pages) Navigation.ItemsSource = pages.ToArray();
        Navigation.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, selected);
    }

    void InitializeInteraction()
    {
        int initialPage = Math.Max(0, Pages.SelectedIndex);
        Navigation.ItemsSource = new NavigationPage[]
        {
            new("Play", "\uE768"), new("Setup", "\uE9F5"), new("Builds", "\uE8AD"), new("Mods", "\uE74C"),
            new("Backups", "\uE74E"), new("Performance", "\uE9D9"), new("Logs", "\uE8A5"), new("Settings", "\uE713")
        };
        ModsList.ItemsSource = visibleMods;
        ServiceList.ItemsSource = serviceRows;
        PerformanceList.ItemsSource = serviceRows;
        RequirementList.ItemsSource = SetupRequirementList.ItemsSource = requirementRows;
        CommandBindings.Add(new(UiCommands.Navigate, (_, e) =>
        {
            if (int.TryParse(e.Parameter?.ToString(), out int index) && index >= 0 && index < Pages.Items.Count)
                NavigateTo(index);
        }));
        CommandBindings.Add(new(UiCommands.NextPage, (_, _) => NavigateTo((Pages.SelectedIndex + 1) % Pages.Items.Count)));
        CommandBindings.Add(new(UiCommands.PreviousPage, (_, _) => NavigateTo((Pages.SelectedIndex + Pages.Items.Count - 1) % Pages.Items.Count)));
        CommandBindings.Add(new(UiCommands.Search, (_, _) => FocusPageSearch()));
        CommandBindings.Add(new(UiCommands.Refresh, async (_, _) => { lastError = null; await RefreshAsync(); }, (_, e) => e.CanExecute = !busy));
        for (int i = 0; i < Pages.Items.Count; i++)
            InputBindings.Add(new KeyBinding(UiCommands.Navigate, new KeyGesture((Key)((int)Key.D1 + i), ModifierKeys.Control)) { CommandParameter = i.ToString() });
        InputBindings.Add(new KeyBinding(UiCommands.NextPage, new KeyGesture(Key.Tab, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(UiCommands.PreviousPage, new KeyGesture(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(UiCommands.Search, new KeyGesture(Key.F, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(UiCommands.Refresh, new KeyGesture(Key.F5)));
        Pages.SelectedIndex = initialPage;
        Navigation.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, initialPage);
    }

    void NavigateTo(int index)
    {
        Pages.SelectedIndex = index;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // Put keyboard users in the chosen page rather than leaving focus in detached content.
            if (Pages.SelectedItem == SettingsPage) { AppearanceButton.Focus(); return; }
            if (!FocusPageSearch() && ((TabItem)Pages.SelectedItem).Content is FrameworkElement content)
                content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }));
    }

    bool FocusPageSearch()
    {
        TextBox? search = Pages.SelectedItem == ModsPage ? ModSearch
            : Pages.SelectedItem == BuildsPage ? SourceSearch : Pages.SelectedItem == LogsPage ? LogSearchBox
            : Pages.SelectedItem == SettingsPage ? RepositoryPathBox : null;
        if (search is null) return false;
        search.Focus();
        search.SelectAll();
        return true;
    }

    void ClearModSearch_Click(object sender, RoutedEventArgs e) { ModSearch.Clear(); ModSearch.Focus(); }

    void SetFeedback(bool failed = false)
    {
        StatusBanner.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, failed ? "Error" : "Outline");
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Error" : "Text");
        if (failed) AnnounceStatus();
    }

    void AnnounceStatus()
    {
        if (announcedStatus == StatusLabel.Text) return;
        announcedStatus = StatusLabel.Text;
        UIElementAutomationPeer.FromElement(StatusLabel)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

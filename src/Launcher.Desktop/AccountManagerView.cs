using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Launcher.Core;

namespace Launcher.Desktop;

static class AccountUi
{
    public static void Bind(DependencyObject element, DependencyProperty property, string text) =>
        BindingOperations.SetBinding(element, property, new Binding($"[{L.ResourceKey(text)}]") { Source = L.Current, Mode = BindingMode.OneWay });
    public static TextBlock Text(string text, string size = "Font14", bool muted = false)
    {
        var label = new TextBlock(); Bind(label, TextBlock.TextProperty, text); label.SetResourceReference(TextBlock.FontSizeProperty, size);
        if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return label;
    }
    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button(); Bind(button, ContentControl.ContentProperty, text);
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        button.Click += (_, _) => click(); return button;
    }
}

/// <summary>Account list/detail presentation only. RuntimeClient and the bridge own all reads and writes.</summary>
public sealed class AccountManagerView : UserControl
{
    readonly ObservableCollection<PlayerAccount> visible = [];
    readonly TextBox search = new() { Margin = new Thickness(0, 6, 0, 8) };
    readonly ListBox accounts = new(), characters = new();
    readonly TextBlock count = new(), name = new(), summary = new(), empty = new(), feedback = new(), access = new(), played = new();
    readonly Button create, refresh;
    readonly Dictionary<string, Button> actions = [];
    readonly CheckBox backup = new();
    AccountInventory? inventory;
    RuntimeSnapshot? runtime;
    bool busy, loading, settingBackup;
    public event Action? RefreshRequested;
    public event Action<string, PlayerAccount?>? ActionRequested;
    public event Action<bool>? BackupChanged;
    public PlayerAccount? Selected => accounts.SelectedItem as PlayerAccount;
    public IReadOnlyList<PlayerAccount> AllAccounts => inventory?.Accounts ?? [];
    public bool ChangesEnabled => actions["Delete"].IsEnabled;
    public TextBox SearchBox => search;

    public AccountManagerView()
    {
        AccountUi.Bind(this, AutomationProperties.NameProperty, "Player accounts");
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(AccountUi.Text("Accounts", "Font22"));
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        create = AccountUi.Button("Create account", () => ActionRequested?.Invoke("Create", null), true);
        refresh = AccountUi.Button("Reload accounts", () => RefreshRequested?.Invoke());
        tools.Children.Add(create); tools.Children.Add(refresh); header.Children.Add(tools); root.Children.Add(header);
        var status = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        AccountUi.Bind(backup, ContentControl.ContentProperty, "Automatic backup before account changes");
        backup.Checked += BackupPreferenceChanged; backup.Unchecked += BackupPreferenceChanged; status.Children.Add(backup);
        access.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); access.SetResourceReference(TextBlock.FontSizeProperty, "Font12"); status.Children.Add(access);
        feedback.Margin = new Thickness(0, 6, 0, 0); feedback.SetResourceReference(TextBlock.FontSizeProperty, "Font12");
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite); status.Children.Add(feedback);
        Grid.SetRow(status, 1); root.Children.Add(status);
        var columns = new Grid(); columns.ColumnDefinitions.Add(new() { Width = new GridLength(220) }); columns.ColumnDefinitions.Add(new() { Width = new GridLength(16) }); columns.ColumnDefinitions.Add(new());
        var list = new DockPanel();
        var searchPanel = new StackPanel(); searchPanel.Children.Add(AccountUi.Text("Search accounts or characters", "Font12", true));
        AccountUi.Bind(search, AutomationProperties.NameProperty, "Search accounts or characters"); searchPanel.Children.Add(search);
        count.Margin = new Thickness(0, 0, 0, 8); count.SetResourceReference(TextBlock.FontSizeProperty, "Font12"); count.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); searchPanel.Children.Add(count);
        DockPanel.SetDock(searchPanel, Dock.Top); list.Children.Add(searchPanel);
        accounts.ItemsSource = visible; accounts.ItemTemplate = (DataTemplate)Application.Current.FindResource("PlayerAccountTemplate");
        AccountUi.Bind(accounts, AutomationProperties.NameProperty, "Player accounts"); list.Children.Add(accounts); columns.Children.Add(list);
        var detail = new DockPanel();
        var selectedHeader = new StackPanel(); name.SetResourceReference(TextBlock.FontSizeProperty, "Font22"); name.FontWeight = FontWeights.SemiBold;
        name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; selectedHeader.Children.Add(name);
        summary.Margin = new Thickness(0, 4, 0, 12); summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); selectedHeader.Children.Add(summary);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (action, text) in new[] { ("Duplicate", "Duplicate…"), ("Rename", "Rename…"), ("Password", "Set password…"), ("Delete", "Delete account…") })
        {
            var button = AccountUi.Button(text, () => ActionRequested?.Invoke(action, Selected));
            if (action == "Delete") button.SetResourceReference(Control.ForegroundProperty, "Error");
            actions[action] = button; buttons.Children.Add(button);
        }
        selectedHeader.Children.Add(buttons);
        var headings = new Grid { Margin = new Thickness(10, 0, 10, 6) };
        foreach (var width in new[] { new GridLength(1.5, GridUnitType.Star), new GridLength(56), new GridLength(1.2, GridUnitType.Star), new GridLength(1.2, GridUnitType.Star) }) headings.ColumnDefinitions.Add(new() { Width = width });
        int column = 0;
        foreach (string text in new[] { "Character", "Level", "Class / ascendancy", "League" }) { var h = AccountUi.Text(text, "Font11", true); Grid.SetColumn(h, column++); headings.Children.Add(h); }
        selectedHeader.Children.Add(headings); DockPanel.SetDock(selectedHeader, Dock.Top); detail.Children.Add(selectedHeader);
        played.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); played.SetResourceReference(TextBlock.FontSizeProperty, "Font12"); played.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(played, Dock.Bottom); detail.Children.Add(played);
        var table = new Grid();
        characters.ItemTemplate = (DataTemplate)Application.Current.FindResource("PlayerCharacterTemplate");
        AccountUi.Bind(characters, AutomationProperties.NameProperty, "Characters on selected account"); table.Children.Add(characters);
        empty.Margin = new Thickness(18); empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); empty.VerticalAlignment = VerticalAlignment.Center; empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.IsHitTestVisible = false; table.Children.Add(empty); detail.Children.Add(table);
        Grid.SetColumn(detail, 2); columns.Children.Add(detail); Grid.SetRow(columns, 2); root.Children.Add(columns);
        var scroll = new WorkspaceScroll { Content = root }; scroll.SetResourceReference(WorkspaceScroll.MinimumContentHeightProperty, "WorkspaceMinHeight"); Content = scroll;
        search.TextChanged += (_, _) => Filter(); accounts.SelectionChanged += (_, _) => SelectAccount();
        characters.SelectionChanged += (_, _) => UpdatePlayed();
        SetInventory(null); UpdateAvailability();
    }

    public void SetInventory(AccountInventory? value)
    {
        inventory = value; Filter();
        Feedback(value is null ? "Choose a checkout and reload its accounts." : value.Notice.Length > 0 ? value.Notice : "Accounts loaded. Reload to check for changes.");
    }
    public void Feedback(string message, bool error = false)
    {
        feedback.Text = L.Text(message); feedback.SetResourceReference(TextBlock.ForegroundProperty, error ? "Error" : "Muted");
    }
    public void SetLoading(bool value) { loading = value; if (value) Feedback("Loading accounts…"); SelectAccount(); }
    public void SetRuntime(RuntimeSnapshot? state, bool operating, bool automaticBackups)
    {
        runtime = state; busy = operating; settingBackup = true; backup.IsChecked = automaticBackups; settingBackup = false; UpdateAvailability();
    }
    public void RefreshLanguage()
    {
        accounts.Items.Refresh(); characters.Items.Refresh(); SelectAccount(); UpdateAvailability();
        count.Text = L.Text("{0} of {1} accounts", visible.Count, AllAccounts.Count);
    }
    void BackupPreferenceChanged(object sender, RoutedEventArgs e) { if (!settingBackup) BackupChanged?.Invoke(backup.IsChecked == true); }
    void Filter()
    {
        string? id = Selected?.Id; string text = search.Text.Trim();
        var matching = AllAccounts.Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            a.Characters.Any(c => (c.Name + " " + c.Class + " " + c.Ascendancy + " " + c.League).Contains(text, StringComparison.OrdinalIgnoreCase)));
        UiCollection.Reconcile(visible, matching, a => a.Id);
        accounts.SelectedItem = visible.FirstOrDefault(a => a.Id == id) ?? visible.FirstOrDefault();
        count.Text = L.Text("{0} of {1} accounts", visible.Count, AllAccounts.Count); SelectAccount();
    }
    void SelectAccount()
    {
        string? character = (characters.SelectedItem as PlayerCharacter)?.Id;
        name.Text = Selected?.Name ?? L.Text(visible.Count == 0 && AllAccounts.Count > 0 ? "No matching accounts" : "Select an account"); name.ToolTip = Selected?.Name;
        summary.Text = Selected?.Summary ?? "";
        characters.ItemsSource = Selected?.Characters ?? [];
        characters.SelectedItem = Selected?.Characters.FirstOrDefault(c => c.Id == character) ?? Selected?.Characters.FirstOrDefault();
        empty.Visibility = Selected?.Characters.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        empty.Text = L.Text(inventory is null ? loading ? "Loading accounts…" : "Reload accounts to view the selected database."
            : Selected is not null ? "No characters yet. Sign in and create a character in the game."
            : AllAccounts.Count > 0 ? "Choose an account from the list, or change your search."
            : "Create your first account to get started. Characters are created in the game.");
        UpdatePlayed(); UpdateAvailability();
    }
    void UpdatePlayed() => played.Text = characters.SelectedItem is PlayerCharacter ch ? L.Text("Last played: {0}", ch.Played) : "";
    void UpdateAvailability()
    {
        bool stopped = runtime is { HasServers: false, GameRunning: false, Conflicts.Length: 0, AccountWriteBlockedReason.Length: 0 };
        bool available = !busy && !loading && stopped;
        create.IsEnabled = available && inventory?.CanRegister == true;
        refresh.IsEnabled = runtime is not null && !busy && !loading; backup.IsEnabled = !busy && !loading;
        foreach (var button in actions.Values) button.IsEnabled = available && inventory?.Writable == true && Selected is not null;
        access.Text = L.Text(runtime is null ? "Check your workspace in Settings before managing accounts."
            : !stopped ? "Read-only while the game or services are running, or ownership is uncertain. Stop them and reload before changing accounts."
            : "Changes require the game and all LocalExile services to stay stopped until the operation finishes.");
        if (!stopped && runtime is { AccountWriteBlockedReason.Length: > 0 })
            access.Text = L.Text("Read-only: {0}", L.Describe(runtime.AccountWriteBlockedReason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]));
        access.ToolTip = runtime?.AccountWriteBlockedReason;
        access.SetResourceReference(TextBlock.ForegroundProperty, runtime is not null && !stopped ? "Warning" : "Muted");
    }
}

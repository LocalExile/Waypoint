using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Waypoint;

/// <summary>The accounts and characters. The list is read from the checkout's save database (the adapter's AccountData.py),
/// with nothing running; registering and creating a character go through the server's dev host (GameServer.Host), which the
/// section starts when it needs it; duplicating, renaming, setting a password and deleting are the adapter's, reviewed
/// against what the list showed, and want the game and every server stopped.</summary>
partial class AccountsTab : UserControl
{
    Shared _shared = null!;
    AccountInventory? _inventory;

    public AccountsTab() => InitializeComponent();

    PlayerAccount? Selected => AccountGrid.SelectedItem as PlayerAccount;

    public void Attach(Shared shared)
    {
        _shared = shared;
        CharacterClass.ItemsSource = AccountList.Classes;
        CharacterClass.SelectedItem = "Witch";
        // These go through the dev host, which a release does not have; Create account… works there through LocalExile.exe.
        if (Repo.IsInstall) StopHost.IsVisible = HostState.IsVisible = HostTools.IsVisible = false;
        ForAccount.Text = L.Text("(select an account above)");
        shared.Monitor.Sampled += samples => Dispatcher.UIThread.Post(() =>
        {
            bool up = samples.FirstOrDefault(s => s.Service == Service.DevHost)?.Running == true;
            HostState.Text = up ? L.Text("Dev host running on port {0}", Service.DevHost.PortList) : L.Text("Dev host stopped (registering starts it)");
            StopHost.IsEnabled = up;
        });
        shared.Workspace.Changed += () => Dispatcher.UIThread.Post(ShowAccess);
        shared.LiveRunning += running => Dispatcher.UIThread.Post(ShowAccess);
        L.Current.PropertyChanged += (_, _) =>
        {
            ShowTexts();
            Filter();
            ShowAccess();
        };
        // Showing the section reads the list: the adapter needs no server for that.
        ViewTools.WhenShown(this, async () => await Load());
        ShowTexts();
    }

    void ShowTexts()
    {
        ReloadButton.Content = L.Text("Reload accounts");
        StopHost.Content = L.Text("Stop dev host");
        ToolTip.SetTip(StopHost, L.Text("GameServer.Host, which Waypoint starts to register accounts and create characters"));
        RegisterHeading.Text = L.Text("Register an account");
        NewAccountLabel.Text = L.Text("Name");
        ToolTip.SetTip(NewAccount, L.Text("3–32 letters, digits, _ or -"));
        NewPasswordLabel.Text = L.Text("Password");
        ToolTip.SetTip(NewPassword, L.Text("at least 4 characters"));
        RegisterButton.Content = L.Text("Register");
        ToolTip.SetTip(RegisterButton, L.Text("Through the dev host: works beside running servers"));
        CreateHeading.Text = L.Text("Create a character");
        ForAccountLabel.Text = L.Text("Account");
        AccountPasswordLabel.Text = L.Text("Password");
        ToolTip.SetTip(AccountPassword, L.Text("The account's password: the dev host logs in with it"));
        CharacterNameLabel.Text = L.Text("Name");
        CharacterClassLabel.Text = L.Text("Class");
        CreateCharacterButton.Content = L.Text("Create");
        ToolTip.SetTip(CreateCharacterButton, L.Text("In the Standard league; the game's own creation screen offers the other leagues"));
        Search.PlaceholderText = L.Text("Search accounts or characters");
        AccountHeader.Text = L.Text("Account");
        CharactersHeader.Text = L.Text("Characters");
        CreateButton.Content = L.Text("Create account…");
        DuplicateButton.Content = L.Text("Duplicate…");
        RenameButton.Content = L.Text("Rename…");
        PasswordButton.Content = L.Text("Set password…");
        DeleteButton.Content = L.Text("Delete account…");
        CharacterHeader.Text = L.Text("Character");
        ClassHeader.Text = L.Text("Class / ascendancy");
        LevelHeader.Text = L.Text("Level");
        LeagueHeader.Text = L.Text("League");
        PlayedHeader.Text = L.Text("Last played");
    }

    async Task Load(string? select = null)
    {
        string? keep = select ?? Selected?.Name;
        var (inventory, problem) = await _shared.Workspace.ReadAccountsAsync();
        if (inventory is null)
        {
            Say(Explain(problem ?? ""), true);
            return;
        }
        _inventory = inventory;
        Filter(keep);
        Say(inventory.Notice.Length > 0 ? L.Text(inventory.Notice)
            : inventory.Accounts.Length == 0 ? L.Text("No accounts yet: register one below.") : L.Text("{0} account(s).", inventory.Accounts.Length), false,
            inventory.Notice.Length > 0 ? "Shell.Warning" : null);
        ShowAccess();
    }

    void Filter(string? keep = null)
    {
        keep ??= Selected?.Name;
        string text = (Search.Text ?? "").Trim();
        var matching = (_inventory?.Accounts ?? []).Where(a => text.Length == 0 || a.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            a.Characters.Any(c => (c.Name + " " + c.Class + " " + c.Ascendancy + " " + c.League).Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        AccountGrid.ItemsSource = matching;
        AccountGrid.SelectedItem = matching.FirstOrDefault(a => a.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? matching.FirstOrDefault();
    }

    void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_shared is not null) Filter();
    }

    void AccountSelected(object? sender, SelectionChangedEventArgs e)
    {
        CharacterGrid.ItemsSource = Selected?.Characters;
        ForAccount.Text = Selected?.Name ?? L.Text("(select an account above)");
        ShowAccess();
    }

    /// <summary>The changes wait for everything to be stopped; the adapter says why when it is not.</summary>
    void ShowAccess()
    {
        var state = _shared.Workspace.Latest;
        bool stopped = state is { HasServers: false, GameRunning: false, Conflicts.Length: 0, AccountWriteBlockedReason.Length: 0 };
        bool available = stopped && !_shared.IsLiveRunning;
        foreach (var button in AccountActions.Children.OfType<Button>())
            button.IsEnabled = button == CreateButton ? available && _inventory?.CanRegister == true : available && _inventory?.Writable == true && Selected is not null;
        Access.Text = state is null ? L.Text("Check your workspace in Settings before managing accounts.")
            : state.AccountWriteBlockedReason is { Length: > 0 } reason ? L.Text("Read-only: {0}", L.Describe(reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]))
            : !stopped ? L.Text("Read-only while the game or services are running, or ownership is uncertain. Stop them and reload before changing accounts.")
            : L.Text("Changes require the game and all LocalExile services to stay stopped until the operation finishes.");
    }

    async void Refresh(object? sender, RoutedEventArgs e) => await Load();

    void StopDevHost(object? sender, RoutedEventArgs e) => _shared.StopDevHost();

    async void Register(object? sender, RoutedEventArgs e)
    {
        string name = (NewAccount.Text ?? "").Trim(), password = NewPassword.Text ?? "";
        if (name.Length == 0 || password.Length == 0) { Say(L.Text("Enter a name and a password."), true); return; }
        if (await Host(() => DevHost.Register(name, password)))
        {
            NewPassword.Clear();
            _shared.Workspace.PrivateNames.Add(name);
            await Load(name);
            Say(L.Text("Account {0} registered: log in with it in the game's Email field.", name), false);
        }
    }

    async void Create(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } account) { Say(L.Text("Select an account first."), true); return; }
        string name = (CharacterName.Text ?? "").Trim(), password = AccountPassword.Text ?? "";
        if (name.Length == 0 || password.Length == 0) { Say(L.Text("Enter the account's password and the character's name."), true); return; }
        string characterClass = (string)CharacterClass.SelectedItem!;
        if (await Host(() => DevHost.CreateCharacter(account.Name, password, name, characterClass)))
        {
            CharacterName.Clear();
            await Load(account.Name);
            Say(L.Text("{0} ({1}) created on {2}.", name, characterClass, account.Name), false);
        }
    }

    /// <summary>Runs a dev-host operation, starting the host first when needed; false (and the reason shown) on failure.</summary>
    async Task<bool> Host(Func<Task> operation)
    {
        IsEnabled = false;
        try
        {
            await DevHost.EnsureRunningAsync(line => Say(line, false));
            await operation();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException
            or TimeoutException or System.ComponentModel.Win32Exception)
        {
            Say(Explain(ex.Message), true);
            _shared.Logs.Write($"accounts: {ex.Message}", Severity.Error);
            return false;
        }
        finally { IsEnabled = true; }
    }

    void CreateAccountClicked(object? sender, RoutedEventArgs e) => _ = EditAsync("Create", null);

    async void Change(object? sender, RoutedEventArgs e) => await EditAsync((string)((Button)sender!).Tag!, Selected);

    /// <summary>The Create step of the Setup section: this section's dialog, when the changes are possible.</summary>
    public async Task CreateAccountAsync()
    {
        if (_inventory is null) await Load();
        await EditAsync("Create", null);
    }

    /// <summary>A change is reviewed in its dialog against the list shown here, and the adapter refuses it if the account
    /// changed since.</summary>
    async Task EditAsync(string action, PlayerAccount? selected)
    {
        if (_inventory is null || _shared.Workspace.Latest is not { } state || action != "Create" && selected is null) return;
        var dialog = new AccountActionDialog(ViewTools.Owner(this), action, selected, _inventory.Accounts, _shared.Settings.AutomaticBackups, state.SaveDatabase);
        if (dialog.ShowDialog() != true || dialog.Request is not { } request) return;
        request = request with { ExpectedDatabase = state.SaveDatabase };
        _shared.Workspace.PrivateNames.Add(request.Name);
        if (request.CharacterNames is not null) _shared.Workspace.PrivateNames.UnionWith(request.CharacterNames.Values);
        var result = action == "Create"
            ? await _shared.Workspace.OperateAsync("Register", L.Text("Create account"), account: new(request.Name, request.Password, state.SaveDatabase))
            : await _shared.Workspace.OperateAsync("ManageAccount", L.Text("Change account"), accountData: request);
        if (result is { AccountData: { } changed })
        {
            _inventory = changed;
            Filter(request.Name);
            ShowAccess();
        }
        else await Load(request.Name);
    }

    /// <summary>The dev protocol's error codes (AuthError) in words.</summary>
    public static string Explain(string message) => message
        .Replace("CharacterNameTaken", L.Text("that character name is taken"))
        .Replace("InvalidCharacterName", L.Text("character names are 3–32 letters, digits, _ or -"))
        .Replace("NameTaken", L.Text("that account name is taken"))
        .Replace("InvalidName", L.Text("names are 3–32 letters, digits, _ or -"))
        .Replace("WeakPassword", L.Text("the password needs at least 4 characters"))
        .Replace("BadCredentials", L.Text("wrong password"))
        .Replace("LimitReached", L.Text("the account has the most characters it can have"));

    void Say(string text, bool error, string? brush = null)
    {
        Status.Text = text;
        Status.Foreground = App.Brush(brush ?? (error ? "Shell.Error" : "Shell.Good"));
    }
}

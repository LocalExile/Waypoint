using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameServer.Launcher;

/// <summary>The accounts and characters, through the server's dev host (GameServer.Host): its "accounts" lists them,
/// "register" and "create_character" make them. The tab starts the host when it needs it.</summary>
public partial class AccountsTab : UserControl
{
    Shared _shared = null!;

    public AccountsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        CharacterClass.ItemsSource = AccountList.Classes;
        CharacterClass.SelectedItem = "Witch";
        shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() =>
        {
            bool up = samples.FirstOrDefault(s => s.Service == Service.DevHost)?.Running == true;
            HostState.Text = up ? $"Dev host running on port {Service.DevHost.PortList}" : "Dev host stopped (Refresh starts it)";
            StopHost.IsEnabled = up;
        });
        // Showing the tab reads the list when the host already runs; starting it is the Refresh button's.
        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            if (DevHost.Listening()) await Load();
            else if (AccountGrid.Items.Count == 0) Say("The account list comes from the server's dev host: Refresh starts it.", false);
        };
    }

    async Task Load(string? select = null)
    {
        string? keep = select ?? (AccountGrid.SelectedItem as AccountRow)?.Name;
        List<AccountRow>? accounts = null;
        if (!await Host(async () => accounts = await AccountList.LoadAsync()) || accounts is null) return;
        AccountGrid.ItemsSource = accounts;
        AccountGrid.SelectedItem = accounts.FirstOrDefault(a => a.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
        Say(accounts.Count == 0 ? "No accounts yet: register one below." : $"{accounts.Count} account(s).", false);
    }

    void AccountSelected(object sender, SelectionChangedEventArgs e)
    {
        var account = AccountGrid.SelectedItem as AccountRow;
        CharacterGrid.ItemsSource = account?.CharacterRows;
        ForAccount.Text = account?.Name ?? "(select an account above)";
    }

    async void Refresh(object sender, RoutedEventArgs e) => await Load();

    void StopDevHost(object sender, RoutedEventArgs e) => _shared.StopDevHost();

    async void Register(object sender, RoutedEventArgs e)
    {
        string name = NewAccount.Text.Trim(), password = NewPassword.Password;
        if (name.Length == 0 || password.Length == 0) { Say("Enter a name and a password.", true); return; }
        if (await Host(() => DevHost.Register(name, password)))
        {
            NewPassword.Clear();
            Say($"Account {name} registered: log in with it in the game's Email field.", false);
            await Load(name);
        }
    }

    async void Create(object sender, RoutedEventArgs e)
    {
        if (AccountGrid.SelectedItem is not AccountRow account) { Say("Select an account first.", true); return; }
        string name = CharacterName.Text.Trim(), password = AccountPassword.Password;
        if (name.Length == 0 || password.Length == 0) { Say("Enter the account's password and the character's name.", true); return; }
        string characterClass = (string)CharacterClass.SelectedItem;
        if (await Host(() => DevHost.CreateCharacter(account.Name, password, name, characterClass)))
        {
            CharacterName.Clear();
            Say($"{name} ({characterClass}) created on {account.Name}.", false);
            await Load(account.Name);
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

    /// <summary>The dev protocol's error codes (AuthError) in words.</summary>
    internal static string Explain(string message) => message
        .Replace("CharacterNameTaken", "that character name is taken")
        .Replace("InvalidCharacterName", "character names are 3–32 letters, digits, _ or -")
        .Replace("NameTaken", "that account name is taken")
        .Replace("InvalidName", "names are 3–32 letters, digits, _ or -")
        .Replace("WeakPassword", "the password needs at least 4 characters")
        .Replace("BadCredentials", "wrong password")
        .Replace("LimitReached", "the account has the most characters it can have");

    void Say(string text, bool error)
    {
        Status.Text = text;
        Status.Foreground = Theme.Brush(error ? "Shell.Error" : "Shell.Good");
    }
}

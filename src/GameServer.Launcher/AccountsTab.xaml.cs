using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameServer.Core.Accounts;

namespace GameServer.Launcher;

public partial class AccountsTab : UserControl
{
    Shared _shared = null!;

    public AccountsTab() => InitializeComponent();

    internal void Attach(Shared shared)
    {
        _shared = shared;
        CharacterClass.ItemsSource = Enum.GetNames<CharacterClass>();
        CharacterClass.SelectedItem = nameof(GameServer.Core.Accounts.CharacterClass.Witch);
        shared.Monitor.Sampled += samples => Dispatcher.BeginInvoke(() =>
        {
            bool up = samples.FirstOrDefault(s => s.Service == Service.DevHost)?.Running == true;
            HostState.Text = up ? $"Dev host running on port {Service.DevHost.PortList}" : "Dev host stopped (started when needed)";
            StopHost.IsEnabled = up;
        });
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Load(); };
        Load();
    }

    void Load(string? select = null)
    {
        string? keep = select ?? (AccountGrid.SelectedItem as AccountRow)?.Name;
        try
        {
            var accounts = AccountList.Load();
            AccountGrid.ItemsSource = accounts;
            AccountGrid.SelectedItem = accounts.FirstOrDefault(a => a.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
            if (accounts.Count == 0) Say("No accounts yet: register one below.", false);
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Say($"Can't read {GameServer.Storage.SaveDatabase.DefaultPath}: {e.Message}", true);
        }
    }

    void AccountSelected(object sender, SelectionChangedEventArgs e)
    {
        var account = AccountGrid.SelectedItem as AccountRow;
        CharacterGrid.ItemsSource = account?.CharacterRows;
        ForAccount.Text = account?.Name ?? "(select an account above)";
    }

    void Refresh(object sender, RoutedEventArgs e) => Load();

    void StopDevHost(object sender, RoutedEventArgs e) => _shared.StopDevHost();

    async void Register(object sender, RoutedEventArgs e)
    {
        string name = NewAccount.Text.Trim(), password = NewPassword.Password;
        if (name.Length == 0 || password.Length == 0) { Say("Enter a name and a password.", true); return; }
        if (await Host(() => DevHost.Register(name, password)))
        {
            NewPassword.Clear();
            Say($"Account {name} registered: log in with it in the game's Email field.", false);
            Load(name);
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
            Load(account.Name);
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
        Status.Foreground = error ? Brushes.Firebrick : Brushes.SeaGreen;
    }
}

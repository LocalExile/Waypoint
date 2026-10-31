using System.IO;
using System.Windows;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class MainWindow
{
    string accountsContext = "";
    bool accountsLoading;
    void InitializeAccounts()
    {
        AccountsView.RefreshRequested += async () => await LoadAccountsAsync();
        AccountsView.ActionRequested += async (action, account) => await EditAccountAsync(action, account);
        AccountsView.BackupChanged += enabled => { if (IsLoaded) SaveDevelopmentPreferences(enabled, settings.HotReload); };
    }
    string CurrentAccountContext => snapshot is null ? "" : settings.RepositoryPath + "\n" + settings.UseLocalProfile + "\n" + snapshot.SaveDatabase;
    void RenderAccounts(RuntimeSnapshot? state)
    {
        if (state is null || accountsContext.Length > 0 && accountsContext != CurrentAccountContext)
        {
            accountsContext = ""; AccountsView.SetInventory(null);
        }
        AccountsView.SetRuntime(state, busy, settings.AutomaticBackups);
    }
    async Task LoadAccountsAsync()
    {
        if (accountsLoading || busy || snapshot is null) return;
        if (!await gate.WaitAsync(0)) return;
        accountsLoading = true;
        var requestedSettings = settings;
        AccountsView.SetLoading(true);
        try
        {
            var result = await runtime.RunAsync(requestedSettings, "QueryAccounts", cancellation: discoveryCancellation.Token);
            if (closing || requestedSettings.RepositoryPath != settings.RepositoryPath || requestedSettings.UseLocalProfile != settings.UseLocalProfile) return;
            if (!result.Ok || result.AccountData is null || result.Snapshot is null) throw new IOException(result.Message);
            snapshot = result.Snapshot;
            accountsContext = CurrentAccountContext; AccountsView.SetInventory(result.AccountData);
            foreach (var a in result.AccountData.Accounts)
            {
                privateNames.Add(a.Name); foreach (var c in a.Characters) privateNames.Add(c.Name);
            }
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (closing || requestedSettings.RepositoryPath != settings.RepositoryPath || requestedSettings.UseLocalProfile != settings.UseLocalProfile) return;
            accountsContext = CurrentAccountContext; // Retry on explicit reload/page entry, rather than every status poll.
            AccountsView.SetInventory(null); AccountsView.Feedback(ex.Message, true);
        }
        finally { accountsLoading = false; AccountsView.SetLoading(false); gate.Release(); RenderAccounts(snapshot); }
    }
    async Task EditAccountAsync(string action, PlayerAccount? selected)
    {
        if (busy || accountsLoading || snapshot is null || action != "Create" && selected is null) return;
        string reviewedContext = CurrentAccountContext, reviewedDatabase = snapshot.SaveDatabase;
        if (accountsContext != reviewedContext) { RenderAccounts(snapshot); AccountsView.Feedback("Reload accounts after changing the workspace.", true); return; }
        var dialog = new AccountActionDialog(this, action, selected, AccountsView.AllAccounts, settings.AutomaticBackups, reviewedDatabase);
        if (dialog.ShowDialog() != true || dialog.Request is not { } request) return;
        if (reviewedContext != CurrentAccountContext) { AccountsView.Feedback("Reload accounts after changing the workspace.", true); return; }
        request = request with { ExpectedDatabase = reviewedDatabase };
        privateNames.Add(request.Name);
        if (request.CharacterNames is not null) foreach (string value in request.CharacterNames.Values) privateNames.Add(value);
        if (action == "Create") await OperateAsync("Register", account: new(request.Name, request.Password, reviewedDatabase));
        else await OperateAsync("ManageAccount", accountData: request);
        if (action == "Create" || lastError is not null) await LoadAccountsAsync();
    }
}

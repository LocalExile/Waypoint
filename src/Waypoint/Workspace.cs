using System.Text;

namespace Waypoint;

/// <summary>
/// The checkout as Waypoint's adapter sees it (scripts/RuntimeBridge.ps1): the last inspection, and the operations the
/// sections run through it, the setup steps, mods, save backups and the offline account tools. Starts and stops stay
/// live.ps1 switches (<see cref="Shared.RunLiveAsync"/>); an operation takes the same lock as a live.ps1 run, and refuses
/// to start beside one.
/// </summary>
sealed class Workspace(Shared shared)
{
    readonly RuntimeClient _client = new(BridgeScripts.Extract());
    readonly StringBuilder _operations = new();
    bool _inspecting;

    /// <summary>The last successful inspection; null before the first one.</summary>
    public RuntimeSnapshot? Latest { get; private set; }
    /// <summary>Why the last inspection failed.</summary>
    public string? InspectError { get; private set; }
    /// <summary>The last operation's failure, for a diagnostic report.</summary>
    public string? LastFailure { get; private set; }
    /// <summary>The recent operations' output, for a diagnostic report.</summary>
    public string OperationText => _operations.ToString();
    /// <summary>Account and character names seen: a diagnostic report leaves them out.</summary>
    public HashSet<string> PrivateNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A new inspection or operation result.</summary>
    public event Action? Changed;
    /// <summary>An operation's stage and its detail.</summary>
    public event Action<string, string>? Stage;
    /// <summary>An operation's output, as it comes.</summary>
    public event Action<string>? Output;
    /// <summary>An operation failed: its stage, why, and its log when it has one.</summary>
    public event Action<string, string, string?>? Failed;

    public async Task RefreshAsync()
    {
        if (_inspecting || shared.IsLiveRunning) return;
        _inspecting = true;
        try
        {
            var result = await _client.RunAsync(shared.Settings.ForBridge, "Inspect");
            Latest = result is { Ok: true, Snapshot: not null } ? result.Snapshot : Latest;
            InspectError = result.Ok ? null : result.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            InspectError = e.Message;
        }
        finally { _inspecting = false; }
        Changed?.Invoke();
    }

    /// <summary>The checkout's accounts from its save database, with no server running or built; null with the reason in
    /// <paramref name="problem"/> when they cannot be read.</summary>
    public async Task<(AccountInventory? Inventory, string? Problem)> ReadAccountsAsync()
    {
        try
        {
            var result = await _client.RunAsync(shared.Settings.ForBridge, "QueryAccounts");
            if (result is not { Ok: true, AccountData: { } inventory }) return (null, result.Message);
            if (result.Snapshot is not null) { Latest = result.Snapshot; Changed?.Invoke(); }
            foreach (var account in inventory.Accounts)
            {
                PrivateNames.Add(account.Name);
                foreach (var character in account.Characters) PrivateNames.Add(character.Name);
            }
            return (inventory, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Runs one adapter operation; null when a live.ps1 run, git or another operation holds the checkout. A failure
    /// is in the returned result, and reported through <see cref="Failed"/> unless the caller asks a question about it itself
    /// (<paramref name="quiet"/>).</summary>
    public async Task<BridgeResult?> OperateAsync(string action, string name, ModRequest? mod = null, string? backupId = null,
        AccountRequest? account = null, AccountDataRequest? accountData = null, bool quiet = false)
    {
        BridgeResult? result = null;
        string stage = name, logPath = "";
        bool ran = await shared.RunExclusiveAsync(name, async () =>
        {
            LastFailure = null;
            Write($"> {name}");
            Stage?.Invoke(name, "");
            try
            {
                result = await _client.RunAsync(shared.Settings.ForBridge, action, log: Write, progress: update =>
                {
                    stage = update.Stage;
                    if (!string.IsNullOrEmpty(update.LogPath)) logPath = update.LogPath;
                    Stage?.Invoke(update.Stage, update.Detail);
                }, account: account, backupId: backupId, mod: mod, accountData: accountData);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                result = new(false, e.Message, null, stage);
            }
            if (result.Snapshot is not null) Latest = result.Snapshot;
        });
        if (!ran) return null;
        Stage?.Invoke("", "");
        if (result!.Ok) Write(result.Message);
        else
        {
            LastFailure = result.Message;
            Write($"failed: {result.Message}");
            shared.Logs.Write($"{name} failed: {result.Message}", Severity.Error);
            if (!quiet) Failed?.Invoke(result.FailedStage ?? stage, result.Message, logPath.Length > 0 ? logPath : null);
        }
        Changed?.Invoke();
        return result;
    }

    void Write(string text)
    {
        shared.Logs.Write(text.TrimEnd());
        _operations.AppendLine(text.TrimEnd());
        if (_operations.Length > 150_000) _operations.Remove(0, _operations.Length - 100_000);
        Output?.Invoke(text.TrimEnd());
    }
}

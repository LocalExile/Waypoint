using Launcher.Core;

namespace Waypoint;

/// <summary>What Simple mode's Release | Latest switch should do next.</summary>
enum EditionStep { Stay, SetUpRelease, SetUpLatest, Switch }

/// <summary>
/// Simple mode's Release | Latest switch: a release install (LocalExile's tested zip) and a source copy (LocalExile's main,
/// which Play keeps up to date), each in a folder of its own with its own characters. Waypoint remembers the last of each it
/// ran on. Characters go one way only, from a release to Latest: a newer LocalExile may save what an older release can't read.
/// </summary>
static class Editions
{
    /// <summary>Notes the folder Waypoint runs on as the release or the Latest one.</summary>
    public static void Remember(Settings settings, string root)
    {
        bool release = LocalExileReleases.IsInstall(root);
        if (!release && !Repo.IsRoot(root)) return;
        if (string.Equals(release ? settings.ReleaseFolder : settings.LatestFolder, root, StringComparison.OrdinalIgnoreCase)) return;
        if (release) settings.ReleaseFolder = root; else settings.LatestFolder = root;
        settings.Save();
    }

    /// <summary>The remembered folder of that side, while it still is one.</summary>
    public static string? Folder(Settings settings, bool release) =>
        (release ? settings.ReleaseFolder : settings.LatestFolder) is { Length: > 0 } folder
        && (release ? LocalExileReleases.IsInstall(folder) : Repo.IsRoot(folder)) ? folder : null;

    /// <summary>The questions of a switch, answered through <paramref name="ask"/> (a dialog: its buttons' index, -1 when closed),
    /// with the saves brought or backed up as answered. Nothing changes until the player has chosen.</summary>
    public static async Task<(EditionStep Step, string? Folder)> PrepareAsync(Shared shared, bool toRelease,
        Func<DialogKind, string, string, string[], int> ask)
    {
        if (toRelease == Repo.IsInstall) return (EditionStep.Stay, null);
        if (shared.IsLiveRunning || shared.Monitor.Latest.Any(s => s.Running))
        {
            ask(DialogKind.Info, L.Text("Stop LocalExile first"), L.Text("Press Stop playing, then switch."), [L.Text("OK")]);
            return (EditionStep.Stay, null);
        }
        static string LessStable() => L.Text("Latest has LocalExile's newest changes. Every change is reviewed, but Latest may be less stable than the release.");
        static string OneWay() => L.Text("Characters can't go from Latest to a release: a newer LocalExile may save things an older release can't read.");
        string? folder = Folder(shared.Settings, toRelease);
        if (folder is null)
            return toRelease
                ? ask(DialogKind.Info, L.Text("You don't have a release yet"),
                      L.Text("The setup guide downloads one. It starts with its own characters; yours stay here in Latest.") + "\n\n" + OneWay(),
                      [L.Text("Open the setup guide"), L.Text("Cancel")]) == 0 ? (EditionStep.SetUpRelease, null) : (EditionStep.Stay, null)
                : ask(DialogKind.Info, L.Text("You don't have Latest yet"),
                      LessStable() + "\n\n" + L.Text("The setup guide downloads LocalExile's newest version and builds it, and can bring your characters."),
                      [L.Text("Open the setup guide"), L.Text("Cancel")]) == 0 ? (EditionStep.SetUpLatest, null) : (EditionStep.Stay, null);

        // One question each way. The saves here are backed up as every other change backs them up: with automatic backups on
        // (Backups), and the question says so; off, nothing is asked about it.
        bool backup = shared.Settings.AutomaticBackups;
        string backupNote = backup ? "\n\n" + L.Text("Your saves here are backed up first.") : "";
        if (toRelease)
        {
            int choice = ask(DialogKind.Warning, L.Text("Switch to the release?"),
                L.Text("The release has its own characters. Yours stay here in Latest for when you switch back.") + "\n\n" + OneWay() + backupNote,
                [L.Text("Switch"), L.Text("Cancel")]);
            if (choice != 0) return (EditionStep.Stay, null);
            return !backup || await BackedUp(shared) ? (EditionStep.Switch, folder) : (EditionStep.Stay, null);
        }

        int keep = ask(DialogKind.Question, L.Text("Bring your characters to Latest?"),
            LessStable() + "\n\n" + L.Text("Latest has its own characters. Bring yours from this release, and Latest's are backed up and set aside first.") + "\n\n" + OneWay() + backupNote,
            [L.Text("Bring my characters"), L.Text("Keep Latest's own"), L.Text("Cancel")]);
        if (keep is < 0 or 2) return (EditionStep.Stay, null);
        if (backup && !await BackedUp(shared)) return (EditionStep.Stay, null);
        if (keep == 0)
        {
            var brought = await shared.Workspace.ImportSavesAsync(folder, Environment.CurrentDirectory);
            if (brought is not { Ok: true })
            {
                if (brought is not null)
                    ask(DialogKind.Error, L.Text("Your characters weren't brought"), L.Describe(brought.Message) + "\n\n" + L.Text("Nothing was switched."), [L.Text("OK")]);
                return (EditionStep.Stay, null);
            }
        }
        return (EditionStep.Switch, folder);
    }

    /// <summary>A copy on the tested version (<see cref="SwitchToSource.TestedBranch"/>, which Play never moves) onto LocalExile's
    /// default branch, which Play then keeps up to date: the saves backed up first, nothing done with changes of the player's own.
    /// The next Play fetches and updates as it always does. True when it moved.</summary>
    public static async Task<bool> MoveToNewestAsync(Shared shared, Func<DialogKind, string, string, string[], int> ask)
    {
        if (shared.IsLiveRunning || shared.Monitor.Latest.Any(s => s.Running))
        {
            ask(DialogKind.Info, L.Text("Stop LocalExile first"), L.Text("Press Stop playing, then try again."), [L.Text("OK")]);
            return false;
        }
        if (ask(DialogKind.Question, L.Text("Get the newest changes?"),
                L.Text("This copy moves from its older version to LocalExile's newest changes, and Play then updates it each day. The newest changes may be less stable. Your saves are backed up first."),
                [L.Text("Get the newest changes"), L.Text("Cancel")]) != 0) return false;
        if ((await Git.ChangesAsync()).Count > 0)
        {
            ask(DialogKind.Warning, L.Text("This copy has changes of your own"), L.Text("Waypoint leaves it as it is. Keep or remove them first."), [L.Text("OK")]);
            return false;
        }
        if (shared.Settings.AutomaticBackups && !await BackedUp(shared)) return false;
        Git.Result? moved = null;
        await shared.RunGitAsync(async () => moved = await Git.SwitchToBranchAsync(await Git.DefaultBranchAsync(), line => shared.Logs.Write($"git: {line}")));
        if (moved is not { Ok: true })
        {
            if (moved is not null) ask(DialogKind.Error, L.Text("The copy didn't move"), moved.Output, [L.Text("OK")]);
            return false;
        }
        // Due at once: the next Play fetches, backs up and moves forward, with its way back.
        shared.Settings.LastUpdateCheck = null;
        shared.Settings.Save();
        await shared.Workspace.RefreshAsync();
        return true;
    }

    /// <summary>The copy onto the last LocalExile made for the player's older client (<see cref="ClientUpdate.Kind.UseOlder"/>):
    /// a branch of its own (<see cref="SwitchToSource.OlderBranch"/>) that Play never moves, the saves backed up first, nothing
    /// done with changes of the player's own. True when it moved.</summary>
    public static async Task<bool> MoveToOlderAsync(Shared shared, string build, Func<DialogKind, string, string, string[], int> ask)
    {
        if (ClientLocator.LastVersionFor(build) is not { } older) return false;
        if (shared.IsLiveRunning || shared.Monitor.Latest.Any(s => s.Running))
        {
            ask(DialogKind.Info, L.Text("Stop LocalExile first"), L.Text("Press Stop playing, then try again."), [L.Text("OK")]);
            return false;
        }
        if ((await Git.ChangesAsync()).Count > 0)
        {
            ask(DialogKind.Warning, L.Text("This copy has changes of your own"), L.Text("Waypoint leaves it as it is. Keep or remove them first."), [L.Text("OK")]);
            return false;
        }
        if (shared.Settings.AutomaticBackups && !await BackedUp(shared)) return false;
        string branch = SwitchToSource.OlderBranch(build);
        void Log(string line) => shared.Logs.Write($"git: {line}");
        Git.Result? moved = null;
        await shared.RunGitAsync(async () => moved = (await Git.LocalBranchesAsync()).Contains(branch)
            ? await Git.RunAsync(Log, "switch", branch)
            : await Git.RunAsync(Log, "switch", "--no-track", "-c", branch, older.Commit));
        if (moved is not { Ok: true })
        {
            if (moved is not null) ask(DialogKind.Error, L.Text("The copy didn't move"), moved.Output, [L.Text("OK")]);
            return false;
        }
        await shared.Workspace.RefreshAsync();
        return true;
    }

    static async Task<bool> BackedUp(Shared shared) =>
        await shared.Workspace.OperateAsync("Backup", L.Text("Back up now")) is { Ok: true };
}

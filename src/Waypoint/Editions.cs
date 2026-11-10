using Launcher.Core;

namespace Waypoint;

/// <summary>What Simple mode's Release | Latest switch should do next.</summary>
enum EditionStep { Stay, SetUpRelease, SetUpLatest, Switch }

/// <summary>
/// The copies of LocalExile and the mode each is shown in. A release install (LocalExile's tested zip) and a source copy
/// (LocalExile's source, "Latest") are each a folder of their own with their own characters; Waypoint works on one at a time,
/// and a change of folder moves no files. Simple mode is for a release; a source copy is shown in Advanced mode
/// (<see cref="Advanced"/>). Characters go from a release to a source copy, and back only when the release is the same as or
/// newer than that copy (<see cref="ReleaseTakesAsync"/>): a newer LocalExile may save what an older release can't read.
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

    /// <summary>Whether a release install's Simple mode shows the Release | Latest switch and Play's "Set up a new copy…"
    /// (a copy from source). Simple mode plays the release; the Latest side, LocalExile's source, is for Advanced mode, so
    /// only someone with Developer Mode on is shown the way to it.</summary>
    public static bool Offered(Settings settings) => Repo.IsInstall && settings.Developer;

    /// <summary>Someone who was in Simple mode on a source copy before Simple mode became the release's alone. Play is not
    /// offered there: its page asks once for one of two things, the published release (Simple mode stays) or Advanced mode
    /// (this copy stays, with branches and pull requests), each one click away, so that nobody is left anywhere. Whoever
    /// had Advanced mode, is new, or goes to a source copy on purpose (<see cref="LeftFor"/>) is not asked.</summary>
    public static bool MustChoose(Settings settings) => !Repo.IsInstall && !settings.Advanced && settings.SourceChoiceMade != true;

    /// <summary>The mode shown, which follows the folder and is never written: on a release the user's own choice
    /// (<see cref="Settings.Advanced"/>), on a source copy Advanced mode, but for the one page of <see cref="MustChoose"/>.
    /// Going to a source copy therefore leaves the choice made on a release as it was.</summary>
    public static bool Advanced(Settings settings) => Repo.IsInstall ? settings.Advanced : !MustChoose(settings);

    /// <summary>Whether two paths name one folder, as far as their spelling tells: case is ignored on Windows, and a link or a
    /// mapped drive is not followed.</summary>
    public static bool SameFolder(string a, string b)
    {
        static string Clean(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(Clean(a), Clean(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Noted before Waypoint starts again on <paramref name="folder"/>: a source copy someone goes to on purpose is
    /// Advanced mode's, with nothing to ask there.</summary>
    public static void LeftFor(Settings settings, string folder)
    {
        if (!LocalExileReleases.IsInstall(folder)) settings.SourceChoiceMade = true;
    }

    static bool Running(Shared shared) => shared.Monitor.Latest.Any(s => s.Running);

    /// <summary>Before Waypoint leaves this folder for another, by whatever way: an operation is waited for, and a game or
    /// servers still running are stopped first, on the user's word. Waypoint finds what runs by its name, not by its folder, so
    /// what is left running would show in the next folder as that folder's own. Whoever works on LocalExile may leave it
    /// running all the same. False when the change of folder should not go on.</summary>
    public static async Task<bool> ReadyToLeaveAsync(Shared shared, Func<DialogKind, string, string, string[], int> ask)
    {
        if (shared.IsLiveRunning)
        {
            ask(DialogKind.Info, L.Text("{0} is still running", shared.BusyWith), L.Text("Wait for the current operation before changing checkout."), [L.Text("OK")]);
            return false;
        }
        if (!Running(shared)) return true;
        bool mayLeave = Advanced(shared.Settings) || shared.Settings.Developer;
        int answer = ask(DialogKind.Question, L.Text("LocalExile is still running"),
            L.Text("The game or its servers are running from this folder. Waypoint stops them before it opens the other folder; the servers save first."),
            mayLeave ? [L.Text("Stop and switch"), L.Text("Switch without stopping"), L.Text("Cancel")] : [L.Text("Stop and switch"), L.Text("Cancel")]);
        shared.Logs.Write($"change of folder while something runs: answer {answer}{(mayLeave ? " (may leave it running)" : "")}", Severity.Info);
        if (mayLeave && answer == 1) return true;
        if (answer != 0) return false;
        await shared.StopAllAsync(_ => { });
        // The monitor looks again every two seconds; a Gateway takes a moment to save and end.
        for (int i = 0; i < 60 && Running(shared); i++) await Task.Delay(500);
        if (!Running(shared)) return true;
        ask(DialogKind.Warning, L.Text("LocalExile is still running"),
            L.Text("Something did not stop. Close the game or stop the servers yourself, then change folder again."), [L.Text("OK")]);
        return false;
    }

    /// <summary>The source copy whose characters wait to come to this release, while that folder still is one.</summary>
    public static string? BringFrom(Settings settings) =>
        Repo.IsInstall && settings.BringFrom is { Length: > 0 } from && Repo.IsRoot(from) ? from : null;

    /// <summary>Whether the release in <paramref name="releaseFolder"/> can take the characters of the source copy in <paramref name="from"/>: its code is the
    /// same as that copy's or newer, which is the step every update of a release makes. A copy that is ahead of the release,
    /// on a branch the release does not hold, or changed by its owner may have saved what this release can't read; why, then,
    /// in words for its user.</summary>
    public static async Task<(bool Ok, string Why)> ReleaseTakesAsync(string from, string releaseFolder)
    {
        if (LocalExileReleases.InstalledCommit(releaseFolder) is not { } release)
            return (false, L.Text("This release does not say which version of LocalExile's code it was built from, so Waypoint can't tell whether your characters fit it."));
        try
        {
            var head = await Git.RunAsync(null, "-C", from, "rev-parse", "HEAD");
            if (head.Code != 0) return (false, L.Text("Waypoint could not read the version of the copy in {0}.", from));
            string source = head.Output.Trim();
            var changes = await Git.RunAsync(null, "-C", from, "status", "--porcelain", "--untracked-files=no");
            if (changes.Code != 0 || changes.Output.Trim().Length > 0)
                return (false, L.Text("The copy in {0} has changes of your own to LocalExile's code, so its characters may not fit this release.", from));
            if (source.StartsWith(release, StringComparison.OrdinalIgnoreCase) || release.StartsWith(source, StringComparison.OrdinalIgnoreCase)) return (true, "");
            var older = await Git.RunAsync(null, "-C", from, "merge-base", "--is-ancestor", source, release);
            if (older.Code is not (0 or 1))
            {
                // The release's commit is not in that copy yet: newer than anything it has fetched.
                await Git.RunAsync(null, "-C", from, "fetch", "--quiet", "origin");
                older = await Git.RunAsync(null, "-C", from, "merge-base", "--is-ancestor", source, release);
            }
            return older.Code switch
            {
                0 => (true, ""),
                1 => (false, L.Text("The copy in {0} is newer than this release, or on a branch that is not in it. Its characters can come once a newer release is out: update LocalExile then, and try again.", from)),
                _ => (false, L.Text("Waypoint could not compare the copy in {0} with this release.", from)),
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, L.Text("Waypoint could not compare the copy in {0} with this release.", from));
        }
    }

    /// <summary>The characters of the source copy its user left for this release, brought here when they fit
    /// (<see cref="ReleaseTakesAsync"/>): that folder is only read, and this release's own saves are backed up and set aside
    /// first (the adapter's ImportSaves). True when they came.</summary>
    public static async Task<bool> BringToReleaseAsync(Shared shared, Func<DialogKind, string, string, string[], int> ask)
    {
        if (BringFrom(shared.Settings) is not { } from) return false;
        if (shared.IsLiveRunning || Running(shared))
        {
            ask(DialogKind.Info, L.Text("Stop LocalExile first"), L.Text("Press Stop playing, then try again."), [L.Text("OK")]);
            return false;
        }
        var (fits, why) = await ReleaseTakesAsync(from, Environment.CurrentDirectory);
        shared.Logs.Write($"characters from {from} to this release: {(fits ? "they fit" : "not now: " + why)}", Severity.Info);
        if (!fits)
        {
            ask(DialogKind.Info, L.Text("Your characters can't come yet"), why + "\n\n" + L.Text("They are safe where they are, in {0}.", from), [L.Text("OK")]);
            return false;
        }
        if (ask(DialogKind.Question, L.Text("Bring your characters to the release?"),
                L.Text("Your accounts, characters and stash in {0} are copied here; that folder is only read. This release's own are backed up and set aside first.", from),
                [L.Text("Bring my characters"), L.Text("Cancel")]) != 0) return false;
        var brought = await shared.Workspace.ImportSavesAsync(Environment.CurrentDirectory, from);
        if (brought is not { Ok: true })
        {
            if (brought is not null) ask(DialogKind.Error, L.Text("Your characters weren't brought"), L.Describe(brought.Message), [L.Text("OK")]);
            return false;
        }
        shared.Settings.BringFrom = null;
        shared.Settings.Save();
        return true;
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
        static string OneWay() => L.Text("Characters go back to a release only when it is the same as or newer than Latest: a newer LocalExile may save things an older release can't read.");
        static string Later() => L.Text("On the release's Play page Waypoint offers to bring your characters, when the release is the same as or newer than this copy.");
        // Leaving a source copy for the release: its characters are the ones to offer there.
        void Waiting() { shared.Settings.BringFrom = Environment.CurrentDirectory; shared.Settings.Save(); }
        (EditionStep, string?) Go(EditionStep step) { Waiting(); return (step, null); }
        string? folder = Folder(shared.Settings, toRelease);
        if (folder is null)
            return toRelease
                ? ask(DialogKind.Info, L.Text("You don't have a release yet"),
                      L.Text("The setup guide downloads one. It starts with its own characters; yours stay here in Latest.") + "\n\n" + Later(),
                      [L.Text("Open the setup guide"), L.Text("Cancel")]) == 0 ? Go(EditionStep.SetUpRelease) : (EditionStep.Stay, null)
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
                L.Text("The release has its own characters. Yours stay here in Latest for when you switch back.") + "\n\n" + Later() + backupNote,
                [L.Text("Switch"), L.Text("Cancel")]);
            if (choice != 0) return (EditionStep.Stay, null);
            if (backup && !await BackedUp(shared)) return (EditionStep.Stay, null);
            Waiting();
            return (EditionStep.Switch, folder);
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

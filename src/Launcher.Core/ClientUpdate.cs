using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// LocalExile moves to a new game client now and then (v0.2.0-beta: 3.29.3b to 3.29.3c), and a folder whose client and
/// LocalExile are of different builds cannot log in. The rule: a player who has a client of the build current LocalExile needs
/// plays on it; one who has only an older client keeps the last LocalExile made for it, with a quiet note that it will not
/// last. When a folder is about to be used (Play, Start servers), Waypoint looks at that folder's client, and for a client of
/// the current build at the usual places, never at every client on the PC, and says what fits before anything starts
/// (<see cref="Kind"/>). A client of a build Waypoint does not know is left to LocalExile's own check.
/// </summary>
public static class ClientUpdate
{
    public enum Kind
    {
        /// <summary>The client is older than the folder's LocalExile needs, and a client of the needed build is on the PC: the old
        /// one is taken out (asked first), and the setup guide brings the new one, as LocalExile's own update steps say.</summary>
        Replace,
        /// <summary>The client is older than the folder's LocalExile needs, and no client of the needed build was found: the
        /// client stays, and the folder goes back to the last LocalExile made for the client the player has.</summary>
        UseOlder,
        /// <summary>The folder's LocalExile is older than the client it has, or than one found on the PC: LocalExile needs the
        /// update, not the client.</summary>
        UpdateLocalExile,
    }

    /// <param name="Have">The client build the player has: this folder's, or the one found (<paramref name="Found"/>).</param>
    /// <param name="Need">The client build this folder's LocalExile needs.</param>
    /// <param name="Link">Client\ is a link (a shared or linked client): only the link goes.</param>
    /// <param name="Target">The folder the link leads to, or Client\ itself.</param>
    /// <param name="Found">A client of the build to move to, elsewhere on the PC.</param>
    /// <param name="Older">For <see cref="Kind.UseOlder"/>: the last release and source commit made for <paramref name="Have"/>.</param>
    public sealed record Plan(Kind Kind, string Folder, string Have, string Need, bool Link, string Target, double Gigabytes,
        string? Found = null, ClientLocator.OlderVersion? Older = null);

    /// <summary>A quick look at the usual places for a client of a build (a few seconds at most); its folder, or null.</summary>
    public static Func<string, CancellationToken, string?> FindClient { get; set; } = (build, stop) =>
        ClientLocator.Find([], stop, TimeSpan.FromSeconds(2)).Clients.FirstOrDefault(c => c.Usable && c.Build == build)?.Folder;

    /// <summary>What the folder needs before a start; null when its client fits (or is not one Waypoint knows). With
    /// <paramref name="lookForNewer"/>, a folder whose LocalExile is older than current LocalExile but fits its client is also
    /// checked for a current client on the PC, so that the older version is kept only while it is all the player has.</summary>
    public static Plan? Check(string folder, bool lookForNewer = false, CancellationToken stop = default)
    {
        if (ClientLocator.Needed(folder) is not { } need) return null;
        string client = Path.Combine(folder, "Client");
        string? have = Directory.Exists(client) ? ClientLocator.BuildOf(client) : null;
        if (have is null) return null;
        bool link = new DirectoryInfo(client).Attributes.HasFlag(FileAttributes.ReparsePoint);
        double gigabytes = 0;
        try { gigabytes = new FileInfo(Path.Combine(client, "Content.ggpk")).Length / 1e9; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        string target = link ? ClientLocator.RealPath(client) : client;

        if (ClientLocator.IsOlder(have, need))
            return FindClient(need, stop) is { } found
                ? new(Kind.Replace, folder, have, need, link, target, gigabytes, found)
                : ClientLocator.LastVersionFor(have) is { } older ? new(Kind.UseOlder, folder, have, need, link, target, gigabytes, null, older) : null;
        if (ClientLocator.IsOlder(need, have))
            return new(Kind.UpdateLocalExile, folder, have, need, link, target, gigabytes);
        if (lookForNewer && ClientLocator.IsOlder(need, ClientLocator.Build) && FindClient(ClientLocator.Build, stop) is { } newer)
            return new(Kind.UpdateLocalExile, folder, ClientLocator.Build, need, link, target, gigabytes, newer);
        return null;
    }

    public static string Title(Plan plan) => plan.Kind switch
    {
        Kind.Replace => L.Text("Replace the game client?"),
        Kind.UseOlder => L.Text("Use the version for your client?"),
        _ => L.Text("Update LocalExile?"),
    };

    public static string Explain(Plan plan) => plan.Kind switch
    {
        Kind.Replace => Moving(plan),
        Kind.UseOlder => L.Text("This LocalExile needs the Path of Exile {0} client. The client in this folder is {1}, and no {0} client was found on this PC.", plan.Need, plan.Have)
            + "\n\n" + L.Text("Your client stays as it is, and you play on the last LocalExile made for {0}. Once you have a {1} client, Play offers the move.", plan.Have, plan.Need),
        _ => plan.Found is { } found
            ? L.Text("LocalExile has moved on to the Path of Exile {0} client, and you have it in {1}. This folder still runs the LocalExile made for {2}: update it to play with {0}. Your saves, accounts and login keys stay.", plan.Have, found, plan.Need)
            : L.Text("This LocalExile works only with the Path of Exile {0} client, and the client in this folder is {1}. LocalExile needs an update that supports {1}. Your saves, accounts and login keys stay.", plan.Need, plan.Have),
    };

    public static string Confirm(Plan plan) => plan.Kind == Kind.UseOlder ? L.Text("Use the version for {0}", plan.Have) : L.Text("Update LocalExile");

    /// <summary>A release cannot go back in place (a newer release's modules would stay behind), so its older version is set up
    /// in a folder of its own by the guide; this one stays as it is.</summary>
    public static string ReleaseOlderNote(Plan plan) =>
        L.Text("Waypoint sets up LocalExile {0} in a new folder with the setup guide. This folder stays as it is, with its saves.", plan.Older?.Release ?? "");

    /// <summary>The quiet note for a folder on a LocalExile made for an older client than current LocalExile's: null when it is
    /// on the current one. Not a warning: the older version still works, it is only not kept up.</summary>
    public static string? OlderNote(string folder) =>
        ClientLocator.Needed(folder) is { } need && ClientLocator.IsOlder(need, ClientLocator.Build) ? OlderNoteFor(need) : null;

    public static string OlderNoteFor(string build) =>
        L.Text("LocalExile now uses Path of Exile {0}. When you can, move to it: versions for {1} won't be offered after 3.30 comes out.", ClientLocator.Build, build);

    /// <summary>What LocalExile.exe is given after the old client was taken out: the client found, always copied. A link would let
    /// LocalExile write into the player's own install (Game.exe, Game.patched.exe, the login list), which the official launcher may
    /// later update under it; LocalExile's own notes say to keep its copy apart.</summary>
    public static string[] ClientArguments(Plan plan) => ["--client", plan.Found!, "--client-mode", "copy"];

    /// <summary>The move still leaves a working setup when the player says no: the folder's client fits its (older) LocalExile, and
    /// only a newer client was found elsewhere.</summary>
    public static bool WorksAnyway(Plan plan) => plan.Kind == Kind.UpdateLocalExile && plan.Found is not null;

    /// <summary>Folders whose player said Not now to a move that leaves a working setup: not asked again while Waypoint runs.</summary>
    static readonly HashSet<string> Declined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The build the player moves to, and the build of this folder's client now.</summary>
    static (string To, string From) Builds(Plan plan) => plan.Kind == Kind.Replace ? (plan.Need, plan.Have) : (plan.Have, plan.Need);

    public static string MoveTitle(Plan plan) => plan.Found is null ? L.Text("Update LocalExile?") : L.Text("Move to Path of Exile {0}?", Builds(plan).To);

    /// <summary>What a move does, folder by folder, so that the player can back up either one first; and what Not now does.</summary>
    public static string MoveText(Plan plan)
    {
        if (plan.Found is null) return Explain(plan) + "\n\n" + L.Text("Not now changes nothing; this folder can't start until LocalExile and its client fit.");
        var (to, from) = Builds(plan);
        var lines = new List<string>();
        if (plan.Kind == Kind.UpdateLocalExile)
            lines.Add(L.Text("This folder runs the LocalExile made for {0}. Waypoint first updates it to the version for {1}.", from, to));
        lines.Add(L.Text("Your Path of Exile {0} client in {1} is copied into this LocalExile folder. That folder is only read.", to, plan.Found));
        lines.Add(plan.Link
            ? L.Text("The link to your old {0} client is removed. {1} stays as it is.", from, plan.Target)
            : L.Text("Your old {0} client in {1} is deleted for good ({2:0.#} GB).", from, plan.Target, plan.Gigabytes));
        lines.Add(L.Text("Your saves, accounts and login keys stay. To back up either folder first, open it from here, then press Continue."));
        lines.Add(WorksAnyway(plan)
            ? L.Text("Not now changes nothing and plays on {0} this time.", from)
            : L.Text("Not now changes nothing; this folder can't start until its client fits."));
        return string.Join("\n\n", lines);
    }

    /// <summary>The folders the move dialog offers to open: the client found, and an old copy that will be deleted.</summary>
    public static IReadOnlyList<(string Label, string Path)> MoveFolders(Plan plan)
    {
        var folders = new List<(string, string)>();
        if (plan.Found is { } found) folders.Add((L.Text("Open the {0} client", Builds(plan).To), found));
        if (plan.Found is not null && !plan.Link && Directory.Exists(plan.Target)) folders.Add((L.Text("Open the old client"), plan.Target));
        return folders;
    }

    /// <summary>The move dialog: Continue (true), Not now (false), or a folder opened by <paramref name="open"/> and the dialog
    /// shown again. <paramref name="show"/> shows a question with buttons and answers the one chosen (any other answer is Not now).</summary>
    public static bool AskMove(Plan plan, Func<string, string, string[], int> show, Action<string> open)
    {
        var folders = MoveFolders(plan);
        string[] buttons = [L.Text("Continue"), .. folders.Select(f => f.Label), L.Text("Not now")];
        while (true)
        {
            int chosen = show(MoveTitle(plan), MoveText(plan), buttons);
            if (chosen == 0) return true;
            if (chosen < 1 || chosen > folders.Count) return false;
            open(folders[chosen - 1].Path);
        }
    }

    /// <summary>What the player reads while Waypoint moves them to the client found.</summary>
    public static string Moving(Plan plan) => L.Text("Moving you to your Path of Exile {0} client in {1}.", plan.Need, plan.Found) + " "
        + (plan.Link ? L.Text("The link to your old {0} client was removed; that folder stays as it is.", plan.Have)
            : L.Text("The old {0} copy in Client\\ was removed ({1:0.#} GB). Your saves, accounts and login keys stay.", plan.Have, plan.Gigabytes));

    /// <summary>The client build a release is made for, from its notes ("For the Path of Exile **3.29.3c** client"); null when
    /// they do not say.</summary>
    public static string? ClientOf(LocalExileRelease release) =>
        Regex.Match(release.Notes, @"Path of Exile \*\*(\d+\.\d+\.\d+[a-z]?)\*\*") is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>A release folder onto LocalExile's newest release, its saves copied first when <paramref name="backUp"/>; the
    /// release installed, or null when there is none newer. Throws as <see cref="LocalExileReleases.InstallAsync"/> does.</summary>
    public static async Task<LocalExileRelease?> UpdateReleaseAsync(HttpClient http, string folder, bool backUp, Action<string> say, CancellationToken stop = default)
    {
        var latest = await LocalExileReleases.LatestAsync(http, stop);
        var installed = LocalExileReleases.Installed(folder);
        if (latest is null || installed is not null && !latest.Version.IsNewerThan(installed)) return null;
        if (backUp)
        {
            say(L.Text("Copying your saves …"));
            LocalExileReleases.BackUpSaves(folder, installed, DateTime.Now);
        }
        bool extracting = false;
        await LocalExileReleases.InstallAsync(http, latest, folder,
            (done, total) => say(extracting ? L.Text("Installing: {0:0} of {1:0} MB", done / 1e6, total / 1e6) : L.Text("Downloading: {0:0} of {1:0} MB", done / 1e6, total / 1e6)),
            () => extracting = true, stop);
        return latest;
    }

    /// <summary>A source copy's way to the newer LocalExile when Waypoint cannot move it itself.</summary>
    public static string HowToUpdateCopy => L.Text("Update this copy to the newest LocalExile: on the Versions section in Advanced mode, or with Get the newest changes on Play in Simple mode.");

    /// <summary>Takes the old client out (<see cref="ClientLocator.RemoveClient"/>); throws, with nothing lost, when the game
    /// still holds one of its files.</summary>
    public static Task RemoveAsync(Plan plan) => Task.Run(() => ClientLocator.RemoveClient(plan.Folder));

    /// <summary>Why the client could not be removed, for the player.</summary>
    public static string Failed(Exception e) =>
        L.Text("The old client could not be removed: {0}\nClose the game and anything else using that folder, then try again.", e.Message);

    public enum Outcome
    {
        /// <summary>The client fits: start as usual.</summary>
        Fits,
        /// <summary>The old client is gone: the setup guide brings the new one, nothing starts now.</summary>
        Replaced,
        /// <summary>The player chose the older LocalExile for their client (<see cref="Plan.Older"/>): the caller moves to it.</summary>
        UseOlder,
        /// <summary>The player chose LocalExile's update: the caller offers it.</summary>
        UpdateLocalExile,
        /// <summary>The player said no, or the removal failed: nothing starts.</summary>
        Stopped,
    }

    /// <summary>Before a start: checks the folder. A move to the client of the build current LocalExile needs is asked first
    /// (<paramref name="askMove"/>, true to go on), naming the folders so the player can back them up; Not now changes nothing, and
    /// plays on when the old setup still works (<see cref="WorksAnyway"/>, then not asked again while Waypoint runs). Already
    /// <paramref name="confirmed"/> (the second round after LocalExile's update) asks nothing. A client too old is then taken out
    /// while <paramref name="exclusive"/> holds the folder (false: something else holds it; a failure goes to <paramref name="tell"/>),
    /// and <see cref="Outcome.UpdateLocalExile"/> is the caller's to carry out. The older version, for a player who has nothing
    /// newer, is asked by <paramref name="askOlder"/>.</summary>
    public static async Task<(Outcome Outcome, Plan? Plan)> EnsureAsync(string folder, bool lookForNewer, Func<Plan, bool> askMove, Func<Plan, bool> askOlder,
        Action<string> tell, Func<Func<Task>, Task<bool>> exclusive, bool confirmed = false)
    {
        if (await Task.Run(() => Check(folder, lookForNewer)) is not { } plan) return (Outcome.Fits, null);
        if (plan.Kind == Kind.UseOlder) return (askOlder(plan) ? Outcome.UseOlder : Outcome.Stopped, plan);
        string key = Path.GetFullPath(folder);
        if (WorksAnyway(plan) && Declined.Contains(key)) return (Outcome.Fits, plan);
        if (!confirmed && !askMove(plan))
        {
            if (!WorksAnyway(plan)) return (Outcome.Stopped, plan);
            Declined.Add(key);
            return (Outcome.Fits, plan);
        }
        if (plan.Kind == Kind.UpdateLocalExile) return (Outcome.UpdateLocalExile, plan);
        Exception? failure = null;
        bool ran = await exclusive(async () =>
        {
            try { await RemoveAsync(plan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure = e; }
        });
        if (failure is not null) tell(Failed(failure));
        return (ran && failure is null ? Outcome.Replaced : Outcome.Stopped, plan);
    }
}

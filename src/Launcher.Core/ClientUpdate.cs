using System.Text.RegularExpressions;

namespace Launcher.Core;

/// <summary>
/// LocalExile moves to a new game client now and then (v0.2.0-beta: 3.29.3b to 3.29.3c), and a folder whose client is no
/// version its LocalExile runs cannot log in. A LocalExile can run several versions at once (v0.5.0-beta: 3.29.3c and
/// 3.29.3d), and which ones is its own to say (<see cref="ClientLocator.Supported"/>): a client of any of them fits. The
/// rule: a player who has a client of a version current LocalExile runs plays on it; one who has only an older client keeps
/// the last LocalExile made for it, with a quiet note that it will not last. Waypoint looks when a folder is about to start the game (Play), at that folder's
/// client and, for a client of another build, at the usual places (<see cref="FindClient"/>), never at every client on the PC,
/// and settles what fits before anything starts (<see cref="Kind"/>). A move is asked once (<see cref="AskMove"/>) and the
/// answer kept for the folder while Waypoint runs. A client of a build Waypoint does not know is left to LocalExile's own check.
/// </summary>
public static class ClientUpdate
{
    public enum Kind
    {
        /// <summary>The client is older than the folder's LocalExile needs, and a client of the needed build is on the PC: the old
        /// one is taken out, and the found one comes in (LocalExile.exe's setup for a release, the setup guide for a checkout).</summary>
        Replace,
        /// <summary>The folder's client is older than its LocalExile needs (or it has none), and no client of the needed build
        /// was found: the folder goes back to the last LocalExile made for the client the player has.</summary>
        UseOlder,
        /// <summary>The folder's LocalExile is older than the client it has, or than one found on the PC: LocalExile needs the
        /// update, not the client.</summary>
        UpdateLocalExile,
    }

    /// <param name="Have">The client build the player has: this folder's, or the one found (<paramref name="Found"/>).</param>
    /// <param name="Need">The client build this folder's LocalExile needs.</param>
    /// <param name="Link">Client\ is a link (a shared or linked client): only the link goes.</param>
    /// <param name="Target">The folder the link leads to, or Client\ itself.</param>
    /// <param name="Found">A client found elsewhere on the PC: the one to move to, or for <see cref="Kind.UseOlder"/> the older one.</param>
    /// <param name="Older">For <see cref="Kind.UseOlder"/>: the last release and source commit made for <paramref name="Have"/>.</param>
    public sealed record Plan(Kind Kind, string Folder, string Have, string Need, bool Link, string Target, double Gigabytes,
        string? Found = null, ClientLocator.OlderVersion? Older = null);

    /// <summary>The player's other LocalExile folders (Waypoint's settings), whose clients are looked at first.</summary>
    public static Func<IEnumerable<string?>> KnownFolders { get; set; } = () => [];

    /// <summary>A quick look for a client of a build (a few seconds at most): the player's LocalExile folders and the usual places
    /// first. A plain client goes before one another LocalExile prepared, whose patched executable trusts that folder's keys,
    /// and a standalone one before one from Steam, which only a LocalExile from v0.5.0-beta on runs (<see cref="Check"/>
    /// passes over one its folder cannot run). Its folder, or null.</summary>
    public static Func<string, CancellationToken, string?> FindClient { get; set; } = (build, stop) =>
        ClientLocator.Find(KnownFolders().OfType<string>().Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase), stop, TimeSpan.FromSeconds(2))
            .Clients.Where(c => c.Usable && c.Build == build).OrderBy(c => c.Kind == ClientKind.Ready ? 1 : 0).ThenBy(c => c.FromSteam ? 1 : 0).FirstOrDefault()?.Folder;

    /// <summary>What the folder needs before the game starts; null when its client fits (or is not one Waypoint knows). With
    /// <paramref name="lookForNewer"/>, a folder whose LocalExile is older than current LocalExile but fits its client is also
    /// checked for a current client on the PC, so that the older version is kept only while it is all the player has.
    /// <paramref name="find"/> stands for <see cref="FindClient"/>.</summary>
    public static Plan? Check(string folder, bool lookForNewer = false, CancellationToken stop = default, Func<string, CancellationToken, string?>? find = null)
    {
        var look = find ?? FindClient;
        // A client from Steam is one to move to only for a LocalExile that runs its files.
        find = (version, cancel) => look(version, cancel) is { } at
            && (ClientLocator.SteamIndexOf(at) is not { } index || ClientLocator.SteamFilesFit(LocalExileBuilds.Of(folder), version, index)) ? at : null;
        // Every version this folder's LocalExile runs, newest first: a client of any of them fits.
        var supported = ClientLocator.Supported(folder);
        if (supported.Count == 0) return null;
        string need = supported[0], oldest = supported[^1];
        (string Version, string Folder)? FindAmong(IEnumerable<string> versions)
        {
            foreach (string version in versions)
                if (find(version, stop) is { } at) return (version, at);
            return null;
        }
        string client = Path.Combine(folder, "Client");
        string? have = Directory.Exists(client) ? ClientLocator.BuildOf(client) : null;
        if (have is null)
        {
            // No client yet (or none Waypoint knows): only a player with nothing but an older client needs a word here.
            if (Directory.Exists(client) || FindAmong(supported) is not null) return null;
            foreach (string older in ClientLocator.OlderBuilds.Where(b => ClientLocator.IsOlder(b, oldest)))
                if (find(older, stop) is { } olderClient)
                    return For(Kind.UseOlder, folder, older, need, olderClient, ClientLocator.LastVersionFor(older));
            return null;
        }
        if (supported.Contains(have))
        {
            // It fits. A folder still on a LocalExile made for an older client is told when a current client is on the PC.
            if (lookForNewer && !supported.Any(ClientLocator.IsCurrent) && FindAmong(ClientLocator.Current) is { } newer)
                return For(Kind.UpdateLocalExile, folder, newer.Version, have, newer.Folder);
            return null;
        }
        if (ClientLocator.IsOlder(have, oldest))
            return FindAmong(supported) is { } found
                ? For(Kind.Replace, folder, have, found.Version, found.Folder)
                : ClientLocator.LastVersionFor(have) is { } last ? For(Kind.UseOlder, folder, have, need, null, last) : null;
        if (ClientLocator.IsOlder(need, have))
            return For(Kind.UpdateLocalExile, folder, have, need);
        // A version between two this LocalExile runs: LocalExile's own check has the last word.
        return null;
    }

    /// <summary>The version of the older client a folder's LocalExile was made for, when it runs none of the current ones
    /// (<see cref="ClientLocator.Current"/>); null for a folder on a current LocalExile, or one that does not say.</summary>
    public static string? OlderVersionOf(string folder) =>
        ClientLocator.Supported(folder) is { Count: > 0 } supported && !supported.Any(ClientLocator.IsCurrent) ? supported[0] : null;

    /// <summary>A quick look for a client of any version current LocalExile runs, the newest first; its folder, or null.</summary>
    public static string? FindCurrent(CancellationToken stop = default) =>
        ClientLocator.Current.Select(version => FindClient(version, stop)).FirstOrDefault(folder => folder is not null);

    /// <summary>A plan with what the folder's own client is: a link or a copy, where, and how big.</summary>
    static Plan For(Kind kind, string folder, string have, string need, string? found = null, ClientLocator.OlderVersion? older = null)
    {
        string client = Path.Combine(folder, "Client");
        bool exists = Directory.Exists(client);
        bool link = exists && new DirectoryInfo(client).Attributes.HasFlag(FileAttributes.ReparsePoint);
        double gigabytes = 0;
        try
        {
            string archive = Path.Combine(client, "Content.ggpk");
            if (exists) gigabytes = (File.Exists(archive) ? new FileInfo(archive).Length : ClientVersions.SizeOf(Path.Combine(client, "Bundles2"))) / 1e9;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new(kind, folder, have, need, link, link ? ClientLocator.RealPath(client) : client, gigabytes, found, older);
    }

    /// <summary>Before a source copy moves forward to a LocalExile that needs client <paramref name="next"/> (what was Simple
    /// mode's daily update, which is no longer reached: docs/launcher.md, "No longer reached"), off the UI thread: Go when its client fits, the build does not change, or the player agreed already. A client of
    /// <paramref name="next"/> found elsewhere gives the question to ask (<see cref="Answer"/> keeps the reply). With only the
    /// older client, or after a Not now, the copy stays, and <c>Staying</c> says why.</summary>
    public static (bool Go, Plan? Ask, string? Staying) BeforeUpdate(string folder, string? next, Func<string, CancellationToken, string?>? find = null)
    {
        find ??= FindClient;
        string key = Key(folder);
        string client = Path.Combine(folder, "Client");
        if (next is null || ClientLocator.Needed(folder) is not { } now || now == next) return (true, null, null);
        if (!Directory.Exists(client) || ClientLocator.BuildOf(client) is not { } have || have == next || !ClientLocator.IsOlder(have, next)) return (true, null, null);
        if (Agreed.ContainsKey(key)) return (true, null, null);
        if (!Declined.Contains(key) && find(next, default) is { } found) return (false, For(Kind.UpdateLocalExile, folder, next, have, found), null);
        return (false, null, Staying(have, next));
    }

    /// <summary>Keeps the player's reply to a move: Continue goes on with the client found, Not now is not asked again while
    /// Waypoint runs. The text for a Not now, or null for a Continue.</summary>
    public static string? Answer(Plan plan, bool yes)
    {
        string key = Key(plan.Folder);
        if (yes && plan.Found is { } found)
        {
            Agreed[key] = found;
            return null;
        }
        Declined.Add(key);
        return Staying(plan.Need, plan.Have);
    }

    /// <summary>Drops a Continue kept for the folder: what it was for failed, so the next start asks again.</summary>
    public static void Forget(string folder) => Agreed.Remove(Key(folder));

    static string Staying(string have, string next) =>
        L.Text("This copy stays on the LocalExile made for {0}: the newest one needs a {1} client.", have, next);

    public static string Title(Plan plan) => plan.Kind switch
    {
        Kind.Replace => L.Text("Replace the game client?"),
        Kind.UseOlder => L.Text("Use the version for your client?"),
        _ => L.Text("Update LocalExile?"),
    };

    public static string Explain(Plan plan) => plan.Kind switch
    {
        Kind.Replace => Moving(plan),
        Kind.UseOlder => L.Text("This LocalExile needs Path of Exile {0}, but only a {1} client was found.", plan.Need, plan.Have),
        _ => plan.Found is { } found
            ? L.Text("LocalExile has moved on to the Path of Exile {0} client, and you have it in {1}. This folder still runs the LocalExile made for {2}: update it to play with {0}. Your saves, accounts and login keys stay.", plan.Have, found, plan.Need)
            : L.Text("This LocalExile works only with the Path of Exile {0} client, and the client in this folder is {1}. LocalExile needs an update that supports {1}. Your saves, accounts and login keys stay.", plan.Need, plan.Have),
    };

    public static string Confirm(Plan plan) => plan.Kind == Kind.UseOlder ? L.Text("Use the version for {0}", plan.Have) : L.Text("Update LocalExile");

    /// <summary>The question for a player who has only an older client: short, with what happens and that it won't last. A release
    /// cannot go back in place (a newer release's modules would stay behind), so its older version is set up in a folder of its
    /// own by the guide; a source copy moves to its own branch.</summary>
    public static string OlderText(Plan plan, bool release) =>
        Explain(plan) + "\n\n"
        + (release
            ? L.Text("Waypoint can set up LocalExile {0}, the last version for {1}, in a new folder. This folder stays as it is.", plan.Older?.Release ?? "", plan.Have)
            : L.Text("Waypoint can switch this copy to the last LocalExile for {0}. Your client stays as it is.", plan.Have))
        + "\n\n" + L.Text("Versions for {0} won't be offered after 3.30 comes out.", plan.Have);

    /// <summary>The quiet note for a folder on a LocalExile made for an older client than current LocalExile's: null when it is
    /// on the current one. Not a warning: the older version still works, it is only not kept up.</summary>
    public static string? OlderNote(string folder) => OlderVersionOf(folder) is { } older ? OlderNoteFor(older) : null;

    public static string OlderNoteFor(string build) =>
        L.Text("LocalExile now uses Path of Exile {0}. When you can, move to it: versions for {1} won't be offered after 3.30 comes out.", ClientLocator.Build, build);

    /// <summary>What LocalExile.exe's setup is given after the old client was taken out: the client found, always copied. A link
    /// would let LocalExile write into the player's own install (Game.exe, Game.patched.exe, the login list), which the official
    /// launcher may later update under it; LocalExile's own notes say to keep its copy apart.</summary>
    public static string[] ClientArguments(Plan plan) => ["--client", plan.Found!, "--client-mode", "copy"];

    /// <summary>After LocalExile.exe copied <paramref name="found"/> in: a client another LocalExile prepared brought its patched
    /// executable, which trusts that folder's keys, so it goes and the next run patches one for this folder's; and the game
    /// settings Client\config.json named come back (<see cref="ClientLocator.RestoreSettings"/>).</summary>
    public static void AfterNewClient(string folder, string found)
    {
        if (ClientLocator.Classify(found) == ClientKind.Ready)
        {
            string patched = Path.Combine(folder, "Client", "Game.patched.exe");
            if (File.Exists(patched)) File.Delete(patched);
        }
        ClientLocator.RestoreSettings(folder);
    }

    /// <summary>The move still leaves a working setup when the player says no: the folder's client fits its (older) LocalExile, and
    /// only a newer client was found elsewhere.</summary>
    public static bool WorksAnyway(Plan plan) => plan.Kind == Kind.UpdateLocalExile && plan.Found is not null;

    /// <summary>Folders whose player said Not now to a move: not asked again while Waypoint runs.</summary>
    static readonly HashSet<string> Declined = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders whose player said Continue, with the client found then: the steps after LocalExile's update ask nothing
    /// and use that client, not a second search's.</summary>
    static readonly Dictionary<string, string> Agreed = new(StringComparer.OrdinalIgnoreCase);
    static string Key(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>The build the player moves to, and the build of this folder's client now.</summary>
    static (string To, string From) Builds(Plan plan) => plan.Kind == Kind.Replace ? (plan.Need, plan.Have) : (plan.Have, plan.Need);

    public static string MoveTitle(Plan plan) => plan.Found is null ? L.Text("Update LocalExile?") : L.Text("Move to Path of Exile {0}?", Builds(plan).To);

    /// <summary>What a move does, kept short: the message says what happens and what Not now does; the details (set apart in the
    /// dialog) name the two folders, so that the player can back either one up first.</summary>
    public static (string Message, string? Details) MoveText(Plan plan)
    {
        string notNow = WorksAnyway(plan)
            ? L.Text("Not now: nothing changes, and you play on {0} this time.", Builds(plan).From)
            : L.Text("Not now: nothing changes, but this folder can't start yet.");
        if (plan.Found is null) return (Explain(plan) + "\n\n" + notNow, null);
        var (to, from) = Builds(plan);
        string lead = plan.Kind == Kind.UpdateLocalExile
            ? L.Text("Path of Exile {0} was found on this PC. Waypoint updates LocalExile, then copies it over your current client.", to)
            : L.Text("Path of Exile {0} was found on this PC. Waypoint can copy it over your current client.", to);
        string message = lead + " " + L.Text("Your saves, accounts and login keys are kept. To back up first, open a folder below.") + "\n\n" + notNow;
        // What happens first, the path on a line of its own: a long path scrolls, and the words before it stay in view.
        string current = plan.Link
            ? L.Text("Current {0} client (a link: only the link is replaced):", from)
            : plan.Gigabytes >= 0.1
                ? L.Text("Current {0} client (replaced, {1:0.#} GB):", from, plan.Gigabytes)
                : L.Text("Current {0} client (replaced):", from);
        return (message, L.Text("{0} client found (only read):", to) + "\n  " + plan.Found + "\n" + current + "\n  " + plan.Target);
    }

    /// <summary>The folders the move dialog offers to open: the client found, and a current copy that will be replaced.</summary>
    public static IReadOnlyList<(string Label, string Path)> MoveFolders(Plan plan)
    {
        var folders = new List<(string, string)>();
        if (plan.Found is { } found) folders.Add((L.Text("Open {0} client", Builds(plan).To), found));
        if (plan.Found is not null && !plan.Link && Directory.Exists(plan.Target)) folders.Add((L.Text("Open current client"), plan.Target));
        return folders;
    }

    /// <summary>The move dialog: Continue (true), Not now (false), or a folder opened by <paramref name="open"/> and the dialog
    /// shown again. <paramref name="show"/> shows a question (title, message, details, buttons) and answers the button chosen
    /// (any other answer is Not now).</summary>
    public static bool AskMove(Plan plan, Func<string, string, string?, string[], int> show, Action<string> open)
    {
        var folders = MoveFolders(plan);
        string[] buttons = [L.Text("Continue"), .. folders.Select(f => f.Label), L.Text("Not now")];
        var (message, details) = MoveText(plan);
        while (true)
        {
            int chosen = show(MoveTitle(plan), message, details, buttons);
            if (chosen == 0) return true;
            if (chosen < 1 || chosen > folders.Count) return false;
            open(folders[chosen - 1].Path);
        }
    }

    /// <summary>What the player reads while Waypoint moves them to the client found.</summary>
    public static string Moving(Plan plan) => L.Text("Moving you to your Path of Exile {0} client in {1}.", plan.Need, plan.Found) + " "
        + (plan.Link ? L.Text("The link to your old {0} client was removed; that folder stays as it is.", plan.Have)
            : L.Text("The old {0} client was removed. Your saves, accounts and login keys stay.", plan.Have));

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
    public static string HowToUpdateCopy => L.Text("Update this copy to the newest LocalExile on the Versions section.");

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
        /// <summary>The old client is gone: the client found comes in (<see cref="Plan.Found"/>), nothing has started yet.</summary>
        Replaced,
        /// <summary>The player chose the older LocalExile for their client (<see cref="Plan.Older"/>): the caller moves to it.</summary>
        UseOlder,
        /// <summary>The player chose LocalExile's update: the caller carries it out, then checks again.</summary>
        UpdateLocalExile,
        /// <summary>The player said no, or the removal failed: nothing starts.</summary>
        Stopped,
    }

    /// <summary>Before the game starts: first what an earlier start left (a removal cut short, game settings to bring back), then
    /// the folder. A move to a client of another build is asked once (<paramref name="askMove"/>, true to go on), naming the
    /// folders so the player can back them up; Not now changes nothing, plays on when the old setup still works
    /// (<see cref="WorksAnyway"/>), and is not asked again while Waypoint runs. Continue is kept with the client found, so the
    /// steps after LocalExile's update ask nothing and use that client. A client too old is taken out while
    /// <paramref name="exclusive"/> holds the folder (false: something else holds it; a failure goes to <paramref name="tell"/>);
    /// <see cref="Outcome.UpdateLocalExile"/> and <see cref="Outcome.UseOlder"/> are the caller's to carry out. The older version,
    /// for a player who has nothing newer, is asked by <paramref name="askOlder"/>.</summary>
    public static async Task<(Outcome Outcome, Plan? Plan)> EnsureAsync(string folder, bool lookForNewer, Func<Plan, bool> askMove, Func<Plan, bool> askOlder,
        Action<string> tell, Func<Func<Task>, Task<bool>> exclusive)
    {
        string key = Key(folder);
        await Task.Run(() =>
        {
            try
            {
                ClientLocator.FinishRemoval(folder);
                ClientLocator.RestoreSettings(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
        bool agreed = Agreed.TryGetValue(key, out string? known);
        Func<string, CancellationToken, string?> find = (build, stop) =>
            agreed && Directory.Exists(known) && ClientLocator.BuildOf(known!) == build ? known : FindClient(build, stop);
        var plan = await Task.Run(() => Check(folder, lookForNewer && !Declined.Contains(key), default, find));
        if (plan is null)
        {
            Agreed.Remove(key);
            return (Outcome.Fits, null);
        }
        if (plan.Kind == Kind.UseOlder) return (askOlder(plan) ? Outcome.UseOlder : Outcome.Stopped, plan);
        if (!agreed)
        {
            if (Declined.Contains(key) && WorksAnyway(plan)) return (Outcome.Fits, plan);
            if (!askMove(plan))
            {
                Declined.Add(key);
                return (WorksAnyway(plan) ? Outcome.Fits : Outcome.Stopped, plan);
            }
            if (plan.Found is { } found) Agreed[key] = found;
        }
        if (plan.Kind == Kind.UpdateLocalExile) return (Outcome.UpdateLocalExile, plan);
        Exception? failure = null;
        bool ran = await exclusive(async () =>
        {
            try { await RemoveAsync(plan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure = e; }
        });
        if (failure is not null) tell(Failed(failure));
        if (!ran || failure is not null) return (Outcome.Stopped, plan);
        Agreed.Remove(key);
        return (Outcome.Replaced, plan);
    }
}

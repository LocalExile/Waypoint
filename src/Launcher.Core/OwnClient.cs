namespace Launcher.Core;

/// <summary>
/// A LocalExile whose <c>Client</c> is a link to the user's own Path of Exile folder (LocalExile's <c>--client-mode link</c>,
/// which is for developers and which neither LocalExile nor Waypoint offers to anyone else) runs from that folder:
/// LocalExile writes Game.exe, Game.patched.exe and the login list there, and a game update in it can break the install.
/// LocalExile says so at every start, and Waypoint says the same on the Play page (<see cref="Notice"/>). Nothing is
/// refused, and nothing is done unasked: what Waypoint adds is the way out LocalExile's warning names, a copy of its own,
/// made on the user's word and without a moment in which the release has no client:
/// <list type="bullet">
/// <item>the copy is made beside the link (<see cref="NewFolder"/>), which keeps working meanwhile;</item>
/// <item>only a copy that is whole, and still matches the folder it came from, takes the link's place;</item>
/// <item>the folder the link led to is only ever read, and removing the link removes nothing in it;</item>
/// <item>a stop or a failure leaves the release as it was, and the next try goes on from what was copied.</item>
/// </list>
/// A link to another LocalExile's client (the <c>Client</c> of a checkout or a release) is shared with it and is left alone,
/// by the rule LocalExile's own warning uses.
/// </summary>
public static class OwnClient
{
    /// <summary>Beside <c>Client</c> while the copy is made.</summary>
    public const string NewFolder = ".waypoint-new-client";
    /// <summary>The link, set aside on Windows while the copy takes its place, so that it can be put back as it was.</summary>
    public const string OldLink = ".waypoint-old-link";
    /// <summary>Written into <see cref="NewFolder"/> once the copy is whole: from then on <see cref="Finish"/> only goes forward.</summary>
    public const string Complete = ".waypoint-complete";

    /// <param name="Source">The folder the link leads to: only read.</param>
    /// <param name="Free">Free bytes on the release's drive; null when it cannot be told.</param>
    public sealed record Plan(string Folder, string Source, ClientCopyPlan Copy, long? Free)
    {
        public bool HasSpace => Free is not { } free || free >= Copy.Remaining + ClientCopy.Margin;
        /// <summary>What an earlier try already copied.</summary>
        public long Copied => Copy.Total - Copy.Remaining;
    }

    public enum Outcome
    {
        /// <summary>Nothing to do, or the user did not want the copy: nothing was changed.</summary>
        None,
        /// <summary>The release has its own client now.</summary>
        Moved,
        /// <summary>The copy failed; the release is as it was.</summary>
        Failed,
        /// <summary>The user stopped the copy; the release is as it was.</summary>
        Stopped,
    }

    public sealed record Result(Outcome Outcome, Plan? Plan = null, string? Reason = null);

    /// <summary>The copy this folder needs; null when its client is a folder of its own, a link to another LocalExile's client,
    /// or a link that leads to no client Waypoint can copy (the setup guide's to replace).</summary>
    public static Plan? Check(string folder)
    {
        try
        {
            string client = Path.Combine(folder, "Client");
            if (!ClientSettings.IsLink(client)) return null;
            string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ClientLocator.RealPath(client)));
            if (ClientLocator.Classify(source) is not (ClientKind.Ready or ClientKind.Standalone) || InLocalExile(source)) return null;
            // A release set up inside the game's own folder would be copied into itself.
            if (Path.GetFullPath(folder).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var copy = ClientCopy.Plan(source, folder, Path.Combine(folder, NewFolder), keepPatched: true);
            return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), source, copy, ClientCopy.FreeSpace(copy.Target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return null; }
    }

    /// <summary>The <c>Client</c> folder of a LocalExile checkout or release: shared with it, and that folder's to look after.
    /// The same test as LocalExile's own warning (<c>ClientInstall.LinkedInstall</c>): the folder above it holds a
    /// <c>GameServer.slnx</c> or a <c>release.json</c>.</summary>
    static bool InLocalExile(string source) =>
        Path.GetDirectoryName(source) is { Length: > 0 } parent && (File.Exists(Path.Combine(parent, "GameServer.slnx")) || File.Exists(Path.Combine(parent, "release.json")));

    /// <summary>The standing line on a release's Play page while its client is such a link: LocalExile's own warning, in
    /// its words.</summary>
    public static string Notice(Plan plan) =>
        L.Text("Client is a link to {0}, not a copy. LocalExile changes files in that folder, and a Path of Exile update there can break this LocalExile. A separate copy is safe from both.", plan.Source);

    public static string Title => L.Text("Make a copy of the game for LocalExile?");

    /// <summary>The question, kept short, and the two folders set apart so that a long path does not push the words away.</summary>
    public static (string Message, string Details) Text(Plan plan)
    {
        string message = plan.HasSpace
            ? L.Text("Waypoint copies the game into this LocalExile folder and then removes the link. The link works until the copy is whole, and your saves, accounts and login keys are kept.")
                + (plan.Copied >= 100_000_000 ? " " + L.Text("The copy goes on from the {0:0.#} GB made earlier.", plan.Copied / 1e9) : "")
            : L.Text("The drive is too full for the copy: it needs {0:0.#} GB, and {1:0.#} GB is free. Free some space and try again; until then nothing changes.",
                (plan.Copy.Remaining + ClientCopy.Margin) / 1e9, (plan.Free ?? 0) / 1e9);
        string details = L.Text("Your Path of Exile folder (only read):") + "\n  " + plan.Source + "\n"
            + L.Text("The copy ({0:0.#} GB):", plan.Copy.Total / 1e9) + "\n  " + Path.Combine(plan.Folder, "Client");
        return (message, details);
    }

    /// <summary>The question: Copy now (true) or Cancel; Open folder shows the user's folder and asks again; a box closed
    /// without an answer is a Cancel. Without the room for the copy there is nothing to agree to, only to know.
    /// <paramref name="show"/> shows a question (title, message, details, buttons) and answers the button chosen.</summary>
    public static bool Ask(Plan plan, Func<string, string, string?, string[], int> show, Action<string> open)
    {
        var (message, details) = Text(plan);
        string[] buttons = plan.HasSpace ? [L.Text("Copy now"), L.Text("Open folder"), L.Text("Cancel")] : [L.Text("OK"), L.Text("Open folder")];
        while (true)
        {
            int chosen = show(Title, message, details, buttons);
            if (chosen != 1) return plan.HasSpace && chosen == 0;
            open(plan.Source);
        }
    }

    /// <summary>What LocalExile added to the user's folder while it ran from it, and their normal game does not need: for the
    /// user to delete, never Waypoint. config.json only when it is the one that names LocalExile's own settings.</summary>
    public static string[] Leftovers(string source)
    {
        var names = new List<string>();
        foreach (string name in new[] { "Game.exe", "Game.patched.exe" })
            if (File.Exists(Path.Combine(source, name))) names.Add(name);
        if (File.Exists(Path.Combine(source, "config.json")) && ClientSettings.Read(source, linked: false).Kind == ClientSettingsKind.Separate) names.Add("config.json");
        return [.. names];
    }

    public static string DoneTitle => L.Text("LocalExile has its own copy of the game");

    public static string DoneText(Plan plan)
    {
        string text = L.Text("LocalExile now runs from {0}, and no longer uses {1}.", Path.Combine(plan.Folder, "Client"), plan.Source);
        var left = Leftovers(plan.Source);
        return left.Length == 0 ? text
            : text + "\n\n" + L.Text("LocalExile left these files in your Path of Exile folder. Your normal game does not need them, and you can delete them: {0}", string.Join(", ", left));
    }

    public static string FailedTitle => L.Text("The copy did not finish");

    public static string FailedText(Plan plan, string reason) =>
        reason + "\n\n" + L.Text("Nothing was changed: LocalExile still runs from {0}. The next try goes on from where the copy stopped.", plan.Source);

    /// <summary>The copy, on the user's word (<paramref name="ask"/>, true to go on): first what an earlier move left
    /// (<see cref="Finish"/>), then the question, then the move while <paramref name="exclusive"/> holds the folder (false:
    /// something else holds it). Whatever comes of it, the release still has a client that works.</summary>
    public static async Task<Result> MakeCopyAsync(string folder, Func<Plan, bool> ask, Action<ClientCopyProgress>? progress,
        Func<Func<Task>, Task<bool>> exclusive, CancellationToken stop)
    {
        var plan = await Task.Run(() =>
        {
            try
            {
                Finish(folder);
                Discard(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return Check(folder);
        });
        if (plan is null || !ask(plan)) return new(Outcome.None, plan);
        Exception? failure = null;
        bool stopped = false;
        bool ran = await exclusive(async () =>
        {
            try { await Task.Run(() => MoveAsync(folder, progress, stop), CancellationToken.None); }
            catch (OperationCanceledException) { stopped = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure = e; }
        });
        if (stopped) return new(Outcome.Stopped, plan);
        if (!ran) return new(Outcome.Failed, plan, L.Text("Something else is using LocalExile. Try again when it is done."));
        return failure is null ? new(Outcome.Moved, plan) : new(Outcome.Failed, plan, failure.Message);
    }

    /// <summary>Copies the client the folder's link leads to into <see cref="NewFolder"/>, checks the copy against that folder as
    /// it is now, and only then lets it take the link's place. Throws with the release as it was.</summary>
    public static async Task MoveAsync(string folder, Action<ClientCopyProgress>? progress, CancellationToken stop)
    {
        var plan = Check(folder) ?? throw new IOException(L.Text("This folder's game client is no longer a link, so there is nothing to copy."));
        // What an earlier try copied counts only while it is still what the folder holds: the official launcher may have
        // updated the game in between.
        bool stale = false;
        foreach (var file in plan.Copy.Files)
            if (File.Exists(file.Target) && !Same(file))
            {
                File.Delete(file.Target);
                stale = true;
            }
        if (stale) plan = Check(folder) ?? throw new IOException(L.Text("This folder's game client is no longer a link, so there is nothing to copy."));
        await ClientCopy.RunAsync(plan.Copy, progress, stop);
        stop.ThrowIfCancellationRequested();
        var now = ClientCopy.Plan(plan.Source, folder, plan.Copy.Target, keepPatched: true);
        if (now.Files.Length != plan.Copy.Files.Length || now.Files.Any(f => !Same(f))
            || ClientLocator.BuildOf(plan.Copy.Target) != ClientLocator.BuildOf(plan.Source))
            throw new IOException(L.Text("The game folder changed while it was copied, so the copy was not used."));
        File.WriteAllText(Path.Combine(plan.Copy.Target, Complete), "");
        if (!Finish(folder)) throw new IOException(L.Text("The copy is ready, but it could not take the link's place."));
    }

    /// <summary>A copy begun for a link that is gone since (the client was replaced another way) is of no use: Waypoint's own
    /// folder, removed. Never while <c>Client</c> is a link, or the copy is whole.</summary>
    public static void Discard(string folder)
    {
        string client = Path.Combine(folder, "Client"), copy = Path.Combine(folder, NewFolder);
        if (!Directory.Exists(copy) || ClientSettings.IsLink(copy) || File.Exists(Path.Combine(copy, Complete))) return;
        if (ClientSettings.IsLink(client) || ClientSettings.IsLink(Path.Combine(folder, OldLink))) return;
        Directory.Delete(copy, recursive: true);
    }

    /// <summary>A copied file is the source's while it has its length and time (the copy keeps the time; some drives round it).</summary>
    static bool Same(ClientCopyFile file)
    {
        FileInfo source = new(file.Source), target = new(file.Target);
        return source.Exists && target.Exists && source.Length == target.Length
            && Math.Abs((source.LastWriteTimeUtc - target.LastWriteTimeUtc).TotalSeconds) <= 2;
    }

    /// <summary>Lets a whole copy (<see cref="Complete"/>) take the link's place, also after a move that was cut short there;
    /// true when the folder's client is the copy now. A link set aside without a whole copy goes back. Nothing is done to a
    /// <c>Client</c> that is a folder of its own, and a copy that cannot be renamed leaves the link where it was.</summary>
    public static bool Finish(string folder)
    {
        string client = Path.Combine(folder, "Client"), copy = Path.Combine(folder, NewFolder), aside = Path.Combine(folder, OldLink);
        bool present = Directory.Exists(client) || ClientSettings.IsLink(client);
        if (!File.Exists(Path.Combine(copy, Complete)))
        {
            if (!present && ClientSettings.IsLink(aside)) Directory.Move(aside, client);
            return false;
        }
        string? target = null;
        if (present)
        {
            if (!ClientSettings.IsLink(client)) return false;
            target = new DirectoryInfo(client).LinkTarget;
            if (OperatingSystem.IsWindows())
            {
                // Renamed, not removed: a junction cannot be made again without a tool, and a rename can be undone.
                if (ClientSettings.IsLink(aside)) Directory.Delete(aside);
                Directory.Move(client, aside);
            }
            else File.Delete(client); // a Unix link is a file
        }
        try { Directory.Move(copy, client); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (present)
            {
                if (OperatingSystem.IsWindows()) Directory.Move(aside, client);
                else if (target is not null) Directory.CreateSymbolicLink(client, target);
            }
            throw;
        }
        File.Delete(Path.Combine(client, Complete));
        if (ClientSettings.IsLink(aside)) Directory.Delete(aside);
        return true;
    }
}

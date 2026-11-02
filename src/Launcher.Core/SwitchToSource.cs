namespace Launcher.Core;

/// <summary>What a player on a release chose on the guide's way to a source copy.</summary>
/// <param name="Accounts">The accounts found in the release, negative when they could not be read; <paramref name="Characters"/> the same.</param>
/// <param name="CopyClientGb">The size of a client copy still to make; 0 when none is.</param>
public sealed record SwitchChoices(string Copy, string Old, bool Download, bool BringCharacters, int Accounts, int Characters,
    bool KeepOld, bool TestedVersion, bool GiveItem, bool SharesClient, double CopyClientGb, bool CopyHasSaves);

/// <summary>A line of "What will happen".</summary>
public sealed record SwitchLine(string Text, bool Attention = false);

/// <summary>The release a player wants removed once the new copy has worked for them.</summary>
public sealed record ReleaseToRemove(string Old, string Copy, bool Played = false);

/// <param name="ClientMoved">The game client moved into the copy; otherwise it is where it was.</param>
public sealed record RemovedRelease(bool ClientMoved);

/// <summary>
/// Moving from a release to a source copy, which has what no release has yet (the item crafter, for one). The release is only
/// read until the player, having played on the copy, asks for it to be removed. The saves are copied by the adapter
/// (<c>ImportSaves</c>); never one database for both, because a newer server may write saves an older release cannot read.
/// </summary>
public static class SwitchToSource
{
    /// <summary>The LocalExile commit this Waypoint was tested with: a release's database opened on it, and an item was given.</summary>
    public const string TestedCommit = "dfcdc8f3786f2839d7ab0dcfaada796a5ebdf214";
    /// <summary>A branch of the copy's own at that commit: only a plain copy on the default branch is moved by Play.</summary>
    public const string TestedBranch = "waypoint-tested";
    /// <summary>What the game's chat box takes, measured: the rest of a longer line is lost. The client control bridge sends
    /// the same message without it, so Give item needs Frida for all but the smallest items.</summary>
    public const int ChatBoxLimit = 175;

    /// <summary>What a release holds besides the player's data, client and mods. Anything else stays.</summary>
    static readonly string[] ReleaseFiles = ["LocalExile.exe", "LocalExileGUI.exe", "LocalExileGUI.ini", "release.json", "README.txt"];
    static readonly string[] ReleaseFolders = ["app", "Tools", "Modules", "Leagues", "scripts", "Agents", "licenses"];

    /// <summary>What the adapter writes last, so that the guide run again never copies over newer progress.</summary>
    public static bool SavesBrought(string copy) => File.Exists(Path.Combine(copy, "data", "saves-brought.json"));

    public static bool OnTestedVersion(string copy)
    {
        try { return File.ReadAllText(Path.Combine(copy, ".git", "HEAD")).Trim() == $"ref: refs/heads/{TestedBranch}"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task ChooseTestedVersionAsync(string copy, Action<string> output, CancellationToken stop)
    {
        int code = await ToolProcess.RunAsync("git", ["-C", copy, "switch", "--no-track", "-c", TestedBranch, TestedCommit], null, output, stop);
        if (code != 0) throw new IOException(L.Text("The tested version could not be chosen (git exit {0}).", code));
    }

    public static bool HasFrida(string copy) =>
        File.Exists(Platform.VenvPython(copy)) && Directory.Exists(Path.Combine(copy, ".venv", "Lib", "site-packages", "frida"));

    /// <summary>Python with Frida in the checkout's own .venv, where the client control bridge looks. git ignores it.</summary>
    public static async Task InstallFridaAsync(string copy, Action<string> output, CancellationToken stop)
    {
        string python = Platform.VenvPython(copy);
        if (!File.Exists(python) && await ToolProcess.RunAsync(Platform.Python, ["-m", "venv", Path.Combine(copy, ".venv")], copy, output, stop) != 0)
            throw new IOException(L.Text("Python could not make its environment in {0}.", copy));
        if (await ToolProcess.RunAsync(python, ["-m", "pip", "install", "--disable-pip-version-check", "frida"], copy, output, stop) != 0)
            throw new IOException(L.Text("Frida could not be installed. Check your connection and try again."));
    }

    /// <summary>The guide's "What will happen", from the choices as they stand.</summary>
    public static IReadOnlyList<SwitchLine> Describe(SwitchChoices c)
    {
        var lines = new List<SwitchLine>
        {
            new(c.Download ? L.Text("Download LocalExile into {0} and build it.", c.Copy) : L.Text("Use the LocalExile in {0}.", c.Copy)),
        };
        if (c.SharesClient) lines.Add(new(L.Text("Use the game client of your current LocalExile, so it takes no extra space.")));
        else if (c.CopyClientGb > 0) lines.Add(new(L.Text("Copy the game client, about {0:0} GB.", c.CopyClientGb)));
        if (c.BringCharacters)
        {
            lines.Add(new(c.Accounts >= 0 ? L.Text("Copy your accounts and characters. Accounts: {0}. Characters: {1}.", c.Accounts, c.Characters)
                : L.Text("Copy your accounts and characters.")));
            if (c.CopyHasSaves) lines.Add(new(L.Text("The characters already in {0} are backed up and set aside.", c.Copy), true));
        }
        else lines.Add(new(L.Text("Start with no accounts or characters. You create an account at the end.")));
        lines.Add(c.GiveItem ? new(L.Text("Install Frida, so that Give item works."))
            : new(L.Text("Leave out Frida. Waypoint offers it again when you press Give item."), true));
        if (c.Download) lines.Add(c.TestedVersion ? new(L.Text("Stay on the tested version.")) : new(L.Text("Update to the newest changes once a day."), true));
        if (c.KeepOld) lines.Add(new(L.Text("Leave your current LocalExile as it is. Each copy keeps its own progress.")));
        else
        {
            lines.Add(new(L.Text("After the new copy has worked for you, ask before deleting the old one."), true));
            if (!c.BringCharacters) lines.Add(new(L.Text("Your old characters stay in {0} until you delete that folder.", Path.Combine(c.Old, "data")), true));
        }
        return lines;
    }

    /// <summary>The guide's last page: where everything is, and the way back.</summary>
    public static string Done(SwitchChoices c)
    {
        var text = new List<string> { L.Text("Your new copy is in {0}. Press Play in Waypoint to start it.", c.Copy) };
        if (c.BringCharacters) text.Add(L.Text("Your characters came with you, so log in as usual. Mods you added yourself did not: they are still in your old folder."));
        text.Add(L.Text("The item crafter is in Tools."));
        text.Add(c.KeepOld ? L.Text("To go back to your other LocalExile, use Change folder… on Play and choose {0}.", c.Old)
            : L.Text("Your old LocalExile is still in {0}. After you have played, Play asks whether to delete it.", c.Old));
        if (c.SharesClient) text.Add(L.Text("The new copy uses the game files in {0}, so do not delete that folder yourself.", c.Old));
        return string.Join("\n\n", text);
    }

    /// <summary>Removes a release's program files. Its data and mods stay, and so does anything it does not ship. A game client
    /// the copy links to moves into the copy when it is on the same drive; on another drive the link stays.</summary>
    public static RemovedRelease RemoveRelease(string old, string copy)
    {
        old = Path.TrimEndingDirectorySeparator(Path.GetFullPath(old));
        copy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copy));
        if (old.Equals(copy, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L.Text("That is the folder Waypoint is using."));
        if (!LocalExileReleases.IsInstall(old)) throw new InvalidOperationException(L.Text("{0} is not a LocalExile release.", old));
        bool moved = MoveClient(old, copy);
        foreach (string name in ReleaseFolders)
        {
            string folder = Path.Combine(old, name);
            if (!Directory.Exists(folder)) continue;
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is not null) { info.Delete(); continue; }
            // Read-only files stop a delete, and a link inside is removed, never followed.
            foreach (var file in info.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                file.Attributes = FileAttributes.Normal;
            info.Delete(recursive: true);
        }
        // Last: until here the folder is still a release, and a stopped removal can be asked for again.
        foreach (string name in ReleaseFiles) File.Delete(Path.Combine(old, name));
        return new(moved);
    }

    static bool MoveClient(string old, string copy)
    {
        string source = Path.Combine(old, "Client"), target = Path.Combine(copy, "Client");
        if (!Directory.Exists(source) || new DirectoryInfo(source).LinkTarget is not null || new DirectoryInfo(target).LinkTarget is null) return false;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(ClientLocator.RealPath(target)), source, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(copy), StringComparison.OrdinalIgnoreCase)) return false;
        // A failed rename leaves everything as it was, so the link goes only once the client is safely beside it.
        string moving = target + ".moving";
        Directory.Move(source, moving);
        Directory.Delete(target);
        Directory.Move(moving, target);
        return true;
    }
}

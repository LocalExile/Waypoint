using System.Net.Http;
using System.Text.Json.Nodes;
using Launcher.Core.NativeMods;
using LocalExile.Client.Waypoint;
#if WINDOWS
using ModIconKind = MaterialDesignThemes.Wpf.PackIconKind;
using ModIconImage = System.Windows.Media.ImageSource;
#else
using ModIconKind = Material.Icons.MaterialIconKind;
using ModIconImage = Avalonia.Media.IImage;
#endif

namespace Waypoint;

/// <summary>What Browse offers for one mod of a repository.</summary>
enum ModOffer
{
    /// <summary>Not installed: a release to download, or source (scripts, or a project to build).</summary>
    Install,
    /// <summary>Installed, and the repository has a newer version.</summary>
    Update,
    /// <summary>Installed at the repository's version or newer.</summary>
    Installed,
}

/// <summary>A row of Browse: a repository's mod, its newest release, and what is installed of it.</summary>
sealed record BrowseItem(ModSource Source, ModListing Listing, ModRelease? Release, ModEntry? Installed)
{
    /// <summary>The version Browse would install: the release's when there is one, the index's otherwise.</summary>
    public ReleaseVersion? Offered => Release?.Version ?? Listing.ParsedVersion;
    public ReleaseVersion? InstalledVersion => Installed is null ? null : ReleaseVersion.Parse(Installed.Version);

    public ModOffer Offer => Installed is null ? ModOffer.Install
        : Offered is { } offered && (InstalledVersion is null || offered.IsNewerThan(InstalledVersion)) ? ModOffer.Update : ModOffer.Installed;

    /// <summary>A project without a release installs as source, built later with the .NET 10 SDK.</summary>
    public bool NeedsBuild => Release is null && Listing.IsProject;

    public string Title => L.Text(Listing.Name);
    public string Byline => L.Text("by {0}", Listing.Author.Length > 0 ? Listing.Author : Source.Owner);
    public string VersionText => Offer switch
    {
        ModOffer.Update => L.Text("{0} → {1}", Installed!.Version, Offered?.ToString() ?? Listing.Version),
        ModOffer.Installed => L.Text("{0} installed", Installed!.Version),
        _ => Offered?.ToString() ?? Listing.Version,
    };
    /// <summary>The install or update running for this mod, shown in place of its button.</summary>
    public ModJob? Job { get; init; }
    public bool HasJob => Job is not null;
    public bool ShowButton => Job is null;
    public bool CanAct => !Repo.LegacyReleaseMods && Offer != ModOffer.Installed && Job is null;
    public string ActionText => Offer switch { ModOffer.Install => L.Text("Install"), ModOffer.Update => L.Text("Update"), _ => L.Text("Installed") };
    /// <summary>Its button is an Update: drawn green.</summary>
    public bool IsUpdate => Offer == ModOffer.Update;
    public string Origin => Release is not null ? L.Text("Release {0}", Release.Tag)
        : Listing.IsProject ? L.Text("No release: installs the source") : L.Text("Scripts: no build needed");
    public string SearchText => $"{Listing.Id} {Listing.Name} {Listing.Author} {Listing.Description} {Title} {Description}";
    public string Initial => ModsText.Initial(Title);
    public ModIconKind Icon => ModsText.Icon(Listing.Id);
    /// <summary>The installed mod's own icon.png, else the repository's.</summary>
    public string IconFile => Installed?.IconFile is { Length: > 0 } own ? own : ModRepository.IconFile(Source, Listing.Id);
    public ModIconImage? IconImage => ModsText.IconImage(IconFile);
    /// <summary>Without an icon.png of its own, a mod shows its symbol.</summary>
    public bool ShowSymbol => IconImage is null;
    public string Description => L.Text(Listing.Description);
    public bool HasDescription => Description.Length > 0;
    public string Metadata => string.Join(" · ", new[] { Author, NativeModsBadgeText }.Where(s => s.Length > 0));
    /// <summary>The LocalExile versions it declares; the list shows these, not the mod's own version.</summary>
    public IReadOnlyList<string> ClientVersions => Listing.ClientVersions ?? [];
    public string ClientText => ModsText.ClientText(ClientVersions);
    /// <summary>It names the LocalExile versions it works with, and this checkout's is not one of them.</summary>
    public bool Incompatible => !ClientCompatibility.Fits(ClientVersions, Repo.ClientVersion);
    public string? IncompatibleTip => Incompatible ? ModsText.IncompatibleTip(ClientVersions) : null;
    /// <summary>Under the package status: the version change of an update.</summary>
    public string VersionChange => Offer == ModOffer.Update ? VersionText : "";
    public bool HasVersionChange => VersionChange.Length > 0;
    public string PackageStatus => Offer == ModOffer.Update ? L.Text("Update available") : Offer == ModOffer.Installed ? L.Text("Installed")
        : NeedsBuild ? L.Text("Source project") : Release is not null ? L.Text("Release") : L.Text("Scripts");
    public bool ShowOfferAction => Job is null && Offer != ModOffer.Installed;
    public bool ShowDetails => Job is null && Offer == ModOffer.Installed;
    public string Author => Listing.Author.Length > 0 ? Listing.Author : Source.Owner;
    public string Latest => Offered?.ToString() ?? Listing.Version;
    /// <summary>Under the name: what an install takes (a release, scripts, or a build) .</summary>
    public string Subtitle => Release is not null ? Release.ZipName : Listing.IsProject ? L.Text("Source project") : L.Text("Scripts");
    public string TypeText => Listing.IsProject ? L.Text("Project") : L.Text("Scripts");
    public NativeClientSummary? NativeClient => Listing.NativeClient ?? Installed?.NativeClient;
    public bool HasNativeClient => NativeClient is not null;
    public bool RequiresNativeMods => NativeClient?.Required == true;
    public bool NativeModsDisabled { get; init; }
    public bool ShowNativeModsWarning => HasNativeClient && NativeModsDisabled;
    public string NativeModsBadgeText => !HasNativeClient ? "" : RequiresNativeMods ? L.Text("Requires NativeMods") : L.Text("NativeMods");
    public string? NativeModsWarningTip => ShowNativeModsWarning
        ? (RequiresNativeMods
            ? L.Text("Requires the NativeMods client extender. Enable it in Settings.")
            : L.Text("Includes a NativeMods client payload. Enable NativeMods in Settings for full client features."))
        : null;
}

/// <summary>A row of a mod page's Versions: one release, installable when it is not the installed version.</summary>
sealed record VersionRow(BrowseItem Item, ModRelease Release)
{
    public string Version => Release.Version.ToString();
    public string Date => Release.Published?.ToLocalTime().ToString("d") ?? "";
    public string Size => Release.ZipBytes > 0 ? L.Text("{0:N0} KB", Math.Max(1, Release.ZipBytes / 1024)) : "";
    public string Notes
    {
        get
        {
            string text = string.Join(" ", Release.Notes.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return text.Length > 300 ? text[..300] + "…" : text;
        }
    }
    public bool IsInstalled => Item.InstalledVersion is { } installed && !installed.IsNewerThan(Release.Version) && !Release.Version.IsNewerThan(installed);
    public bool CanAct => !Repo.LegacyReleaseMods && !IsInstalled;
    public string ActionText => IsInstalled ? L.Text("Installed") : L.Text("Install");
}

/// <summary>An install, build or uninstall running for one mod: what its row shows in place of its button. Changes are
/// raised on the thread that started it (the UI's), whichever thread reports them.</summary>
sealed class ModJob(string id, string text) : System.ComponentModel.INotifyPropertyChanged
{
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public string Id => id;
    public string Text { get; private set; } = text;
    /// <summary>0 to 100.</summary>
    public double Percent { get; private set; }
    public bool Indeterminate { get; private set; } = true;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void Show(string text, double fraction, bool indeterminate)
    {
        void Apply()
        {
            Text = text;
            Percent = Math.Clamp(fraction, 0, 1) * 100;
            Indeterminate = indeterminate;
            PropertyChanged?.Invoke(this, new(null));
        }
        if (_ui is null || SynchronizationContext.Current == _ui) Apply();
        else _ui.Post(_ => Apply(), null);
    }
}

/// <summary>What a row of My Mods offers to do, in the order it sorts.</summary>
enum RowAction { Update, Reinstall, Build, NotLoaded, UpToDate }

/// <summary>A column My Mods and Browse sort by: its heading is a button.</summary>
enum ModSort { Enabled, Name, Client, Status, Action }

/// <summary>The lists' order: by a column, then by name.</summary>
static class ModSorting
{
    static readonly StringComparer ByName = StringComparer.CurrentCultureIgnoreCase;

    // Text by name, versions as versions; what has none comes first.
    static readonly Comparer<object?> Keys = Comparer<object?>.Create((x, y) => (x, y) switch
    {
        (string a, string b) => ByName.Compare(a, b),
        (ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b),
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        _ => Comparer<object>.Default.Compare(x, y),
    });

    public static IEnumerable<InstalledRow> Sort(IEnumerable<InstalledRow> rows, ModSort by, bool descending) => Order(rows, r => r.Title, descending, by switch
    {
        ModSort.Enabled => r => r.Enabled ? 0 : 1,
        ModSort.Client => r => Newest(r.ClientVersions),
        ModSort.Status => r => r.PackageStatus,
        ModSort.Action => r => (int)r.Action,
        _ => null,
    });

    public static IEnumerable<BrowseItem> Sort(IEnumerable<BrowseItem> rows, ModSort by, bool descending) => Order(rows, r => r.Title, descending, by switch
    {
        ModSort.Client => r => Newest(r.ClientVersions),
        ModSort.Status => r => r.PackageStatus,
        ModSort.Action => r => (int)r.Offer,
        _ => null,
    });

    static IEnumerable<T> Order<T>(IEnumerable<T> rows, Func<T, string> title, bool descending, Func<T, object?>? key)
    {
        if (key is null) return descending ? rows.OrderByDescending(title, ByName) : rows.OrderBy(title, ByName);
        return (descending ? rows.OrderByDescending(key, Keys) : rows.OrderBy(key, Keys)).ThenBy(title, ByName);
    }

    static ReleaseVersion? Newest(IReadOnlyList<string> versions) => versions.Select(ReleaseVersion.Parse).OfType<ReleaseVersion>().Max();

    /// <summary>A column's heading, with an arrow when the list is sorted by it.</summary>
    public static string Heading(ModSort column, ModSort by, bool descending)
    {
        string text = column switch
        {
            ModSort.Enabled => L.Text("Use"),
            ModSort.Client => L.Text("Client version"),
            ModSort.Status => L.Text("Package status"),
            ModSort.Action => L.Text("Action"),
            _ => L.Text("Mod"),
        };
        return column == by ? text + (descending ? " ▼" : " ▲") : text;
    }
}

/// <summary>A row of My Mods: an installed mod, and what a repository offers of it (for Update and its latest version).</summary>
sealed record InstalledRow(ModEntry Entry, BrowseItem? Offer)
{
    public string Title => Entry.Name.Length > 0 ? L.Text(Entry.Name) : Offer?.Title ?? Entry.Id;
    public string Initial => ModsText.Initial(Title);
    public ModIconKind Icon => ModsText.Icon(Entry.Id);
    /// <summary>The installed mod's own icon.png, else its repository's.</summary>
    public string IconFile => Entry.IconFile.Length > 0 ? Entry.IconFile : Offer?.IconFile ?? "";
    public ModIconImage? IconImage => ModsText.IconImage(IconFile);
    /// <summary>Without an icon.png of its own, a mod shows its symbol.</summary>
    public bool ShowSymbol => IconImage is null;
    public string Description => Entry.Description.Length > 0 ? L.Text(Entry.Description) : Offer?.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public string Metadata => string.Join(" · ", new[] { Author, Entry.IsScript ? L.Text("Script mod") : "", NativeModsBadgeText }.Where(s => s.Length > 0));
    public NativeClientSummary? NativeClient => Entry.NativeClient ?? Offer?.NativeClient;
    public bool HasNativeClient => NativeClient is not null;
    public bool RequiresNativeMods => NativeClient?.Required == true;
    public bool NativeModsDisabled { get; init; }
    public bool ShowNativeModsWarning => HasNativeClient && NativeModsDisabled;
    public string NativeModsBadgeText => !HasNativeClient ? "" : RequiresNativeMods ? L.Text("Requires NativeMods") : L.Text("NativeMods");
    public string? NativeModsWarningTip => ShowNativeModsWarning
        ? (RequiresNativeMods
            ? L.Text("Requires the NativeMods client extender. Enable it in Settings.")
            : L.Text("Includes a NativeMods client payload. Enable NativeMods in Settings for full client features."))
        : null;
    /// <summary>The LocalExile versions its installed mod.json declares, else what its repository's listing does.</summary>
    public IReadOnlyList<string> ClientVersions => Entry.ClientVersions is { Count: > 0 } own ? own : Offer?.ClientVersions ?? [];
    public string ClientText => ModsText.ClientText(ClientVersions);
    public bool Incompatible => !ClientCompatibility.Fits(ClientVersions, Repo.ClientVersion);
    public string? IncompatibleTip => Incompatible ? ModsText.IncompatibleTip(ClientVersions) : null;
    public string PackageStatus => HasUpdate ? L.Text("Update available") : !IsBuilt ? L.Text("Not built")
        : Offer is null ? L.Text("Installed") : L.Text("Up to date");
    public string VersionChange => HasUpdate ? L.Text("{0} → {1}", Entry.Version, Latest) : "";
    public bool ShowDetails => Job is null && !HasButton;
    public bool CanToggle => IsBuilt && Job is null;
    public bool Enabled => Entry.Enabled;
    public bool HasUpdate => Offer?.Offer == ModOffer.Update;
    public bool IsBuilt => Entry.Fingerprint.Length > 0;
    public string EnabledTip => L.Text("Saved enable choice for {0}; this does not confirm live load status.", Title);
    /// <summary>Why the running Gateway did not load this mod (its log), or "".</summary>
    public string LoadError { get; init; } = "";
    /// <summary>A league's mod that no league lists: nothing is broken and nothing is to be installed again.</summary>
    public bool Unused => Entry.Enabled && (LoadError.StartsWith(ModCatalog.NoLeagueReason, StringComparison.Ordinal) || Entry.Status == ModCatalog.Unused);
    public bool Failed => Entry.Enabled && LoadError.Length > 0 && !Unused;
    /// <summary>The Action column: Update when a repository has a newer version; Reinstall when the Gateway could not load it
    /// and a repository has it; Build when the mod is not built; Not loaded when it failed with nothing to install; Up to date
    /// otherwise (a built source mod is built again from its right-click menu).</summary>
    public RowAction Action => HasUpdate ? RowAction.Update : Failed && Offer is not null ? RowAction.Reinstall : !IsBuilt ? RowAction.Build
        : Failed ? RowAction.NotLoaded : RowAction.UpToDate;
    public string ActionText => Action switch
    {
        RowAction.Update => L.Text("Update"),
        RowAction.Reinstall => L.Text("Reinstall"),
        RowAction.Build => L.Text("Build"),
        RowAction.NotLoaded => L.Text("Not loaded"),
        _ => L.Text("Up to date"),
    };
    /// <summary>The install, build or uninstall running for this mod, shown in place of its button.</summary>
    public ModJob? Job { get; init; }
    public bool HasJob => Job is not null;
    public bool HasButton => Action is RowAction.Update or RowAction.Reinstall or RowAction.Build && Job is null;
    /// <summary>The Action column as text, not a button: Up to date, or Not loaded.</summary>
    public bool UpToDate => !HasButton && Job is null;
    /// <summary>Build compiles the source project (a source checkout), or installs the repository's copy when there is none.</summary>
    public bool CanAct => !Repo.LegacyReleaseMods && Job is null && Action switch
    {
        RowAction.Update or RowAction.Reinstall => true,
        RowAction.Build => (Entry.Project.Length > 0 && !Repo.IsInstall) || Offer is not null,
        _ => false,
    };
    public string? ActionTip => Action == RowAction.Update ? VersionChange
        : Action == RowAction.Build && !CanAct ? L.Text("This mod has no source project to build here; install it again from Browse.") : null;
    /// <summary>Its button is an Update: drawn green.</summary>
    public bool IsUpdate => Action == RowAction.Update;
    public string Latest => Offer?.Latest ?? "";
    public string Author => Entry.Author.Length > 0 ? Entry.Author : Offer?.Author ?? "";
    /// <summary>Under the name: the installed version, and a problem when there is one.</summary>
    public string Subtitle => string.Join(" · ", new[] { Entry.Version, Entry.IsScript ? L.Text("Script mod") : "", Problem }.Where(t => t.Length > 0));
    public string Problem => Unused ? L.Text("No league uses it") : Failed ? L.Text("Failed to load: {0}", LoadError)
        : Entry.Status is "Missing DLL" or "Not built" or "Dependency blocked" or "Mods disabled globally" || Entry.Status.StartsWith("Invalid", StringComparison.Ordinal)
        || (Entry.Enabled && Entry.Status == "Load failed") ? L.Text(Entry.Status) : "";
    public bool HasProblem => Problem.Length > 0;
    public string? ProblemTip => HasProblem ? Problem : null;
    public string SearchText => $"{Entry.SearchText} {Title} {Description} {Author}";
}

/// <summary>What the running Gateway last said about each mod in its log (logs/live-gateway.log, written anew at each
/// start): "[mods] loaded id", "[mods] unloaded id", "[mods] id not loaded: why".</summary>
static class GatewayMods
{
    static readonly System.Text.RegularExpressions.Regex Line = new(
        @"\[mods\] (?:(?:loaded|unloaded) (?<id>[A-Za-z0-9][A-Za-z0-9_.-]*)(?: |$)|(?<id>[A-Za-z0-9][A-Za-z0-9_.-]*) not loaded: (?<why>.+)$)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    // Read on from where it was last read: the log grows all through play. A shorter file is a new Gateway's log.
    static string _file = "";
    static long _offset;
    static Dictionary<string, string> _errors = new(StringComparer.Ordinal);

    /// <summary>Reads what the log added; true when a mod's load state changed (My Mods draws its rows again).</summary>
    public static bool Changed(RuntimeSnapshot? state)
    {
        if (state is null) return false;
        string file = Path.Combine(state.Root, "logs", "live-gateway.log");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return Reset(file);
            bool changed = false;
            if (file != _file || info.Length < _offset) changed = Reset(file);
            if (info.Length == _offset) return changed;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(_offset, stream.Length - (4 << 20)), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            // Only whole lines: the rest is read with what follows it.
            int end = text.LastIndexOf('\n') + 1;
            _offset = stream.Length - System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(end));
            foreach (string line in text[..end].Split('\n'))
            {
                if (!line.Contains("[mods] ", StringComparison.Ordinal) || Line.Match(line.TrimEnd('\r')) is not { Success: true } match) continue;
                string id = match.Groups["id"].Value;
                if (match.Groups["why"].Success)
                {
                    string why = match.Groups["why"].Value.Trim();
                    changed |= !_errors.TryGetValue(id, out var was) || was != why;
                    _errors[id] = why;
                }
                else changed |= _errors.Remove(id);
            }
            return changed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool Reset(string file)
    {
        bool had = _errors.Count > 0;
        _file = file;
        _offset = 0;
        _errors = new(StringComparer.Ordinal);
        return had;
    }

    /// <summary>Why each mod the running Gateway tried did not load, by id; empty when no Gateway runs.</summary>
    public static IReadOnlyDictionary<string, string> LoadErrors(RuntimeSnapshot? state)
    {
        if (state?.Services.FirstOrDefault(s => s.Name == "Gateway") is not { } gateway || (gateway.ProcessId <= 0 && gateway.Status != "Ready"))
            return new Dictionary<string, string>();
        Changed(state);
        return new Dictionary<string, string>(_errors, StringComparer.Ordinal);
    }
}

/// <summary>Browse's engine, shared by both apps: the repositories' indexes and releases (read once, then on Refresh), and an
/// install or update of one mod: download and check it in Waypoint's folder, then the adapter's InstallMod puts it in the
/// checkout (a project installed from source is built later, by Build). Each mod's job shows on its row; downloads run
/// side by side, the checkout's operations wait their turn.</summary>
sealed class ModBrowser(Shared shared)
{
    readonly HttpClient _http = Updates.Client();
    readonly Dictionary<string, ModRepositoryView> _views = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The official repository, then the player's own, in the order they were added.</summary>
    public IReadOnlyList<ModSource> Sources =>
        [ModSource.Official, .. shared.Settings.ModRepositories.Select(ModSource.Parse).OfType<ModSource>().Where(s => !s.IsOfficial)];

    public ModRepositoryView? View(ModSource source) => _views.GetValueOrDefault(source.Key);
    public string? Error(ModSource source) => _errors.GetValueOrDefault(source.Key);

    /// <summary>Reads a repository's index and releases; with <paramref name="again"/> false, only the first time.</summary>
    public async Task<ModRepositoryView?> ReadAsync(ModSource source, bool again)
    {
        if (!again && _views.TryGetValue(source.Key, out var known)) return known;
        try
        {
            var view = await ModRepository.ReadAsync(_http, source);
            _views[source.Key] = view;
            _errors.Remove(source.Key);
            _ = RefreshIconsAsync(view);
            return view;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _errors[source.Key] = e.Message;
            return _views.GetValueOrDefault(source.Key);
        }
    }

    /// <summary>A mod's icon.png changed: the lists draw their rows again.</summary>
    public event Action? IconsChanged;

    /// <summary>Brings the kept icons up to date behind the list, which shows them as they were until this ends. An icon that
    /// cannot be read stays as it was; a mod without one shows its symbol.</summary>
    async Task RefreshIconsAsync(ModRepositoryView view)
    {
        int changed = 0;
        await Parallel.ForEachAsync(view.Mods, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (mod, stop) =>
        {
            try { if (await ModRepository.RefreshIconAsync(_http, view.Source, mod, ModRepository.IconFile(view.Source, mod.Id), stop)) Interlocked.Increment(ref changed); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) { }
        });
        if (changed > 0) IconsChanged?.Invoke();
    }

    /// <summary>Whether NativeMods is opted-in for the current folder.</summary>
    public bool NativeModsEnabled => shared.Settings.NativeModsFor().Enabled;

    /// <summary>The last launch's warning that the enabled mods' game data could not be built; null when there was none.</summary>
    public string? NativeModsWarning => shared.NativeModsWarning;

    /// <summary>The rows of a repository read before, matched to the installed mods by id.</summary>
    public IReadOnlyList<BrowseItem> Items(ModSource source, IReadOnlyList<ModEntry> installed)
    {
        if (View(source) is not { } view) return [];
        var byId = installed.ToDictionary(e => e.Id, StringComparer.Ordinal);
        bool disabled = !NativeModsEnabled;
        return view.Mods.Select(m => new BrowseItem(source, m, view.Releases.GetValueOrDefault(m.Id), byId.GetValueOrDefault(m.Id))
            { Job = Job(m.Id), NativeModsDisabled = disabled })
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Reads every repository not read yet (or all again), for My Mods' updates.</summary>
    public async Task ReadAllAsync(bool again)
    {
        foreach (var source in Sources) await ReadAsync(source, again);
    }

    /// <summary>What the repositories read so far offer for each installed mod, by id: the official repository first.</summary>
    public IReadOnlyDictionary<string, BrowseItem> Offers(IReadOnlyList<ModEntry> installed)
    {
        var offers = new Dictionary<string, BrowseItem>(StringComparer.Ordinal);
        foreach (var source in Sources)
            foreach (var item in Items(source, installed))
                if (item.Installed is not null) offers.TryAdd(item.Listing.Id, item);
        return offers;
    }

    /// <summary>A mod's README: from its repository for a listed mod, from its folder (or source project) otherwise.</summary>
    public async Task<string> ReadmeAsync(BrowseItem? item, ModEntry? installed)
    {
        if (item is not null)
        {
            try { if (await ModRepository.ReadmeAsync(_http, item.Source, item.Listing) is { Length: > 0 } text) return text; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        }
        foreach (string folder in new[] { installed?.Folder ?? "", installed?.Project is { Length: > 0 } p && shared.Workspace.Latest is { } state ? Path.GetDirectoryName(Path.Combine(state.Root, p)) ?? "" : "" })
        {
            string file = Path.Combine(folder, "README.md");
            if (folder.Length > 0 && File.Exists(file) && ModCatalog.Physical(file) && new FileInfo(file).Length < 256 * 1024) return await File.ReadAllTextAsync(file);
        }
        return "";
    }

    /// <summary>Turns an installed mod on or off: its line in the server's mod-state.txt, at once (no backup, no client).
    /// For dual-side NativeMods mods, also syncs client mod state and rebuilds <c>Mods/Client/.merged-cache/</c>.</summary>
    public Task<bool> SetEnabledAsync(ModEntry mod, bool enabled) => shared.RunExclusiveAsync(L.Text("Save mod choice"), async () =>
    {
        if (shared.Workspace.Latest is { EditConflicts.Length: > 0 } state) throw new InvalidOperationException(string.Join("\n", state.EditConflicts));
        if (shared.Workspace.Latest?.ModEnvironment is not { Supported: true } environment) throw new InvalidOperationException(L.Text("This revision does not have a supported mod host."));
        ModCatalog.WriteChoice(environment.Folder, mod.Id, enabled);
        if (mod.HasNativeClient)
        {
            string root = Environment.CurrentDirectory;
            var cache = await Task.Run(() => NativeModsClientStore.OnModEnabledChanged(root, mod.Id, enabled));
            if (cache is { Rebuilt: true })
                shared.Logs.Write($"[nativemods] rebuilt .merged-cache ({cache.MergedTables.Count} tables, digest {cache.Digest}).", Severity.Info);
            else if (!string.IsNullOrWhiteSpace(cache?.Diagnostic))
                shared.Logs.Write($"[nativemods] {cache.Diagnostic}", Severity.Warning);
            else if (cache is { Removed: true })
                shared.Logs.Write("[nativemods] cleared .merged-cache (no enabled datPatches mods).", Severity.Info);
        }
        NoteRestart(mod.RestartToApply);
    });

    /// <summary>Uninstalls a mod: the adapter's UninstallMod moves its folders to Mods/.replaced/, and updates NativeMods ledger/.merged-cache.</summary>
    public async Task<BridgeResult?> UninstallAsync(ModEntry mod)
    {
        RequireModernMods();
        var job = Begin(mod.Id, L.Text("Uninstalling…"));
        if (job is null) return null;
        try
        {
            var result = await Finish(job, await QueuedAsync(job, "UninstallMod", L.Text("Uninstall mod"), new(mod.Id), L.Text("Uninstalling…")));
            if (result?.Ok == true)
            {
                string root = Environment.CurrentDirectory;
                await RunExclusiveWaitingAsync(L.Text("Update NativeMods cache"), async () =>
                {
                    var cache = await Task.Run(() => NativeModsClientStore.OnModUninstalled(root, mod.Id));
                    if (cache is { Rebuilt: true })
                        shared.Logs.Write($"[nativemods] rebuilt .merged-cache after uninstall ({cache.MergedTables.Count} tables).", Severity.Info);
                    else if (!string.IsNullOrWhiteSpace(cache?.Diagnostic))
                        shared.Logs.Write($"[nativemods] {cache.Diagnostic}", Severity.Warning);
                    else if (cache is { Removed: true })
                        shared.Logs.Write("[nativemods] removed .merged-cache after uninstall.", Severity.Info);
                });
            }
            return Installed(result, mod.RestartToApply);
        }
        catch { End(job); throw; }
    }

    /// <summary>Builds an installed source project (the adapter's BuildMod).</summary>
    public async Task<BridgeResult?> BuildAsync(ModEntry mod)
    {
        if (CannotBuild() is { } reason) throw new InvalidOperationException(reason);
        var job = Begin(mod.Id, L.Text("Building…"));
        if (job is null) return null;
        try { return await Finish(job, await QueuedAsync(job, "BuildMod", L.Text("Build mod"), new(mod.Id, mod.Project), L.Text("Building…"))); }
        catch { End(job); throw; }
    }

    // The servers read a mod's leagues and world data, and which leagues list a mod, only when they start: the Gateway
    // that has not read the change (ModsText.GatewayRun), until another one runs or none does.
    string _restartFor = "";
    void NoteRestart(bool needed)
    {
        if (needed && ModsText.GatewayRun(shared.Workspace.Latest) is { Length: > 0 } run) _restartFor = run;
    }
    BridgeResult? Installed(BridgeResult? result, bool restartToApply)
    {
        if (result?.Ok == true) NoteRestart(restartToApply);
        return result;
    }
    /// <summary>Such a mod was installed, removed or switched while the servers ran: said until they stop.</summary>
    public string RestartNotice(RuntimeSnapshot? state) => ModsText.RestartNotice(ref _restartFor, state);

    // ---- Jobs: what runs for each mod, shown on its button ----

    // Downloads run side by side; the checkout's operations one at a time, as Waypoint runs them (QueuedAsync).
    readonly Dictionary<string, ModJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>A job starts or ends: the lists draw their rows again (a job's progress updates its row by itself).</summary>
    public event Action? JobsChanged;

    public ModJob? Job(string id) => _jobs.GetValueOrDefault(id);

    ModJob? Begin(string id, string text)
    {
        if (_jobs.ContainsKey(id)) return null;
        var job = _jobs[id] = new ModJob(id, text);
        JobsChanged?.Invoke();
        return job;
    }

    void End(ModJob job)
    {
        if (_jobs.GetValueOrDefault(job.Id) == job) _jobs.Remove(job.Id);
        JobsChanged?.Invoke();
    }

    /// <summary>Ends a job: its row shows the mod's state again.</summary>
    Task<BridgeResult?> Finish(ModJob job, BridgeResult? result)
    {
        End(job);
        return Task.FromResult(result);
    }

    /// <summary>An adapter operation waiting its turn on the checkout.</summary>
    sealed record Pending(ModJob? Job, string Action, string Name, ModRequest Mod, string Running, TaskCompletionSource<BridgeResult?> Done);

    // Operations wait here, in order; the UI thread adds and takes them.
    readonly List<Pending> _pending = [];
    bool _pumping;

    /// <summary>Runs an adapter operation after the mods' others and whatever else Waypoint runs on the checkout (it runs one
    /// at a time, and refuses one more): waits its turn instead of being refused. Installs, uninstalls, and builds that wait
    /// together run as one operation of their kind over all their mods: one backup, and builds in parallel.</summary>
    Task<BridgeResult?> QueuedAsync(ModJob? job, string action, string name, ModRequest mod, string running)
    {
        job?.Show(L.Text("Waiting…"), 0, indeterminate: true);
        var pending = new Pending(job, action, name, mod, running, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending.Add(pending);
        if (!_pumping) _ = PumpAsync();
        return pending.Done.Task;
    }

    async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (_pending.Count > 0)
            {
                var first = _pending[0];
                List<Pending> batch = first.Action is "InstallMod" or "UninstallMod" or "BuildMod" ? _pending.Where(p => p.Action == first.Action).ToList() : [first];
                foreach (var p in batch) _pending.Remove(p);
                var request = batch.Count == 1 ? first.Mod : new ModRequest(Batch: batch.Select(p => p.Mod).ToList());
                string name = batch.Count == 1 ? first.Name : L.Text("{0} ({1} mods)", first.Name, batch.Count);
                try
                {
                    var result = await WaitForWorkspaceAsync(
                        () => shared.Workspace.OperateAsync(first.Action, name, mod: request), waiting =>
                    {
                        foreach (var p in batch) p.Job?.Show(waiting ? L.Text("Waiting…") : p.Running, 0, indeterminate: true);
                    });
                    foreach (var p in batch) p.Done.TrySetResult(result);
                }
                catch (Exception e)
                {
                    foreach (var p in batch) p.Done.TrySetException(e);
                }
            }
        }
        finally { _pumping = false; }
    }

    /// <summary>Only a busy workspace is retried. Cancellation and failures complete the job and release the queue.</summary>
    // The client half follows a queued server step, so the queue's next operation may still hold the lock: wait like the bridge does.
    async Task<bool> RunExclusiveWaitingAsync(string name, Func<Task> work)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (!await shared.RunExclusiveAsync(name, work))
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(500);
        }
        return true;
    }

    internal static async Task<BridgeResult?> WaitForWorkspaceAsync(Func<Task<BridgeResult?>> run, Action<bool> waiting,
        Func<Task>? pause = null)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (true)
        {
            waiting(false);
            var result = await run();
            if (result is not null || DateTime.UtcNow > deadline) return result;
            waiting(true);
            await (pause?.Invoke() ?? Task.Delay(500));
        }
    }

    /// <summary>Adds a repository by its URL or "owner/name"; false for text that names none, or one already there.</summary>
    public bool Add(string text, out ModSource? source)
    {
        var parsed = source = ModSource.Parse(text);
        if (parsed is null || Sources.Any(s => s.Key.Equals(parsed.Key, StringComparison.OrdinalIgnoreCase))) return false;
        shared.Settings.ModRepositories.Add(parsed.Branch == "main" ? parsed.Key : $"{parsed.Key}/tree/{parsed.Branch}");
        shared.Settings.Save();
        return true;
    }

    public void Remove(ModSource source)
    {
        if (source.IsOfficial) return;
        shared.Settings.ModRepositories.RemoveAll(t => ModSource.Parse(t)?.Key.Equals(source.Key, StringComparison.OrdinalIgnoreCase) == true);
        shared.Settings.Save();
        _views.Remove(source.Key);
        _errors.Remove(source.Key);
    }

    /// <summary>Why this checkout cannot take a mod's source, or null when it can: a release install has no SDK and no
    /// GameServer.Modding to build it against.</summary>
    public static string? CannotInstallSource() => Repo.IsInstall
        ? L.Text("This mod has no release, and a LocalExile release cannot build mods from source. Use a source checkout of LocalExile to build it, or wait for a release of the mod.")
        : null;

    /// <summary>Why this checkout cannot build a mod now, or null when it can: building needs the .NET 10 SDK.</summary>
    public string? CannotBuild()
    {
        if (CannotInstallSource() is { } reason) return reason;
        bool sdk = shared.Workspace.Latest?.Requirements.FirstOrDefault(r => r.Name == ".NET 10 SDK")?.Ready == true;
        return sdk ? null : L.Text("Building a mod needs the .NET 10 SDK. Install it (Setup lists what the checkout needs), then try again.");
    }

    /// <summary>The warning shown before installing a mod's source: it is installed, not built.</summary>
    public static string SourceWarning(BrowseItem item) =>
        L.Text("{0} has no release, so Waypoint installs its source project. Press Build in My Mods to compile it; building needs the .NET 10 SDK and this checkout's build tools. Continue?", item.Title);

    /// <summary>Downloads and installs (or updates) one mod, its progress on its button; the result of the adapter's
    /// InstallMod, or null when it did not run (one already runs for this mod). Dual-side mods with <c>nativeClient</c>
    /// are audited before install, split-deployed into <c>Mods/Binaries/</c> + <c>Mods/Client/</c>, and rebuild <c>.merged-cache/</c>.</summary>
    public async Task<BridgeResult?> InstallAsync(BrowseItem item, Func<InstallAuditReport, bool>? confirmAudit = null, CancellationToken stop = default)
    {
        RequireModernMods();
        var job = Begin(item.Listing.Id, L.Text("Downloading…"));
        if (job is null) return null;
        try
        {
            void Progress(long done, long total) => job.Show(total > 0 ? $"{(double)done / total:P0}" : L.Text("{0:N0} KB", done / 1024),
                total > 0 ? (double)done / total : 0, indeterminate: total <= 0);
            StagedMod staged = item.Release is { } release
                ? await ModRepository.DownloadReleaseAsync(_http, release, Progress, stop)
                : await ModRepository.DownloadSourceAsync(_http, item.Source, item.Listing, Progress, stop);
            try
            {
                return await InstallStagedAsync(job, staged, item.Offer == ModOffer.Update ? L.Text("Update mod") : L.Text("Install mod"), confirmAudit);
            }
            finally { ModRepository.Discard(staged); }
        }
        catch { End(job); throw; }
    }

    /// <summary>Installs a mod folder from the player's disk: a project's source is installed, not built.</summary>
    public async Task<BridgeResult?> InstallFolderAsync(string folder, Func<InstallAuditReport, bool>? confirmAudit = null)
    {
        RequireModernMods();
        var staged = ModRepository.StageFolder(folder);
        try
        {
            if (staged.Target == "Source" && CannotInstallSource() is { } reason) throw new InvalidOperationException(reason);
            var job = Begin(staged.Id, L.Text("Installing…"));
            if (job is null) return null;
            try { return await InstallStagedAsync(job, staged, L.Text("Install mod"), confirmAudit); }
            catch { End(job); throw; }
        }
        finally { ModRepository.Discard(staged); }
    }

    async Task<BridgeResult?> InstallStagedAsync(ModJob job, StagedMod staged, string operationName, Func<InstallAuditReport, bool>? confirmAudit)
    {
        string root = Environment.CurrentDirectory;
        bool restart = ModCatalog.RestartToApply(staged.Folder);
        bool isDualSide = staged.Target == "Binaries" && NativeModsClientStore.HasNativeClientManifest(staged.Folder);
        if (!isDualSide)
        {
            // Read before InstallMod: a successful install moves the staged folder away.
            bool dropsClientHalf = File.Exists(Path.Combine(staged.Folder, "mod.json")) && !NativeModsClientStore.HasNativeClientManifest(staged.Folder);
            var plain = await Finish(job, await QueuedAsync(job, "InstallMod", operationName,
                new(staged.Id, Staged: staged.Folder, Target: staged.Target), L.Text("Installing…")));
            // An update that dropped nativeClient: the earlier version's client half must not keep loading against the new server.
            if (plain?.Ok == true && dropsClientHalf)
            {
                try
                {
                    if (!NativeModsClientStore.HasClientHalf(root, staged.Id)) return Installed(plain, restart);
                    restart = true;
                    await RunExclusiveWaitingAsync(L.Text("Update NativeMods cache"), async () =>
                    {
                        await Task.Run(() => NativeModsClientStore.RetireClientHalf(root, staged.Id));
                        shared.Logs.Write($"[nativemods] {staged.Id} no longer has a client half; moved Mods/Client/{staged.Id} to Mods/.replaced.", Severity.Info);
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                               or System.Text.Json.JsonException)
                {
                    shared.Logs.Write($"[nativemods] could not retire the client half of {staged.Id}: {ex.Message}", Severity.Error);
                }
            }
            return Installed(plain, restart);
        }

        // Dual-side NativeMods package: validate .ui layouts, datPatches, closed tables, SHA-256, encoding, and cross-mod conflicts.
        var audit = await Task.Run(() => NativeModsClientStore.AuditPackage(staged.Folder, root, allowRawAccess: false, allowConflicts: false));
        bool hasBlockingErrors = audit.Validation.Errors.Any();
        if (hasBlockingErrors || audit.Conflicts.Count > 0)
        {
            End(job);
            throw new InvalidDataException(audit.ToHumanReadableSummary());
        }

        bool needsConfirmation = audit.RawAccessRequested || audit.ReplacedStockPaths.Count > 0 || !NativeModsEnabled;
        if (needsConfirmation && confirmAudit is not null && !confirmAudit(audit))
        {
            End(job);
            return new BridgeResult(false, L.Text("Operation cancelled."), null, Cancelled: true);
        }
        if (audit.RawAccessRequested && confirmAudit is null)
        {
            End(job);
            throw new InvalidDataException(L.Text("This mod requests RawAccess and requires explicit confirmation."));
        }

        // Split deployment: strip client payload into a clean server staging copy for ModBridge.ps1 (Mods/Binaries/<id>/).
        bool hadServerBefore = Directory.Exists(Path.Combine(root, "Mods", "Binaries", staged.Id));
        string serverOnlyFolder = await Task.Run(() => NativeModsClientStore.PrepareServerStaging(staged.Folder, staged.Id));
        try
        {
            var serverResult = await QueuedAsync(job, "InstallMod", operationName,
                new(staged.Id, Staged: serverOnlyFolder, Target: "Binaries"), L.Text("Installing…"));
            if (serverResult is not { Ok: true })
                return await Finish(job, serverResult);

            // Deploy client half into Mods/Client/<id>/ and rebuild Mods/Client/.merged-cache/ under the workspace lock.
            ClientModDeployResult? clientDeploy = null;
            string? failure = null;
            try
            {
                bool ran = await RunExclusiveWaitingAsync(L.Text("Install NativeMods client payload"), async () =>
                {
                    clientDeploy = await Task.Run(() => NativeModsClientStore.DeployClientHalf(
                        staged.Folder, root, enableOverride: null, allowRawAccess: audit.RawAccessRequested, allowConflicts: false));
                    if (clientDeploy.MergedCache is { Rebuilt: true } mc)
                        shared.Logs.Write($"[nativemods] rebuilt .merged-cache ({mc.MergedTables.Count} tables, digest {mc.Digest}).", Severity.Info);
                    else if (!string.IsNullOrWhiteSpace(clientDeploy.MergedCache?.Diagnostic))
                        shared.Logs.Write($"[nativemods] {clientDeploy.MergedCache.Diagnostic}", Severity.Warning);
                });
                if (!ran) failure = L.Text("Something else is using LocalExile. Try again when it is done.");
                else if (clientDeploy is not { Succeeded: true }) failure = clientDeploy?.Message ?? L.Text("Client mod installation was blocked by validation.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                failure = ex.Message;
            }

            if (failure is not null)
            {
                shared.Logs.Write($"[nativemods] client deploy failed for {staged.Id}: {failure}", Severity.Error);
                if (!hadServerBefore)
                    await QueuedAsync(job, "UninstallMod", L.Text("Uninstall mod"), new(staged.Id), L.Text("Uninstalling…"));
                return await Finish(job, new BridgeResult(false, failure, serverResult.Snapshot, L.Text("Install NativeMods client payload")));
            }

            return Installed(await Finish(job, serverResult), restartToApply: true);
        }
        finally
        {
            string? work = Path.GetDirectoryName(serverOnlyFolder);
            if (work is not null)
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes a mod's settings: the values file is checked into Waypoint's folder, and the adapter's SaveModSettings
    /// puts it in the checkout's data/mods/.</summary>
    public async Task<BridgeResult?> SaveSettingsAsync(string id, JsonObject values, string expectedHash)
    {
        RequireModernMods();
        string folder = Path.Combine(ModRepository.DownloadsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, id + ".json");
        await File.WriteAllTextAsync(file, values.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        try { return await QueuedAsync(null, "SaveModSettings", L.Text("Save mod settings"), new(id, Staged: file, ExpectedHash: expectedHash), ""); }
        finally { try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Reads the checkout's LocalExile version again (<see cref="Repo.ClientVersion"/>): a release's release.json, a
    /// source checkout's newest version tag. A switch or an update changes it.</summary>
    public static async Task RefreshClientVersionAsync()
    {
        if (Repo.IsInstall) { Repo.ClientVersion = LocalExileReleases.Installed(Environment.CurrentDirectory); return; }
        try
        {
            var result = await Git.RunAsync(null, "describe", "--tags", "--abbrev=0", "--match", "v[0-9]*");
            Repo.ClientVersion = result.Ok ? ReleaseVersion.Parse(result.Output.Trim()) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Repo.ClientVersion = null; }
    }

    /// <summary>The question before installing, updating or building a mod made for other LocalExile versions.</summary>
    public static string IncompatibleQuestion() =>
        L.Text("This mod is incompatible with your client version: {0}. Are you sure you want to install it?", Repo.ClientVersion?.ToString() ?? "?");

    internal static void RequireModernMods()
    {
        if (Repo.LegacyReleaseMods) throw new InvalidOperationException(L.Text("This release does not declare compatibility with downloadable mods. Use a current source checkout to install or update mods."));
    }
}

/// <summary>The Mods section's text and form reading, the same in both apps' views.</summary>
static class ModsText
{
    /// <summary>The checkout's saved choices, never an assertion that those mods loaded.</summary>
    public static string GlobalChoices(ModEnvironment? environment, IReadOnlyList<ModEntry> entries, bool compact = true)
    {
        if (environment is not { Supported: true }) return L.Text("Mod information unavailable");
        if (!environment.Enabled) return L.Text("Mods are off for this checkout");
        var enabled = entries.Where(e => e.Enabled).OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (enabled.Length == 0) return L.Text("No mods enabled");
        string names = string.Join(", ", (compact ? enabled.Take(3) : enabled).Select(e => e.Title));
        if (compact && enabled.Length > 3) names += L.Text(" + {0} more", enabled.Length - 3);
        return L.Text("Saved enabled: {0}", names);
    }
    public static string GlobalRuntime(RuntimeSnapshot? state)
    {
        if (state?.ModEnvironment is not { Supported: true } environment) return "";
        var report = ModCatalog.ParseRuntimeReport(environment.RuntimeReport);
        return report.Count == 0 ? L.Text("Live load: not checked")
            : L.Text("Last checked: {0} loaded", report.Count(e => e.Value.Status == "Loaded (last checked)"));
    }
    /// <summary>The Mods section's warning on a release whose server cannot load compiled mods
    /// (<see cref="Repo.ReleaseModsMayNotLoad"/>); "" on any other. A warning only: every action stays available.</summary>
    public static string MayNotLoadNotice() => Repo.ReleaseModsMayNotLoad
        ? L.Text("LocalExile {0} has a bug that stops most downloaded mods from loading. Update LocalExile to {1} or newer to fix it. You can still install, change and remove mods here.",
            "v" + Repo.ClientVersion, "v" + Repo.FirstReleaseLoadingMods)
        : "";

    /// <summary>Presentation-only defaults, using the same native symbol family as the module library.</summary>
    public static ModIconKind Icon(string id) => id.ToLowerInvariant() switch
    {
        "bossloot" => ModIconKind.TrophyOutline, "doubleexperience" => ModIconKind.StarFourPoints,
        "fastcampaign" => ModIconKind.RunFast, "itemquantity" => ModIconKind.PackageVariantClosed,
        "itemrarity" => ModIconKind.DiamondStone, "example" => ModIconKind.CodeBraces,
        "hello" => ModIconKind.ChatOutline, _ => ModIconKind.PuzzleOutline
    };

    // The lists draw their rows often: a file is decoded again only when it changes.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, DateTime Written, ModIconImage? Image)> IconImages = new();

    /// <summary>A mod's icon.png as an image; null, for the mod's symbol, when there is no such file or it is not a usable PNG.</summary>
    public static ModIconImage? IconImage(string file)
    {
        if (file.Length == 0) return null;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            if (IconImages.TryGetValue(file, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc) return known.Image;
            ModIconImage? image = null;
            try { if (info.Length <= 1 << 20 && File.ReadAllBytes(file) is var bytes && ModRepository.IsIcon(bytes)) image = Decode(bytes); }
            // Decoders throw many kinds for a damaged file, and any of them only means this mod has no icon to show.
            catch (Exception e) when (e is not (IOException or UnauthorizedAccessException)) { }
            IconImages[file] = (info.Length, info.LastWriteTimeUtc, image);
            return image;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    static ModIconImage Decode(byte[] bytes)
    {
#if WINDOWS
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = 256;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
#else
        using var stream = new MemoryStream(bytes);
        return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 256);
#endif
    }

    /// <summary>Only an explicit runtime report can supply a last-checked load state; saved choices never do.</summary>
    public static string RuntimeStatus(RuntimeSnapshot? state, string id)
    {
        if (state?.ModEnvironment is not { } environment) return L.Text("Not checked");
        var report = ModCatalog.ParseRuntimeReport(environment.RuntimeReport);
        if (!report.TryGetValue(id, out var observation)) return L.Text("Not checked");
        string status = observation.Status == "Loaded (last checked)" ? L.Text("Loaded (last checked)")
            : observation.Status == "Disabled" ? L.Text("Not loaded (last checked)")
            : observation.Status == ModCatalog.Unused ? L.Text("No league uses it") : L.Text("Failed to load (last checked)");
        return DateTimeOffset.TryParse(environment.CheckedUtc, out var when) ? L.Text("{0} · {1:g}", status, when.ToLocalTime()) : status;
    }
    /// <summary>Choices are saved even on an older Gateway; do not imply its running mods changed.</summary>
    public static string ChoicesNotice(RuntimeSnapshot? state) =>
        state is { ModEnvironment.Supported: true } && state.Services.Any(s => s.Name == "Gateway" && s.IsRunning)
            && !ModCatalog.SupportsLiveChoices(state.Root)
            ? L.Text("Live mod switches could not be verified for this server. Choices are saved; restart Gateway to apply them.") : "";

    /// <summary>The running Gateway, as one start of it: "" when none runs.</summary>
    public static string GatewayRun(RuntimeSnapshot? state) =>
        state?.Services.FirstOrDefault(s => s.Name == "Gateway") is { ProcessId: > 0 } gateway ? gateway.ProcessId + " " + gateway.StartedUtc : "";

    /// <summary>What a change to a league's mod is told while the Gateway that has not read it (<paramref name="pendingFor"/>)
    /// still runs; once it stopped or was restarted, nothing is pending.</summary>
    public static string RestartNotice(ref string pendingFor, RuntimeSnapshot? state)
    {
        if (pendingFor.Length == 0 || pendingFor != GatewayRun(state)) { pendingFor = ""; return ""; }
        return L.Text("Restart the servers to apply the change to a league's mod.");
    }

    /// <summary>A mod page's labels: where it comes from, what it is.</summary>
    public static IReadOnlyList<string> Tags(BrowseItem? item, ModEntry? installed)
    {
        var tags = new List<string>();
        if (item is not null) tags.Add(item.Source.IsOfficial ? L.Text("Official") : item.Source.Key);
        if (item?.Listing.IsProject == true || installed?.Project.Length > 0) tags.Add(L.Text("Project"));
        else if (item is not null || installed?.IsScript == true) tags.Add(L.Text("Scripts"));
        string kind = installed?.Kind ?? item?.Listing.Kind ?? "";
        if (kind.Length > 0 && kind != "?") tags.Add(L.Text(kind));
        if (item?.Listing.HasSettings == true || installed?.SettingsFile.Length > 0) tags.Add(L.Text("Settings"));
        if (installed?.Listed ?? item?.Listing.Listed == true) tags.Add(L.Text("League content"));
        foreach (string league in installed?.Leagues ?? []) tags.Add(L.Text("Adds league {0}", league));
        if ((installed?.NativeClient ?? item?.NativeClient) is { } nc)
        {
            tags.Add(nc.Required ? L.Text("Requires NativeMods") : L.Text("NativeMods"));
            if (nc.RawAccess) tags.Add(L.Text("Raw access"));
            if (nc.DatPatchCount > 0) tags.Add(L.Text("Dat patches"));
        }
        return tags;
    }

    /// <summary>A mod page's numbers: installed and latest versions, the release's date and size.</summary>
    public static string Stats(BrowseItem? item, ModEntry? installed)
    {
        var parts = new List<string>();
        if (installed is not null) parts.Add(L.Text("Installed {0}", installed.Version));
        if (item is not null && (installed is null || item.Latest != installed.Version)) parts.Add(L.Text("Latest {0}", item.Latest));
        var client = installed?.ClientVersions is { Count: > 0 } own ? own : item?.ClientVersions ?? [];
        parts.Add(L.Text("Client version {0}", ClientText(client)));
        if (item?.Release?.Published is { } published) parts.Add(published.ToLocalTime().ToString("d"));
        if (item?.Release is { ZipBytes: > 0 } release) parts.Add(L.Text("{0:N0} KB", Math.Max(1, release.ZipBytes / 1024)));
        return string.Join("   ·   ", parts);
    }

    /// <summary>A mod's compatible client versions as the lists show them.</summary>
    public static string ClientText(IReadOnlyList<string> versions) => versions.Count > 0 ? ClientCompatibility.Text(versions) : L.Text("Not declared");

    /// <summary>The warning beside a mod made for other LocalExile versions.</summary>
    public static string IncompatibleTip(IReadOnlyList<string> versions) =>
        L.Text("Made for LocalExile {0}; this checkout runs {1}.", ClientCompatibility.Text(versions), Repo.ClientVersion?.ToString() ?? "?");

    /// <summary>The question before an uninstall.</summary>
    public static string UninstallQuestion(ModEntry mod) =>
        L.Text("Are you sure you want to uninstall {0}? Its files move to Mods/.replaced/, where you can get them back; its settings stay.", mod.Title);

    /// <summary>The letter on a mod's tile.</summary>
    public static string Initial(string title) => title.FirstOrDefault(char.IsLetterOrDigit) is var c and not default(char) ? char.ToUpperInvariant(c).ToString() : "?";

    static readonly Markdig.MarkdownPipeline ReadmePipeline = Markdig.MarkdownExtensions.UseTaskLists(Markdig.MarkdownExtensions.UseEmphasisExtras(
        Markdig.MarkdownExtensions.UseAutoLinks(Markdig.MarkdownExtensions.UsePipeTables(new Markdig.MarkdownPipelineBuilder())))).Build();

    /// <summary>A README as GitHub's Markdown (CommonMark, tables, task lists, strikethrough, bare links), for both apps'
    /// renderers. HTML in it is not drawn.</summary>
    public static Markdig.Syntax.MarkdownDocument Readme(string markdown) => Markdig.Markdown.Parse(markdown, ReadmePipeline);

    /// <summary>Where a README's relative links point: the mod's folder on GitHub, or null for a mod no repository lists.</summary>
    public static string? ReadmeBase(BrowseItem? item) =>
        item is null ? null : item.Source.ModPage(item.Listing).Replace("/tree/", "/blob/") + "/";

    /// <summary>A link of a README to open in the browser: a web link, or a relative one resolved against
    /// <paramref name="baseUrl"/>; null for anything else (anchors, mail, files).</summary>
    public static string? Link(string? url, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#')) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
        return baseUrl is not null && Uri.TryCreate(new Uri(baseUrl), url, out var resolved) && resolved.Scheme == "https" ? resolved.AbsoluteUri : null;
    }

    public static string Details(ModEntry mod) =>
        L.Text("{0}\n{1} · {2}\nDependencies: {3}\n{4}\n{5}",
            mod.Description.Length > 0 ? mod.Description : mod.Id,
            mod.Author.Length > 0 ? L.Text("by {0}", mod.Author) : mod.Id, mod.Summary,
            mod.Depends.Length == 0 ? L.Text("none") : string.Join(", ", mod.Depends), mod.DisplayDetail,
            mod.Project.Length > 0 ? L.Text("Source: ") + mod.Project : mod.IsScript ? L.Text("Script mod") : L.Text("Binary-only mod"));

    /// <summary>A setting's label with its unit and range: "Item quantity (×) (1 to 100)".</summary>
    public static string Heading(ModSetting setting)
    {
        string unit = setting.IsPercent ? "%" : setting.Format == "multiplier" ? "×" : setting.Unit;
        string range = setting.Min is not null || setting.Max is not null
            ? L.Text(" ({0} to {1})", setting.Min is { } min ? ModSettingsForm.Show(JsonValue.Create(min), setting) : "…",
                setting.Max is { } max ? ModSettingsForm.Show(JsonValue.Create(max), setting) : "…") : "";
        return setting.Title + (unit.Length > 0 ? $" ({unit})" : "") + range;
    }

    public static string NotEditable(ModSetting setting) =>
        L.Text("Edit this setting in the mod's settings file (Open folder): Waypoint does not edit {0} settings yet.", setting.Type);

    /// <summary>Its description, and when a change applies when that is not at once.</summary>
    public static string Note(ModSetting setting) => (setting.Description + setting.Applies switch
    {
        "restart" => " " + L.Text("Applies when the Gateway restarts."),
        "nextArea" => " " + L.Text("Applies in the next area."),
        _ => "",
    }).Trim();

    /// <summary>The player's file from the form's inputs, or the errors of those that do not fit.</summary>
    public static (JsonObject Values, List<string> Errors) Collect(ModSettingsForm form, IReadOnlyDictionary<string, Func<object?>> inputs)
    {
        var edited = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var setting in form.Settings.Where(s => inputs.ContainsKey(s.Name)))
        {
            var value = ModSettingsForm.Read(setting, inputs[setting.Name](), out string? error);
            if (error is not null) errors.Add(error);
            else edited[setting.Name] = value;
        }
        return (form.Values(edited), errors);
    }

    /// <summary>The player's file with every setting the form edits back at its default.</summary>
    public static JsonObject Defaults(ModSettingsForm form, IReadOnlyDictionary<string, Func<object?>> inputs) =>
        form.Values(form.Settings.Where(s => inputs.ContainsKey(s.Name)).ToDictionary(s => s.Name, s => s.Default?.DeepClone(), StringComparer.Ordinal));

    public static string SourceName(ModSource source) => source.IsOfficial ? L.Text("Official mods ({0})", source.Key) : source.Key;

    public static string BrowseStatus(ModBrowser browser, ModSource source, IReadOnlyList<BrowseItem> items)
    {
        var view = browser.View(source);
        if (browser.Error(source) is { } error) return L.Text("Could not read {0}: {1}", source.Key, error);
        if (view is null) return L.Text("Not read yet.");
        string status = L.Text("{0} mods · {1} installed · {2} updates · read {3:t}", items.Count, items.Count(i => i.Installed is not null),
            items.Count(i => i.Offer == ModOffer.Update), view.Read);
        return view.ReleasesError is null ? status : status + "\n" + L.Text("Releases could not be read ({0}); mods install from source until they can.", view.ReleasesError);
    }

    /// <summary>What went wrong reading a repository, or null: Browse shows a line only then.</summary>
    public static string? BrowseProblem(ModBrowser browser, ModSource source) =>
        browser.Error(source) is { } error ? L.Text("Could not read {0}: {1}", source.Key, error)
        : browser.View(source)?.ReleasesError is { } releases ? L.Text("Releases could not be read ({0}); mods install from source until they can.", releases)
        : null;

    public static string AddRefused(ModSource? source) => source is null
        ? L.Text("That is not a GitHub repository: use github.com/owner/name or owner/name.") : L.Text("{0} is already in the list.", source.Key);

    /// <summary>The Mods section's warning when installed mods declare <c>nativeClient</c> while NativeMods is turned off in Settings.</summary>
    public static string NativeModsNotice(IReadOnlyList<ModEntry> entries, bool nativeModsEnabled, string? launchWarning = null)
    {
        if (nativeModsEnabled) return launchWarning ?? "";
        int count = entries.Count(e => e.Enabled && e.HasNativeClient);
        if (count == 0) return "";
        return L.Text("{0} enabled mod(s) include NativeMods client payloads, but NativeMods is currently off in Settings.", count);
    }

    /// <summary>Builds the human-readable confirmation prompt when installing a NativeMods dual-side mod that requests RawAccess, replaces stock paths, or when NativeMods is off.</summary>
    public static (string Title, string Message, string Details) AuditPrompt(InstallAuditReport audit, bool nativeModsEnabled)
    {
        var reasons = new List<string>();
        if (audit.RawAccessRequested)
            reasons.Add(L.Text("This mod requests direct memory/hook access (RawAccess): {0}", audit.RawAccessJustification ?? ""));
        if (audit.ReplacedStockPaths.Count > 0)
            reasons.Add(L.Text("This mod overrides {0} stock game path(s) via overlay.", audit.ReplacedStockPaths.Count));
        if (!nativeModsEnabled)
            reasons.Add(L.Text("NativeMods is currently off in Settings. You can still install the mod now and enable NativeMods in Settings later."));
        string title = audit.RawAccessRequested ? L.Text("Approve RawAccess for {0}?", audit.ModName) : L.Text("Install NativeMods mod {0}?", audit.ModName);
        return (title, string.Join("\n\n", reasons), audit.ToHumanReadableSummary());
    }

    public static string Untrusted(BrowseItem item) =>
        L.Text("{0} comes from {1}, not from the official mods. A mod runs with the Gateway's rights on your PC: install it only from a repository you trust.", item.Title, item.Source.Key);
}

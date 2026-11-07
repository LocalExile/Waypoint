using System.Net.Http;
using System.Text.Json.Nodes;

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

    /// <summary>A project without a release is built from source, which needs the .NET 10 SDK.</summary>
    public bool NeedsBuild => Release is null && Listing.IsProject;

    public string Title => Listing.Name;
    public string Byline => L.Text("by {0}", Listing.Author.Length > 0 ? Listing.Author : Source.Owner);
    public string VersionText => Offer switch
    {
        ModOffer.Update => L.Text("{0} → {1}", Installed!.Version, Offered?.ToString() ?? Listing.Version),
        ModOffer.Installed => L.Text("{0} installed", Installed!.Version),
        _ => Offered?.ToString() ?? Listing.Version,
    };
    public bool CanAct => Offer != ModOffer.Installed;
    public string ActionText => Offer switch { ModOffer.Install => L.Text("Install"), ModOffer.Update => L.Text("Update"), _ => L.Text("Installed") };
    public string Origin => Release is not null ? L.Text("Release {0}", Release.Tag)
        : Listing.IsProject ? L.Text("No release: builds from source") : L.Text("Scripts: no build needed");
    public string SearchText => $"{Listing.Id} {Listing.Name} {Listing.Author} {Listing.Description}";
}

/// <summary>Browse's engine, shared by both apps: the repositories' indexes and releases (read once, then on Refresh), and an
/// install or update of one mod: download and check it in Waypoint's folder, then the adapter's InstallMod puts it in the
/// checkout (and builds a project installed from source).</summary>
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
            return view;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _errors[source.Key] = e.Message;
            return _views.GetValueOrDefault(source.Key);
        }
    }

    /// <summary>The rows of a repository read before, matched to the installed mods by id.</summary>
    public IReadOnlyList<BrowseItem> Items(ModSource source, IReadOnlyList<ModEntry> installed)
    {
        if (View(source) is not { } view) return [];
        var byId = installed.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return view.Mods.Select(m => new BrowseItem(source, m, view.Releases.GetValueOrDefault(m.Id), byId.GetValueOrDefault(m.Id)))
            .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
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

    /// <summary>Why this checkout cannot build a mod from source, or null when it can: a release install has no SDK and no
    /// GameServer.Modding to build against; a source checkout needs the .NET 10 SDK.</summary>
    public string? CannotBuild()
    {
        if (Repo.IsInstall) return L.Text("This mod has no release, and a LocalExile release cannot build mods from source. Use a source checkout of LocalExile to build it, or wait for a release of the mod.");
        bool sdk = shared.Workspace.Latest?.Requirements.FirstOrDefault(r => r.Name == ".NET 10 SDK")?.Ready == true;
        return sdk ? null : L.Text("This mod has no release, so Waypoint builds it from source, which needs the .NET 10 SDK. Install it (Setup lists what the checkout needs), then try again.");
    }

    /// <summary>The warning shown before building a mod from source.</summary>
    public static string BuildWarning(BrowseItem item) =>
        L.Text("{0} has no release, so Waypoint will download its source and build it with the .NET 10 SDK against this checkout. Building needs the SDK and the checkout's build tools, and can take a minute. Continue?", item.Title);

    /// <summary>Downloads and installs (or updates) one mod; the result of the adapter's InstallMod, or null when it did not
    /// run. <paramref name="progress"/> gets the bytes downloaded and of how many.</summary>
    public async Task<BridgeResult?> InstallAsync(BrowseItem item, Action<long, long>? progress, CancellationToken stop = default)
    {
        StagedMod staged = item.Release is { } release
            ? await ModRepository.DownloadReleaseAsync(_http, release, progress, stop)
            : await ModRepository.DownloadSourceAsync(_http, item.Source, item.Listing, progress, stop);
        try
        {
            return await shared.Workspace.OperateAsync("InstallMod", item.Offer == ModOffer.Update ? L.Text("Update mod") : L.Text("Install mod"),
                mod: new(staged.Id, Staged: staged.Folder, Target: staged.Target));
        }
        finally { ModRepository.Discard(staged); }
    }

    /// <summary>Installs a mod folder from the player's disk.</summary>
    public async Task<BridgeResult?> InstallFolderAsync(string folder)
    {
        var staged = ModRepository.StageFolder(folder);
        try
        {
            if (staged.Target == "Source" && CannotBuild() is { } reason) throw new InvalidOperationException(reason);
            return await shared.Workspace.OperateAsync("InstallMod", L.Text("Install mod"), mod: new(staged.Id, Staged: staged.Folder, Target: staged.Target));
        }
        finally { ModRepository.Discard(staged); }
    }

    /// <summary>Writes a mod's settings: the values file is checked into Waypoint's folder, and the adapter's SaveModSettings
    /// puts it in the checkout's data/mods/.</summary>
    public async Task<BridgeResult?> SaveSettingsAsync(string id, JsonObject values)
    {
        string folder = Path.Combine(ModRepository.DownloadsFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, id + ".json");
        await File.WriteAllTextAsync(file, values.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        try { return await shared.Workspace.OperateAsync("SaveModSettings", L.Text("Save mod settings"), mod: new(id, Staged: file)); }
        finally { try { Directory.Delete(folder, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}

/// <summary>The Mods section's text and form reading, the same in both apps' views.</summary>
static class ModsText
{
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

    public static string AddRefused(ModSource? source) => source is null
        ? L.Text("That is not a GitHub repository: use github.com/owner/name or owner/name.") : L.Text("{0} is already in the list.", source.Key);

    public static string Untrusted(BrowseItem item) =>
        L.Text("{0} comes from {1}, not from the official mods. A mod runs with the Gateway's rights on your PC: install it only from a repository you trust.", item.Title, item.Source.Key);

    public static string Downloading(BrowseItem item, long done, long total) => total > 0
        ? L.Text("Downloading {0} … {1:P0}", item.Title, (double)done / total) : L.Text("Downloading {0} … {1:N0} KB", item.Title, done / 1024);
}

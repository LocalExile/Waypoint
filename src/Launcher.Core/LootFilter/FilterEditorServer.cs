using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Launcher.Core.LootFilter;

/// <summary>
/// The loot filter editor: a page in the player's browser, served by Waypoint on localhost, the same on Windows and Linux.
/// The address carries a random token, so no other page can reach it. The page reads the option file's sections and rules
/// with the base filter's current state, sends back the player's changes, and asks for the filter to be written into the
/// game's filter folder. Nothing else on the machine is touched.
/// </summary>
public sealed class FilterEditorServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly HttpClient _http;
    readonly Func<string?> _checkout;
    readonly string _gameFolder, _dataFolder, _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, FilterDocument> _bases = [];
    IReadOnlyList<OptionChapter>? _chapters;
    ItemCatalog? _catalog;
    (string Key, FilterDocument Filter)? _applied;
    FilterCustomization _customization;

    /// <param name="gameFolder">Where the game looks for .filter files (Documents\My Games\Path of Exile).</param>
    /// <param name="dataFolder">Waypoint's own folder for the player's customization, the downloads and the decoded sounds.</param>
    /// <param name="checkout">The LocalExile checkout Waypoint runs, asked each time: its client's ItemFilter.bank has the
    /// sounds, and its mods' loot-filter.json files their rules and items (<see cref="ModFilters"/>).</param>
    public FilterEditorServer(string gameFolder, string dataFolder, HttpClient http, Func<string?> checkout)
    {
        (_gameFolder, _dataFolder, _http, _checkout) = (gameFolder, dataFolder, http, checkout);
        _customization = File.Exists(CustomizationFile) ? FilterCustomization.FromJson(File.ReadAllText(CustomizationFile)) : new();
        int port = FreePort();
        Url = $"http://localhost:{port}/{_token}/";
        _listener.Prefixes.Add(Url);
    }

    public string Url { get; }

    string CustomizationFile => Path.Combine(_dataFolder, "customization.json");

    /// <summary>The file a customization is written to: named so the player tells it apart from retail's filters, which
    /// share the folder.</summary>
    public static string OutputName(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return $"LocalExile - {(clean.Length > 0 ? clean : "filter")}.filter";
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(Serve);
    }

    async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    async Task Handle(HttpListenerContext context)
    {
        var (request, response) = (context.Request, context.Response);
        try
        {
            string path = request.Url!.AbsolutePath;
            string prefix = $"/{_token}/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) { response.StatusCode = 404; return; }
            string route = path[prefix.Length..];
            await _gate.WaitAsync();
            try
            {
                switch (request.HttpMethod, route)
                {
                    case ("GET", ""): await Send(response, "text/html; charset=utf-8", Page()); break;
                    case ("GET", "api/model"): await Send(response, "application/json", (await Model(request.QueryString["strictness"])).ToJsonString()); break;
                    case ("PUT", "api/customization"): Save(await Body(request)); await Send(response, "application/json", "{}"); break;
                    case ("GET", "api/items"): await Send(response, "application/json", (await Items()).ToJsonString()); break;
                    case ("POST", "api/simulate"): await Send(response, "application/json", (await Simulate(JsonNode.Parse(await Body(request)) as JsonObject ?? [])).ToJsonString()); break;
                    case ("POST", "api/identify"): await Send(response, "application/json", (await Identify(JsonNode.Parse(await Body(request)) as JsonObject ?? [])).ToJsonString()); break;
                    case ("GET", "api/sounds"): await Send(response, "application/json", (await Sounds()).ToJsonString()); break;
                    case ("GET", _) when route.StartsWith("sound/", StringComparison.Ordinal): await SendSound(response, route["sound/".Length..]); break;
                    case ("GET", "custom-sound"): await SendCustomSound(response, request.QueryString["file"]); break;
                    case ("POST", "api/write"): await Send(response, "application/json", (await Write()).ToJsonString()); break;
                    default: response.StatusCode = 404; break;
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception e)
        {
            response.StatusCode = 500;
            await Send(response, "application/json", new JsonObject { ["error"] = e.Message }.ToJsonString());
        }
        finally { response.Close(); }
    }

    async Task<FilterDocument> Base(string strictness)
    {
        if (!_bases.TryGetValue(strictness, out var doc))
            _bases[strictness] = doc = FilterDocument.Parse(await FilterSources.ReadAsync(_http, FilterSources.For(strictness)));
        return doc;
    }

    async Task<IReadOnlyList<OptionChapter>> Chapters() =>
        _chapters ??= OptionFile.Parse(await FilterSources.ReadAsync(_http, FilterSources.Options));

    /// <summary>The mods the checkout has on that have a loot-filter.json, and what is wrong with the others.</summary>
    (List<ModFilter> Mods, List<string> Problems) Mods()
    {
        var problems = new List<string>();
        return (ModFilters.Read(_checkout(), problems), problems);
    }

    /// <summary>The option file's chapters, and the Mods chapter when a mod has rules or items.</summary>
    async Task<IReadOnlyList<OptionChapter>> Chapters(IReadOnlyList<ModFilter> mods) =>
        ModFilters.Chapter(mods, L.Text("Mods")) is { } chapter ? [.. await Chapters(), chapter] : await Chapters();

    /// <summary>NeverSink's filter at the chosen strictness with the mods' rules and items: what the player's changes apply to.</summary>
    async Task<FilterDocument> Prepared(IReadOnlyList<ModFilter> mods, List<string> problems)
    {
        var filter = FilterDocument.Parse((await Base(_customization.Strictness)).ToString());
        ModFilters.Apply(filter, mods, problems);
        return filter;
    }

    /// <summary>The editor's model: the sections and rules, each rule with the base filter's state, and the player's changes.</summary>
    async Task<JsonObject> Model(string? strictness)
    {
        if (strictness is not null && FilterSources.Strictness.Any(s => s.Id == strictness)) _customization.Strictness = strictness;
        var (mods, problems) = Mods();
        var blocks = (await Prepared(mods, problems)).ByKey();
        var modItems = mods.SelectMany(m => m.Items).GroupBy(i => i.Tier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(i => i.BaseType).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        JsonNode? Node(OptionNode node) => node switch
        {
            OptionChapter c => new JsonObject { ["kind"] = "chapter", ["title"] = c.Title, ["children"] = Nodes(c.Children) },
            OptionSection s => new JsonObject { ["kind"] = "section", ["title"] = s.Title, ["children"] = Nodes(s.Children) },
            OptionText t => new JsonObject { ["kind"] = t.Kind, ["text"] = t.Text, ["details"] = t.Details },
            OptionRule r when blocks.TryGetValue(r.Key, out var block) => Rule(r, block, modItems.GetValueOrDefault(r.Key) ?? []),
            OptionUnsupported u => new JsonObject { ["kind"] = "unsupported", ["name"] = u.Name },
            _ => null,
        };
        JsonArray Nodes(IReadOnlyList<OptionNode> nodes) => new(nodes.Select(Node).Where(n => n is not null).ToArray());
        return new JsonObject
        {
            ["version"] = FilterSources.Version,
            ["strictness"] = new JsonArray(FilterSources.Strictness.Select(s => (JsonNode)s.Id).ToArray()),
            ["output"] = Path.Combine(_gameFolder, OutputName(_customization.Name)),
            ["customization"] = JsonNode.Parse(_customization.ToJson()),
            ["chapters"] = Nodes(await Chapters(mods)),
            ["modProblems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
            ["texts"] = Texts(),
        };
    }

    static JsonObject Rule(OptionRule rule, FilterBlock block, HashSet<string> modItems)
    {
        var items = rule.TierList is null ? null : FilterSyntax.List(block.Find(rule.TierStat)?.Value ?? "").Values;
        return new()
        {
        ["kind"] = "rule",
        ["id"] = rule.Id,
        ["title"] = rule.Title,
        ["buttons"] = rule.Buttons,
        ["controls"] = new JsonArray(rule.Controls.Select(c => (JsonNode)c).ToArray()),
        ["tierList"] = rule.TierList,
        ["tierStat"] = rule.TierStat,
        ["description"] = rule.Description,
        ["action"] = block.Disabled ? "Disable" : block.Action,
        ["lines"] = new JsonArray(block.Lines.Where(l => !l.IsBlank).Select(l => (JsonNode)new JsonArray(l.Keyword, l.Value)).ToArray()),
        ["items"] = items is null ? null : new JsonArray(items.Select(v => (JsonNode)v).ToArray()),
        ["modItems"] = items is null ? null : new JsonArray(items.Where(modItems.Contains).Select(v => (JsonNode)v).ToArray()),
        };
    }

    void Save(string json)
    {
        var customization = FilterCustomization.FromJson(json);
        foreach (var id in customization.Rules.Where(r => r.Value.IsEmpty).Select(r => r.Key).ToList()) customization.Rules.Remove(id);
        if (!FilterSources.Strictness.Any(s => s.Id == customization.Strictness)) throw new InvalidDataException("Unknown strictness.");
        _customization = customization;
        Directory.CreateDirectory(_dataFolder);
        string temp = CustomizationFile + ".tmp";
        File.WriteAllText(temp, customization.ToJson());
        File.Move(temp, CustomizationFile, overwrite: true);
    }

    /// <summary>The base filter with the mods' parts and the player's changes: what Write puts in the game's folder.</summary>
    async Task<(FilterDocument Filter, List<string> Skipped)> Applied(IReadOnlyList<ModFilter> mods)
    {
        var filter = await Prepared(mods, []);
        var skipped = _customization.ApplyTo(filter, OptionFile.Rules(await Chapters(mods)));
        ModFilters.Restore(filter, mods);
        return (filter, skipped);
    }

    async Task<ItemCatalog> Catalog() => _catalog ??= ItemCatalog.Parse(await FilterSources.ReadAsync(_http, FilterSources.BaseTypes));

    /// <summary>Every base type's name and class, the mods' own included, for the page's item search.</summary>
    async Task<JsonArray> Items() =>
        new(ModFilters.Catalog(await Catalog(), Mods().Mods).ByName.Values.OrderBy(i => i.BaseType, StringComparer.OrdinalIgnoreCase)
            .Select(i => (JsonNode)new JsonArray(i.BaseType, i.Class)).ToArray());

    /// <summary>
    /// Drops run through the player's filter as it is now: a preset's sample ({"preset":"currency","count":20}) or named
    /// base types ({"items":["Divine Orb"]}), at an area level. Each comes back with what the filter does and the rule that
    /// decides it.
    /// </summary>
    async Task<JsonObject> Simulate(JsonObject body)
    {
        int areaLevel = Math.Clamp((int?)body["areaLevel"] ?? 68, 1, 100);
        var mods = Mods().Mods;
        var filter = await Current(mods);
        var catalog = ModFilters.Catalog(await Catalog(), mods);
        var drops = body["items"] is JsonArray names
            ? DropSimulation.Named(names.Select(n => (string?)n).OfType<string>().Take(50), areaLevel, catalog)
            : DropSimulation.Generate((string?)body["preset"] ?? "mixed", Math.Clamp((int?)body["count"] ?? 20, 1, 100), areaLevel,
                (int?)body["seed"] ?? Environment.TickCount, await Prepared(mods, []), catalog);
        var rules = await RulesByKey(mods);
        return new JsonObject { ["drops"] = new JsonArray(drops.Select(item => (JsonNode)Drop(item, FilterMatcher.Match(filter, item, areaLevel), rules)).ToArray()) };
    }

    async Task<FilterDocument> Current(IReadOnlyList<ModFilter> mods)
    {
        string key = _customization.ToJson() + System.Text.Json.JsonSerializer.Serialize(mods);
        if (_applied?.Key != key) _applied = (key, (await Applied(mods)).Filter);
        return _applied.Value.Filter;
    }

    async Task<Dictionary<string, OptionRule>> RulesByKey(IReadOnlyList<ModFilter> mods) =>
        OptionFile.Rules(await Chapters(mods)).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());

    /// <summary>An item and what the filter does with it, with the editor's rule that decides it (null when the deciding
    /// rule is one the option file does not show; "block" then names it by NeverSink's type and tier).</summary>
    static JsonObject Drop(SimItem item, FilterMatch match, Dictionary<string, OptionRule> rules)
    {
        var decider = match.Rules.LastOrDefault();
        var rule = decider?.Key is { } k && rules.TryGetValue(k, out var r) ? r : null;
        return new JsonObject
        {
            ["name"] = item.BaseType, ["class"] = item.Class, ["rarity"] = item.Rarity, ["stack"] = item.StackSize,
            ["visibility"] = match.Visibility,
            ["actions"] = new JsonObject(match.Actions.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value))),
            ["rule"] = rule?.Id, ["ruleTitle"] = rule?.Title,
            ["block"] = Describe(decider),
        };
    }

    static string? Describe(FilterBlock? block) => block?.Key?.Replace(';', ' ') ?? block?.HeaderRest.TrimStart('#', ' ');

    /// <summary>An item the player copied in game (Ctrl+C): what the editor read from it and what the filter does with it,
    /// with every rule it passed through on the way (Continue rules first).</summary>
    async Task<JsonObject> Identify(JsonObject body)
    {
        string text = (string?)body["text"] ?? "";
        if (!ItemText.Looks(text)) throw new InvalidDataException(L.Text("An item copied in game starts with \"Item Class:\"; this text does not."));
        int areaLevel = Math.Clamp((int?)body["areaLevel"] ?? 68, 1, 100);
        var mods = Mods().Mods;
        var imported = ItemText.Parse(text, ModFilters.Catalog(await Catalog(), mods));
        var item = imported.Item;
        var match = FilterMatcher.Match(await Current(mods), item, areaLevel);
        var rules = await RulesByKey(mods);
        var drop = Drop(item, match, rules);
        if (imported.Name is { } name) drop["title"] = name;
        var facts = new List<string> { item.Class, item.Rarity };
        if (item.ItemLevel > 1) facts.Add(L.Text("Item level {0}", item.ItemLevel));
        if (item.StackSize > 1) facts.Add(L.Text("Stack {0}", item.StackSize));
        if (item.Quality > 0) facts.Add(L.Text("Quality {0}", item.Quality));
        if (item.MapTier > 0) facts.Add(L.Text("Map tier {0}", item.MapTier));
        if (item.GemLevel > 0) facts.Add(L.Text("Gem level {0}", item.GemLevel));
        if (item.SocketGroups.Count > 0) facts.Add(L.Text("Sockets {0}", string.Join(" ", item.SocketGroups)));
        if (!item.Identified) facts.Add(L.Text("Unidentified"));
        if (item.Corrupted) facts.Add(L.Text("Corrupted"));
        if (item.Mirrored) facts.Add(L.Text("Mirrored"));
        facts.AddRange(item.Influences);
        facts.AddRange(item.Flags);
        if (item.ExplicitMods.Count > 0) facts.Add(L.Text("Mods: {0}", string.Join(", ", item.ExplicitMods)));
        return new JsonObject
        {
            ["drop"] = drop,
            ["facts"] = new JsonArray(facts.Select(f => (JsonNode)f).ToArray()),
            ["notes"] = new JsonArray(imported.Notes.Select(n => (JsonNode)(n.Arg is null ? L.Text(n.Text) : L.Text(n.Text, n.Arg))).ToArray()),
            ["chain"] = new JsonArray(match.Rules.Select(b =>
            {
                var rule = b.Key is { } k && rules.TryGetValue(k, out var r) ? r : null;
                return (JsonNode)new JsonObject
                {
                    ["rule"] = rule?.Id, ["title"] = rule?.Title, ["block"] = Describe(b), ["action"] = b.Action,
                    ["continue"] = b.Find("Continue") is not null,
                };
            }).ToArray()),
        };
    }

    /// <summary>Which alert sounds the page can play: every id when the client's ItemFilter.bank decodes, else why not.</summary>
    async Task<JsonObject> Sounds()
    {
        try
        {
            await SoundFolder();
            return new JsonObject { ["ids"] = new JsonArray(FilterSounds.Ids.Select(i => (JsonNode)i).ToArray()) };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new JsonObject { ["ids"] = new JsonArray(), ["error"] = e.Message };
        }
    }

    async Task<string> SoundFolder() => _checkout() is { Length: > 0 } checkout
        ? await FilterSounds.FolderAsync(_http, Path.Combine(checkout, "Client"), _dataFolder)
        : throw new InvalidOperationException(L.Text("Waypoint has no LocalExile checkout with a client yet."));

    async Task SendSound(HttpListenerResponse response, string id)
    {
        if (FilterSounds.Sample(Uri.UnescapeDataString(id)) is not { } sample) { response.StatusCode = 404; return; }
        string file = Path.Combine(await SoundFolder(), sample + ".wav");
        if (!File.Exists(file)) { response.StatusCode = 404; return; }
        await SendFile(response, "audio/wav", file);
    }

    /// <summary>A CustomAlertSound's file: only a file directly in the game's filter folder, where the game looks for it.</summary>
    async Task SendCustomSound(HttpListenerResponse response, string? name)
    {
        string? file = name is { Length: > 0 } && Path.GetFileName(name) == name ? Path.Combine(_gameFolder, name) : null;
        string type = Path.GetExtension(name ?? "").ToLowerInvariant() switch { ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".ogg" => "audio/ogg", _ => "" };
        if (file is null || type.Length == 0 || !File.Exists(file)) { response.StatusCode = 404; return; }
        await SendFile(response, type, file);
    }

    static async Task SendFile(HttpListenerResponse response, string type, string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file);
        response.ContentType = type;
        response.Headers["Cache-Control"] = "max-age=3600";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>Writes the player's filter into the game's folder: a fresh copy of the base filter with the mods' parts and
    /// the player's changes. The rules other mods wrote into the file themselves, between their marker lines, stay at its top.</summary>
    async Task<JsonObject> Write()
    {
        var (mods, problems) = Mods();
        var (filter, skipped) = await Applied(mods);
        Directory.CreateDirectory(_gameFolder);
        string file = Path.Combine(_gameFolder, OutputName(_customization.Name));
        if (File.Exists(file))
            filter.Parts.InsertRange(0, ModFilters.Foreign(await File.ReadAllTextAsync(file), mods.Select(m => m.ModId)).Select(l => new FilterText(l)));
        await File.WriteAllTextAsync(file, filter.ToString(), new UTF8Encoding(false));
        return new JsonObject
        {
            ["file"] = file, ["skipped"] = new JsonArray(skipped.Select(s => (JsonNode)s).ToArray()),
            ["modProblems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        };
    }

    static async Task<string> Body(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    static async Task Send(HttpListenerResponse response, string type, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = type;
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    static string Page()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LootFilter/editor.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The page's own words, in the player's language. FilterBlade's section and rule titles stay in English.</summary>
    static JsonObject Texts() => new()
    {
        ["Loot filter"] = L.Text("Loot filter"),
        ["Strictness"] = L.Text("Strictness"),
        ["Filter name"] = L.Text("Filter name"),
        ["Write filter"] = L.Text("Write filter"),
        ["Temporary beams only"] = L.Text("Temporary beams only"),
        ["A beam that stays keeps a quiet looping sound near the item until it is picked up. A temporary beam shows only as the item drops, without the loop."] = L.Text("A beam that stays keeps a quiet looping sound near the item until it is picked up. A temporary beam shows only as the item drops, without the loop."),
        ["Search rules and items: a name, a tier, a class (press / to come back here)"] = L.Text("Search rules and items: a name, a tier, a class (press / to come back here)"),
        ["Drop preview"] = L.Text("Drop preview"),
        ["Area level"] = L.Text("Area level"),
        ["Items"] = L.Text("Items"),
        ["Show hidden"] = L.Text("Show hidden"),
        ["{0} rules"] = L.Text("{0} rules"),
        ["Where these items go, as a plain drop at area level {0}"] = L.Text("Where these items go, as a plain drop at area level {0}"),
        ["Decided by a rule the editor does not show: {0}"] = L.Text("Decided by a rule the editor does not show: {0}"),
        ["No rule matches it; the game shows it plainly."] = L.Text("No rule matches it; the game shows it plainly."),
        ["Currency"] = L.Text("Currency"),
        ["Valuable currency"] = L.Text("Valuable currency"),
        ["Uniques"] = L.Text("Uniques"),
        ["Divination cards"] = L.Text("Divination cards"),
        ["Map drop mix"] = L.Text("Map drop mix"),
        ["Sound {0}"] = L.Text("Sound {0}"),
        ["{0} hidden"] = L.Text("{0} hidden"),
        ["Show"] = L.Text("Show"),
        ["Hide"] = L.Text("Hide"),
        ["Disable"] = L.Text("Disable"),
        ["Changed"] = L.Text("Changed"),
        ["Reset"] = L.Text("Reset"),
        ["Font size"] = L.Text("Font size"),
        ["Text"] = L.Text("Text"),
        ["Border"] = L.Text("Border"),
        ["Background"] = L.Text("Background"),
        ["Sound"] = L.Text("Sound"),
        ["Volume"] = L.Text("Volume"),
        ["Minimap icon"] = L.Text("Minimap icon"),
        ["Beam"] = L.Text("Beam"),
        ["Temporary"] = L.Text("Temporary"),
        ["None"] = L.Text("None"),
        ["Move to"] = L.Text("Move to"),
        ["Add item"] = L.Text("Add item"),
        ["Remove"] = L.Text("Remove"),
        ["Any"] = L.Text("Any"),
        ["Conditions"] = L.Text("Conditions"),
        ["Saved"] = L.Text("Saved"),
        ["Experimental"] = L.Text("Experimental"),
        ["Experimental: the filter stays NeverSink's 8.20.2 for the 3.29 client, and some of FilterBlade's options are not here yet."] = L.Text("Experimental: the filter stays NeverSink's 8.20.2 for the 3.29 client, and some of FilterBlade's options are not here yet."),
        ["A mod's loot filter part was left out: {0}"] = L.Text("A mod's loot filter part was left out: {0}"),
        ["Put in this tier by a mod"] = L.Text("Put in this tier by a mod"),
        ["Play alert sounds"] = L.Text("Play alert sounds"),
        ["Each item is a plain drop: no mods, influence or sockets. Click a label to open the rule that decides it."] = L.Text("Each item is a plain drop: no mods, influence or sockets. Click a label to open the rule that decides it."),
        ["Alert sounds are not available: {0}"] = L.Text("Alert sounds are not available: {0}"),
        ["Play sound"] = L.Text("Play sound"),
        ["Import item"] = L.Text("Import item"),
        ["Paste an item copied in game with Ctrl+C (Ctrl+Alt+C adds the mod names), or press Ctrl+V anywhere on this page."] = L.Text("Paste an item copied in game with Ctrl+C (Ctrl+Alt+C adds the mod names), or press Ctrl+V anywhere on this page."),
        ["Find its rule"] = L.Text("Find its rule"),
        ["Close"] = L.Text("Close"),
        ["Rules it passes, in order:"] = L.Text("Rules it passes, in order:"),
        ["continues"] = L.Text("continues"),
        ["Written to {0}"] = L.Text("Written to {0}"),
        ["Choose it in the item filter list of the game options; if it is already chosen, reload it there."] = L.Text("Choose it in the item filter list of the game options; if it is already chosen, reload it there."),
        ["Not in this filter version: {0}"] = L.Text("Not in this filter version: {0}"),
        ["Not editable here yet: {0}"] = L.Text("Not editable here yet: {0}"),
        ["Waypoint closed this editor. Open it again from Tools."] = L.Text("Waypoint closed this editor. Open it again from Tools."),
    };

    public void Dispose()
    {
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
        _gate.Dispose();
    }
}

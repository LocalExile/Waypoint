namespace Launcher.Core;

public sealed partial class GameSystems
{
    // Mirrors LeagueCatalog's public-file list, not compatibility families or migration/death destinations.
    // No listing command is executed: this predicts the next server start, not a running process's cached catalogue.
    public bool LoadedDefinition(GameLeague league) => Rooted(league)
        && _definitionOrder.Contains(league.Folder, StringComparer.OrdinalIgnoreCase)
        && (Directory.Exists(Path.Combine(league.Folder, "Source")) || Directory.Exists(Path.Combine(league.Folder, "Binaries")))
        && league.BaseSystems.Distinct(StringComparer.OrdinalIgnoreCase).Count() == league.BaseSystems.Length;

    public bool OfferedDefinition(GameLeague league) => LoadedDefinition(league) && league.Creatable && !league.Ended;

    GameLeague? LinkedParent(GameLeague league) => Leagues.FirstOrDefault(l => l.Name.Equals(league.EntryOf, StringComparison.OrdinalIgnoreCase));

    /// <summary>Only explicit entryOf links establish a flag's mode tree. Broken trees never become invented flags.</summary>
    public GameLeague? FlagOf(GameLeague league)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var at = league; seen.Add(at.Name);)
        {
            if (at.IsMain) return at;
            if (LinkedParent(at) is not { } parent) return null;
            at = parent;
        }
        return null;
    }

    public bool ReachableMode(GameLeague league)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var at = league; seen.Add(at.Name);)
        {
            if (!OfferedDefinition(at)) return false;
            if (at.IsMain) return true;
            if (at.EntryKind is not ("hardcore" or "ssf" or "form") || LinkedParent(at) is not { } parent) return false;
            // Two children on the same wire link are ambiguous; do not claim which one the client will choose.
            if (Leagues.Count(l => !l.IsMain && OfferedDefinition(l) && l.EntryOf.Equals(at.EntryOf, StringComparison.OrdinalIgnoreCase)
                && l.EntryKind == at.EntryKind) != 1) return false;
            at = parent;
        }
        return false;
    }

    public string ModeLinkLabel(string kind) => kind switch
    {
        "hardcore" => L.Text("Hardcore"), "ssf" => L.Text("Solo self-found"), "form" => L.Text("Ruthless"),
        _ => L.Text("Unknown mode link")
    };

    /// <summary>The exact option chain, including each intermediate state, rather than flags inferred from gameplay booleans.</summary>
    public string ModePath(GameLeague league)
    {
        var parts = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var at = league; !at.IsMain && seen.Add(at.Name);)
        {
            parts.Insert(0, ModeLinkLabel(at.EntryKind));
            if (LinkedParent(at) is not { } parent) break;
            at = parent;
        }
        return parts.Count == 0 ? L.Text("Flag default") : string.Join(" → ", parts);
    }

    public IReadOnlyList<GameLeagueCard> Cards
    {
        get
        {
            int Order(GameLeague l) => Array.FindIndex(_definitionOrder, p => p.Equals(l.Folder, StringComparison.OrdinalIgnoreCase));
            var cards = Leagues.Where(l => l.IsMain).OrderBy(l => l.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(Order).Select(root => new GameLeagueCard(root.Name,
                    Leagues.Where(l => ReferenceEquals(FlagOf(l), root)).OrderBy(l => ReferenceEquals(l, root) ? 0 : 1).ThenBy(ModePath).ToArray(),
                    OfferedDefinition(root))).ToList();
            // Available only through the explicit inspection filter, with no implication that these have a player flag.
            cards.AddRange(Leagues.Where(l => FlagOf(l) is null).Select(l => new GameLeagueCard(l.Name, [l], false, false)));
            return cards;
        }
    }
}

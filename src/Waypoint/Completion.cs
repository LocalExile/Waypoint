using System.Text.RegularExpressions;

namespace Waypoint;

/// <summary>One suggestion: what replaces the text being completed, and how the list shows it.</summary>
sealed record Suggestion(string Insert, string Label, string Detail);

/// <summary>A value an argument of some kind can take (<c>GameServer.Gateway values &lt;kind&gt;</c>).</summary>
sealed record ArgumentValue(string Value, string Detail);

/// <summary>The suggestions for a line: they replace the text from <see cref="From"/> on. <see cref="Loading"/> names
/// an argument kind whose values are not loaded yet.</summary>
sealed record CompletionResult(int From, IReadOnlyList<Suggestion> Items, string? Loading = null)
{
    public static readonly CompletionResult None = new(0, []);
}

/// <summary>
/// Suggestions while a command line is typed: the command's name first, then each argument in turn. An argument the
/// Gateway names a kind for (/goto's area, /spawn_mob's monster …) is suggested from that kind's values, and one that
/// takes several words (an item's name) counts as done once a whole value and a space are typed. Any other argument is
/// suggested from the usage's own choices: "[normal|magic|rare]", "[all | &lt;id&gt;]", or a form's leading word
/// ("/cosmetic list | /cosmetic off").
/// </summary>
static partial class Completion
{
    public const int MaxItems = 200;

    public static CompletionResult For(string line, CommandCatalog catalog, Func<string, IReadOnlyList<ArgumentValue>?> values)
    {
        if (!line.StartsWith('/')) return CompletionResult.None;
        int space = line.IndexOf(' ');
        if (space < 0)
            return new(0, [.. catalog.Suggest(line).Take(MaxItems).Select(c => new Suggestion($"/{c.Name}", $"/{c.Name}", c.Short[(c.Name.Length + 1)..].Trim()))]);
        if (catalog.Of(line) is not { } command) return CompletionResult.None;

        var parameters = Parameters(command);
        int at = space + 1;
        for (int position = 0; position < parameters.Count; position++)
        {
            string rest = line[at..];
            if (parameters[position].Kind is { } kind)
            {
                if (values(kind) is not { } known) return new(at, [], kind);
                if (Consumed(rest, known) is int used) { at += used; continue; }
                return new(at, Rank(rest, known.Select(Shown).OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase)));
            }
            int end = rest.IndexOf(' ');
            if (end >= 0) { at += end + 1; continue; }
            return new(at, Rank(rest, parameters[position].Choices.Select(c => new Suggestion(c, c, ""))));
        }
        return new(at, []);
    }

    /// <summary>How much of the text a whole value and its space take (a monster's "@level" included), or null while
    /// the value is still being typed. A row given by its Id counts by its name too: the command finds a name as well.</summary>
    static int? Consumed(string text, IReadOnlyList<ArgumentValue> known)
    {
        int best = -1;
        foreach (var v in known)
            foreach (string given in IsId(v) ? [v.Value, v.Detail] : (string[])[v.Value])
            {
                if (given.Length <= best || !text.StartsWith(given, StringComparison.OrdinalIgnoreCase)) continue;
                int after = given.Length;
                if (after < text.Length && text[after] == '@')
                    while (after < text.Length && text[after] != ' ') after++;
                if (after < text.Length && text[after] == ' ') best = Math.Max(best, after + 1);
            }
        return best < 0 ? null : best;
    }

    static bool IsId(ArgumentValue v) => v.Value.Contains('/') && v.Detail.Length > 0;

    /// <summary>A value as the list shows it: a row given by its Id ("Metadata/Monsters/…", for a name several rows share)
    /// shows that name first and the Id beside it.</summary>
    static Suggestion Shown(ArgumentValue v) => IsId(v) ? new(v.Value, v.Detail, v.Value) : new(v.Value, v.Value, v.Detail);

    /// <summary>Those whose label or value starts with the typed text, then those whose label or value contains it,
    /// then those whose detail does.</summary>
    static List<Suggestion> Rank(string typed, IEnumerable<Suggestion> all)
    {
        var list = all.ToList();
        if (typed.Length == 0) return [.. list.Take(MaxItems)];
        bool Starts(Suggestion s) => s.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || s.Insert.StartsWith(typed, StringComparison.OrdinalIgnoreCase);
        bool Contains(Suggestion s) => s.Label.Contains(typed, StringComparison.OrdinalIgnoreCase) || s.Insert.Contains(typed, StringComparison.OrdinalIgnoreCase);
        return [.. list.Where(Starts)
            .Concat(list.Where(s => !Starts(s) && Contains(s)))
            .Concat(list.Where(s => !Contains(s) && s.Detail.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxItems)];
    }

    internal sealed record Parameter(string? Kind, IReadOnlyList<string> Choices);

    /// <summary>The command's arguments by position: the Gateway's kinds first, then the usage's choices at each position
    /// across its forms ("/x a | /x b c" and "/x &lt;n&gt; | off" both have forms).</summary>
    internal static List<Parameter> Parameters(ChatCommand command)
    {
        string usage = command.Short;
        var forms = SplitTopLevel(usage, '|').Select(f => f.Trim()).Select(f =>
            f.StartsWith($"/{command.Name}", StringComparison.OrdinalIgnoreCase) ? f[(command.Name.Length + 1)..].Trim() : f);
        var positions = new List<List<string>>();
        foreach (string form in forms)
        {
            var chunks = SplitTopLevel(form, ' ').Where(c => c.Length > 0).ToList();
            for (int i = 0; i < chunks.Count; i++)
            {
                while (positions.Count <= i) positions.Add([]);
                positions[i].AddRange(Choices(chunks[i]));
            }
        }
        int count = Math.Max(positions.Count, command.Args.Count);
        return [.. Enumerable.Range(0, count).Select(i => new Parameter(
            i < command.Args.Count ? command.Args[i] : null,
            i < positions.Count ? positions[i].Distinct(StringComparer.OrdinalIgnoreCase).ToList() : []))];
    }

    /// <summary>The literal words one chunk of a usage allows: a bare word itself; inside its first bracket, each
    /// alternative's first word that is not a bracketed placeholder or a placeholder's name. An angle bracket without
    /// alternatives is a value to supply ("&lt;area id or name&gt;"), never a word to type.</summary>
    static IEnumerable<string> Choices(string chunk)
    {
        if (chunk[0] is not ('<' or '['))
            return Literal().IsMatch(chunk) && !Placeholders.Contains(chunk) ? [chunk] : [];
        int close = Matching(chunk, 0);
        string inner = chunk[1..close];
        var alternatives = SplitTopLevel(inner, '|');
        if (chunk[0] == '<' && alternatives.Count == 1) return [];
        return alternatives.Select(a => a.Trim().Split(' ')[0])
            .Where(w => w.Length > 0 && Literal().IsMatch(w) && !Placeholders.Contains(w)).ToList();
    }

    /// <summary>Words a usage writes for a value the player supplies rather than types as such.</summary>
    static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "amount", "id", "n", "count", "name", "type", "percent", "degrees", "page", "filter", "layout", "speed",
        "index", "hex", "x", "y", "z", "path", "damage", "colours", "section", "values", "mod", "level", "seed", "a", "b",
        "c", "d", "kind", "rotation", "skill", "file", "u32", "u16", "u64", "u8", "flags", "monster", "item",
    };

    [GeneratedRegex(@"^[A-Za-z_][\w+-]*$")]
    private static partial Regex Literal();

    static int Matching(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] is '<' or '[') depth++;
            else if (s[i] is '>' or ']' && --depth == 0) return i;
        }
        return s.Length;
    }

    /// <summary>Splits at a separator outside brackets.</summary>
    static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '<' or '[') depth++;
            else if (s[i] is '>' or ']') depth = Math.Max(0, depth - 1);
            else if (s[i] == separator && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }
}

using System.Globalization;
using System.Text;

namespace Launcher.Core.LootFilter;

/// <summary>What the editor shows, read from a FilterBlade option file.</summary>
public abstract record OptionNode;

/// <summary>One of the large groups (General, Maps, Endgame …), titled by its first SectionTitle.</summary>
public sealed record OptionChapter(string Title, IReadOnlyList<OptionNode> Children) : OptionNode;

/// <summary>A collapsible group of rules, with FilterBlade's icon name (an image of theirs, which Waypoint does not ship).</summary>
public sealed record OptionSection(string Title, string? Icon, IReadOnlyList<OptionNode> Children) : OptionNode;

/// <summary>Title, Description or Warning text between rules.</summary>
public sealed record OptionText(string Kind, string Text, string? Details = null) : OptionNode;

/// <summary>
/// A QuickUI: one rule of the filter the player can edit. <see cref="Id"/> is FilterBlade's name for it, which also keys the
/// player's changes; <see cref="Key"/> is the rule's "type;tier" in NeverSink's filter. <see cref="Buttons"/> holds S, H and D
/// for the Show, Hide and Disable buttons; <see cref="Controls"/> the conditions the player may change (ItemLevel, StackSize …).
/// A rule with a <see cref="TierList"/> lists items by <see cref="TierStat"/> (BaseType unless named) that the player moves
/// between tiers; <see cref="TierFlags"/>: O exact match (==), A an "any" button, D duplicates allowed, S a single tier.
/// </summary>
public sealed record OptionRule(string Id, string Key, double Version, string Buttons, IReadOnlyList<string> Controls, string Title,
    string? TierList, string TierFlags, string TierStat, string? Description) : OptionNode;

/// <summary>A command the editor does not offer yet, kept so it can say what is missing.</summary>
public sealed record OptionUnsupported(string Name) : OptionNode;

/// <summary>
/// Reads FilterBlade's option files (NeverSinkDev/FilterBlade-Public-Assets, MIT), the language that describes FilterBlade's
/// Customize screen: <c>Chapter(){ Section("Currency", "icon", "E") { QuickUI([3.0, "Currency God Tier 1", "currency;t1exalted"],
/// "S", [], "S Tier:", ["normalCurrency", "O"]); } }</c>. Functions (<c>Function Name($s, $shd = "SHD") { … }</c>) are expanded
/// where called, visible in the section that defines them. The format is described in that repository's README_OptionFile.md.
/// </summary>
public static class OptionFile
{
    sealed record Command(string Name, string? Label, List<object?> Args, List<Command>? Body, List<(string Name, object? Default)>? Parameters);

    sealed record Var(string Name);

    /// <summary>A bare name in an argument: a Search defined earlier (<c>Search S0(8.1, "Uniques Tier 1", "uniques;t1") { … }</c>).</summary>
    sealed record Ref(string Name);

    static readonly HashSet<string> Containers = ["Chapter", "Section", "Box", "Function"];
    static readonly HashSet<string> Ignored =
    [
        "Info_OptionVersion", "DefensiveSearch", "DeepSearchMulti", "Conditional", "ElementAdder_Tier", "TierListToolBar",
    ];

    public static IReadOnlyList<OptionChapter> Parse(string text)
    {
        var commands = new Parser(Tokenize(text)).Statements(endAtBrace: false);
        var scopes = new List<Dictionary<string, Command>> { new() };
        var chapters = new List<OptionChapter>();
        foreach (var command in commands)
        {
            if (command.Name is "Function" or "Search") { scopes[0][command.Label!] = command; continue; }
            if (command.Name != "Chapter") continue;
            var children = Build(command.Body ?? [], scopes, []);
            string title = children.OfType<OptionText>().FirstOrDefault(t => t.Kind == "SectionTitle")?.Text ?? "";
            chapters.Add(new OptionChapter(title, children.Where(c => c is not OptionText { Kind: "SectionTitle" }).ToList()));
        }
        return chapters;
    }

    /// <summary>Every rule under these nodes, in order.</summary>
    public static IEnumerable<OptionRule> Rules(IEnumerable<OptionNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is OptionRule rule) yield return rule;
            var children = node switch { OptionChapter c => c.Children, OptionSection s => s.Children, _ => null };
            if (children is not null)
                foreach (var inner in Rules(children)) yield return inner;
        }
    }

    static List<OptionNode> Build(List<Command> commands, List<Dictionary<string, Command>> scopes, Dictionary<string, object?> vars)
    {
        scopes.Add([]);
        var nodes = new List<OptionNode>();
        foreach (var raw in commands)
        {
            var command = raw with { Args = raw.Args.Select(a => Bind(a, vars, scopes)).ToList() };
            if (command.Name is "Function" or "Search") { scopes[^1][command.Label!] = command; continue; }
            if (Find(scopes, command.Name) is { Name: "Function" } function)
            {
                var bound = new Dictionary<string, object?>();
                for (int i = 0; i < function.Parameters!.Count; i++)
                    bound[function.Parameters[i].Name] = i < command.Args.Count ? command.Args[i] : function.Parameters[i].Default;
                nodes.AddRange(Build(function.Body ?? [], scopes, bound));
                continue;
            }
            switch (command.Name)
            {
                case "Section":
                case "Box":
                    nodes.Add(new OptionSection(Str(command.Args, 0) ?? "", Str(command.Args, 1), Build(command.Body ?? [], scopes, vars)));
                    break;
                case "Title" or "SectionTitle" or "Description" or "Warning":
                    nodes.Add(new OptionText(command.Name, Str(command.Args, 0) ?? "", Str(command.Args, 2)));
                    break;
                case "QuickUI":
                    // A rule names its Search by label (S0) or, from inside a Function, by the bare number (0).
                    var args = command.Args is [double index, ..] && Find(scopes, $"S{(int)index}") is { Name: "Search" } search
                        ? [search.Args, .. command.Args.Skip(1)] : command.Args;
                    nodes.Add(Rule(args) ?? (OptionNode)new OptionUnsupported("QuickUI (search)"));
                    break;
                default:
                    if (!Ignored.Contains(command.Name)) nodes.Add(new OptionUnsupported(command.Name));
                    break;
            }
        }
        scopes.RemoveAt(scopes.Count - 1);
        return nodes;
    }

    static OptionRule? Rule(List<object?> args)
    {
        if (args.ElementAtOrDefault(0) is not List<object?> { Count: 3 } rule || rule[1] is not string id || rule[2] is not string key) return null;
        var tier = args.ElementAtOrDefault(4) as List<object?>;
        var controls = (args.ElementAtOrDefault(2) as List<object?> ?? []).OfType<string>().ToList();
        string? description = args.ElementAtOrDefault(5) as string;
        return new OptionRule(id, key, rule[0] is double v ? v : 0, Str(args, 1) ?? "", controls,
            Str(args, 3) is { Length: > 0 } title ? title : id,
            tier is { Count: > 0 } ? tier[0] as string : null,
            tier?.ElementAtOrDefault(1) as string ?? "", tier?.ElementAtOrDefault(2) as string ?? "BaseType",
            description is { Length: > 0 } ? description : null);
    }

    static Command? Find(List<Dictionary<string, Command>> scopes, string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var function)) return function;
        return null;
    }

    static object? Bind(object? value, Dictionary<string, object?> vars, List<Dictionary<string, Command>> scopes) => value switch
    {
        Var v => vars.TryGetValue(v.Name, out var bound) ? bound : null,
        Ref r => Find(scopes, r.Name) is { Name: "Search" } search ? search.Args : null,
        List<object?> list => list.Select(item => Bind(item, vars, scopes)).ToList(),
        _ => value,
    };

    static string? Str(List<object?> args, int index) => args.ElementAtOrDefault(index) as string;

    // Tokens: (kind, text). Kinds: i identifier, v $variable, s string, n number, p punctuation.
    static List<(char Kind, string Text)> Tokenize(string text)
    {
        var tokens = new List<(char, string)>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '"')
            {
                var s = new StringBuilder();
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    s.Append(text[i]);
                }
                i++;
                tokens.Add(('s', s.ToString()));
                continue;
            }
            if (char.IsDigit(c) || (c is '-' or '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(('n', text[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$' or '?')
            {
                int start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '?' or '.')) i++;
                tokens.Add((c == '$' ? 'v' : 'i', text[start..i]));
                continue;
            }
            tokens.Add(('p', c.ToString()));
            i++;
        }
        return tokens;
    }

    sealed class Parser(List<(char Kind, string Text)> tokens)
    {
        int _at;

        (char Kind, string Text) Peek => _at < tokens.Count ? tokens[_at] : ('\0', "");
        bool Is(string punct) => Peek is ('p', var t) && t == punct;
        void Skip(string punct) { if (Is(punct)) _at++; }

        public List<Command> Statements(bool endAtBrace)
        {
            var list = new List<Command>();
            while (_at < tokens.Count && !(endAtBrace && Is("}")))
            {
                if (Peek.Kind != 'i') { _at++; continue; }
                list.Add(Statement());
            }
            Skip("}");
            return list;
        }

        Command Statement()
        {
            string name = tokens[_at++].Text;
            string? label = Peek.Kind == 'i' ? tokens[_at++].Text : null;
            var args = new List<object?>();
            List<(string, object?)>? parameters = null;
            if (Is("("))
            {
                _at++;
                if (name == "Function") parameters = Parameters();
                else args = Values(")");
            }
            List<Command>? body = null;
            if (Is("{"))
            {
                _at++;
                if (Containers.Contains(name)) body = Statements(endAtBrace: true);
                else SkipBlock();
            }
            Skip(";");
            return new Command(name, label, args, body, parameters);
        }

        List<(string, object?)> Parameters()
        {
            var list = new List<(string, object?)>();
            while (_at < tokens.Count && !Is(")"))
            {
                if (Peek.Kind != 'v') { _at++; continue; }
                string name = tokens[_at++].Text;
                object? value = null;
                if (Is("=")) { _at++; value = Value(); }
                list.Add((name, value));
                Skip(",");
            }
            Skip(")");
            return list;
        }

        List<object?> Values(string close)
        {
            var list = new List<object?>();
            while (_at < tokens.Count && !Is(close))
            {
                list.Add(Value());
                Skip(",");
            }
            Skip(close);
            return list;
        }

        object? Value()
        {
            var (kind, text) = tokens[_at++];
            return kind switch
            {
                's' => text,
                'n' => double.Parse(text, CultureInfo.InvariantCulture),
                'v' => new Var(text),
                'p' when text == "[" => Values("]"),
                'i' when text is "true" or "false" => text == "true",
                'i' when text != "null" => new Ref(text),
                _ => null,
            };
        }

        void SkipBlock()
        {
            for (int depth = 1; _at < tokens.Count && depth > 0; _at++)
                if (Is("{")) depth++;
                else if (Is("}")) depth--;
        }
    }
}

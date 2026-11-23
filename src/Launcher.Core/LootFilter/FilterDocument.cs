using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core.LootFilter;

/// <summary>One condition or action of a rule: <c>SetFontSize 45</c>. <see cref="Raw"/> is the line as read, kept while
/// the line is unchanged so an untouched filter is written back byte for byte.</summary>
public sealed record FilterLine(string Keyword, string Value, string? Raw = null)
{
    public string Text => Value.Length == 0 ? Keyword : $"{Keyword} {Value}";
    /// <summary>A blank or comment line inside a rule, kept as read: it neither ends the rule nor tests anything.</summary>
    public bool IsBlank => Keyword.Length == 0;
}

/// <summary>
/// One Show, Hide or Minimal rule. NeverSink names every rule in its header comment with a type and a tier
/// (<c>Show # %D3 $type->currency $tier->t1exalted</c>); FilterBlade's option files point at a rule by "type;tier".
/// A disabled rule is commented out line by line (<c>#Show …</c>, <c>#\tSetFontSize 40</c>): the game skips it.
/// </summary>
public sealed partial class FilterBlock
{
    public string Action { get; set; } = "Show";
    public bool Disabled { get; set; }
    /// <summary>The header after the action, comment included: <c> # $type->currency $tier->t1exalted</c>.</summary>
    public string HeaderRest { get; init; } = "";
    public List<FilterLine> Lines { get; } = [];
    internal string? RawHeader { get; set; }
    internal (string Action, bool Disabled) Original { get; set; }

    public string? Type => TypeTag().Match(HeaderRest) is { Success: true } m ? m.Groups[1].Value : null;
    public string? Tier => TierTag().Match(HeaderRest) is { Success: true } m ? m.Groups[1].Value : null;
    /// <summary>"type;tier", as FilterBlade names a rule; null for a rule without both tags.</summary>
    public string? Key => Type is { } type && Tier is { } tier ? $"{type};{tier}" : null;

    public FilterLine? Find(string keyword) => Lines.FirstOrDefault(l => l.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets a keyword's value, replacing its first line or adding one before the first action (the game reads
    /// conditions, then actions, and NeverSink keeps that order); null removes every line with it.</summary>
    public void Set(string keyword, string? value)
    {
        int at = Lines.FindIndex(l => l.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            Lines.RemoveAll(l => l.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));
            return;
        }
        var line = new FilterLine(keyword, value);
        if (at >= 0) { if (Lines[at].Text != line.Text) Lines[at] = line; return; }
        int firstAction = FilterSyntax.IsAction(keyword) ? Lines.FindIndex(l => l.Keyword == "Continue") : Lines.FindIndex(l => FilterSyntax.IsAction(l.Keyword));
        Lines.Insert(firstAction < 0 ? Lines.Count : firstAction, line);
    }

    [GeneratedRegex(@"\$type->(\S+)")] private static partial Regex TypeTag();
    [GeneratedRegex(@"\$tier->(\S+)")] private static partial Regex TierTag();
}

/// <summary>What sits between rules: comments, blank lines, the table of contents. Kept as read.</summary>
public sealed record FilterText(string Raw);

/// <summary>
/// A .filter file as the game reads it: text and rules in file order. <see cref="Parse"/> then <see cref="ToString"/>
/// gives the file back unchanged; a rule writes itself again only where it was changed.
/// </summary>
public sealed partial class FilterDocument
{
    public List<object> Parts { get; } = [];
    public IEnumerable<FilterBlock> Blocks => Parts.OfType<FilterBlock>();
    public string NewLine { get; private init; } = "\r\n";
    bool _endsWithNewLine = true;

    public static FilterDocument Parse(string text)
    {
        var doc = new FilterDocument { NewLine = text.Contains("\r\n") ? "\r\n" : "\n", _endsWithNewLine = text.Length == 0 || text.EndsWith('\n') };
        string[] lines = text.Split('\n');
        int count = doc._endsWithNewLine ? lines.Length - 1 : lines.Length;
        FilterBlock? block = null;
        for (int i = 0; i < count; i++)
        {
            string raw = lines[i].TrimEnd('\r');
            if (Header().Match(raw) is { Success: true } header)
            {
                block = new FilterBlock { Action = header.Groups[2].Value, Disabled = header.Groups[1].Success, HeaderRest = header.Groups[3].Value, RawHeader = raw };
                block.Original = (block.Action, block.Disabled);
                doc.Parts.Add(block);
                continue;
            }
            if (block is not null && Body().Match(raw) is { Success: true } body && body.Groups[1].Success == block.Disabled)
            {
                block.Lines.Add(new FilterLine(body.Groups[2].Value, body.Groups[3].Value.TrimEnd(), raw));
                continue;
            }
            // The game reads a rule up to the next Show, Hide or Minimal: a blank or comment line does not end a rule that is
            // on when more of its lines follow. A disabled rule is all comments, which the game skips, so it ends where it did.
            if (block is { Disabled: false } && Gap(raw))
            {
                int next = i;
                while (next < count && Gap(lines[next].TrimEnd('\r'))) next++;
                if (next < count && lines[next].TrimEnd('\r') is var after && !Header().IsMatch(after)
                    && Body().Match(after) is { Success: true } more && !more.Groups[1].Success)
                {
                    for (; i < next; i++) block.Lines.Add(new FilterLine("", "", lines[i].TrimEnd('\r')));
                    i--;
                    continue;
                }
            }
            block = null;
            doc.Parts.Add(new FilterText(raw));
        }
        return doc;
    }

    static bool Gap(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var part in Parts)
        {
            if (part is FilterText t) { text.Append(t.Raw).Append(NewLine); continue; }
            var block = (FilterBlock)part;
            string mark = block.Disabled ? "#" : "";
            bool same = block.RawHeader is not null && block.Original == (block.Action, block.Disabled);
            text.Append(same ? block.RawHeader : $"{mark}{block.Action}{block.HeaderRest}").Append(NewLine);
            foreach (var line in block.Lines)
                text.Append((same || line.IsBlank) && line.Raw is not null ? line.Raw : $"{mark}\t{line.Text}").Append(NewLine);
        }
        if (!_endsWithNewLine && text.Length >= NewLine.Length) text.Length -= NewLine.Length;
        return text.ToString();
    }

    /// <summary>The rules NeverSink tagged, by "type;tier". A key names one rule in its filters; a repeated one keeps the first.</summary>
    public Dictionary<string, FilterBlock> ByKey()
    {
        var map = new Dictionary<string, FilterBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in Blocks)
            if (block.Key is { } key) map.TryAdd(key, block);
        return map;
    }

    [GeneratedRegex(@"^(#)?(Show|Hide|Minimal)(\b.*)$")] private static partial Regex Header();
    [GeneratedRegex(@"^(#)?[ \t]+([A-Za-z]+)\s*(.*)$")] private static partial Regex Body();
}

/// <summary>The few facts about the filter language the editor needs.</summary>
public static class FilterSyntax
{
    static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetFontSize", "SetTextColor", "SetBorderColor", "SetBackgroundColor", "PlayAlertSound", "PlayAlertSoundPositional",
        "CustomAlertSound", "CustomAlertSoundOptional", "DisableDropSound", "EnableDropSound", "DisableDropSoundIfAlertSound",
        "EnableDropSoundIfAlertSound", "MinimapIcon", "PlayEffect", "Continue",
    };

    public static bool IsAction(string keyword) => Actions.Contains(keyword);

    /// <summary>The quoted values of a list condition, its operator apart: <c>== "A" "B"</c> → ("==", [A, B]).</summary>
    public static (string Operator, List<string> Values) List(string value)
    {
        string op = value.StartsWith("==") ? "==" : "";
        var values = new List<string>();
        foreach (Match m in Regex.Matches(value, "\"([^\"]*)\"|(\\S+)"))
            if (m.Groups[1].Success) values.Add(m.Groups[1].Value);
            else if (m.Value != "==") values.Add(m.Value);
        return (op, values);
    }

    public static string Quote(string op, IEnumerable<string> values) =>
        (op.Length > 0 ? op + " " : "") + string.Join(' ', values.Select(v => $"\"{v}\""));
}

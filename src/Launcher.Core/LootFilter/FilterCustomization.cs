using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core.LootFilter;

/// <summary>
/// What the player changed on one rule. <see cref="Visibility"/> is Show, Hide or Disable. <see cref="Lines"/> sets a keyword
/// (SetFontSize, SetTextColor, PlayAlertSound, MinimapIcon, PlayEffect, ItemLevel …) to a value, or removes it with null.
/// <see cref="Items"/> is the rule's whole tier list when the player moved items in or out of it.
/// </summary>
public sealed class RuleChange
{
    public string? Visibility { get; set; }
    public Dictionary<string, string?> Lines { get; set; } = [];
    public List<string>? Items { get; set; }

    [JsonIgnore] public bool IsEmpty => Visibility is null && Lines.Count == 0 && Items is null;
}

/// <summary>
/// A player's filter: NeverSink's filter at one strictness and the changes made to it, keyed by FilterBlade's rule names.
/// Only changes are kept, so the same customization applies to the base filter at any strictness.
/// </summary>
public sealed class FilterCustomization
{
    public string Name { get; set; } = "My filter";
    public string Strictness { get; set; } = "1-REGULAR";
    public Dictionary<string, RuleChange> Rules { get; set; } = [];

    /// <summary>Every beam only when the item drops. A beam that stays also keeps a quiet looping sound near the item
    /// until it is picked up (the client's ItemFilterEffect loop); a temporary one has neither.</summary>
    public bool TemporaryBeams { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FilterCustomization FromJson(string json) => JsonSerializer.Deserialize<FilterCustomization>(json, Json) ?? new();

    public RuleChange For(string id) => Rules.TryGetValue(id, out var change) ? change : Rules[id] = new RuleChange();

    /// <summary>
    /// Applies the changes to <paramref name="filter"/> (in place) through the rules of the option file. A change to a rule the
    /// filter does not have is skipped and named in the result. A tier list emptied by the player disables its rule: a rule
    /// without its BaseType condition would match every item.
    /// </summary>
    public List<string> ApplyTo(FilterDocument filter, IEnumerable<OptionRule> rules)
    {
        var skipped = new List<string>();
        var byId = rules.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var blocks = filter.ByKey();
        foreach (var (id, change) in Rules)
        {
            if (change.IsEmpty) continue;
            if (!byId.TryGetValue(id, out var rule) || !blocks.TryGetValue(rule.Key, out var block)) { skipped.Add(id); continue; }
            foreach (var (keyword, value) in change.Lines) block.Set(keyword, value);
            if (change.Items is { } items)
            {
                string op = rule.TierFlags.Contains('O') ? "==" : FilterSyntax.List(block.Find(rule.TierStat)?.Value ?? "").Operator;
                block.Set(rule.TierStat, items.Count > 0 ? FilterSyntax.Quote(op, items) : null);
            }
            switch (change.Visibility)
            {
                case "Show": block.Action = "Show"; block.Disabled = false; break;
                case "Hide": block.Action = "Hide"; block.Disabled = false; break;
                case "Disable": block.Disabled = true; break;
            }
            if (change.Items is { Count: 0 }) block.Disabled = true;
        }
        if (TemporaryBeams)
            foreach (var block in filter.Blocks)
                if (block.Find("PlayEffect") is { } beam && !beam.Value.Contains("Temp", StringComparison.OrdinalIgnoreCase))
                    block.Set("PlayEffect", beam.Value.Trim() + " Temp");
        return skipped;
    }
}

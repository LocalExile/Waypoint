using System.Text.RegularExpressions;

namespace LocalExile.Client.Waypoint;

/// <summary>Severity level for validation and lint diagnostics.</summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>A structured validation or lint finding.</summary>
public sealed record ValidationDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null);

/// <summary>
/// Static linter for Path of Exile 3.29 <c>.ui</c> layout files, preventing the parser exceptions and
/// layout infinite loops discovered in <c>07-spike-results.md</c>.
/// </summary>
public static partial class UiLayoutLinter
{
    /// <summary>
    /// Verified 47 built-in control types registered in the 3.29.3d client (<c>samples/registered-controls.txt</c>).
    /// Both qualified names (<c>Standard::Button</c>) and short names (<c>Button</c>) are accepted by the parser.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Full qualified names from samples/registered-controls.txt
        "InGame::Ancestral::AncestralTrialRespawnTotemHealthDisplay",
        "InGame::BuffDisplay",
        "InGame::Clock",
        "InGame::ExertedAttackDisplay",
        "InGame::ExperienceBar",
        "InGame::FlaskBar",
        "InGame::FlaskBar_Console",
        "InGame::MapView",
        "InGame::MicrotransactionContextMenu",
        "InGame::MinionDisplay",
        "InGame::MOTDDisplay",
        "InGame::Orb",
        "InGame::PbrPanel",
        "InGame::PlayerShield",
        "InGame::SigilDisplay",
        "InGame::SkillBinder_Console",
        "InGame::SkillsBar",
        "InGame::SkillsBar_Console",
        "InGame::SkillsList",
        "InGame::TrinitySupportHudDisplay",
        "InGame::TutorialArticle",
        "InGame::UnholyTrinitySupportHudDisplay",
        "Layout::FlowLayout",
        "Layout::StackLayout",
        "SkillControls::SkillIcon",
        "Standard::BasicButton",
        "Standard::Button",
        "Standard::CheckBox",
        "Standard::CollapsableFrame",
        "Standard::ComboBox",
        "Standard::ContextMenu",
        "Standard::ControlListBox",
        "Standard::CreditsPicture",
        "Standard::Frame",
        "Standard::GreenButton",
        "Standard::IconLabelButton",
        "Standard::ImageButton",
        "Standard::Label",
        "Standard::Picture",
        "Standard::Popup",
        "Standard::ProgressBar",
        "Standard::ScalableTextLabel",
        "Standard::ScrollableFrame",
        "Standard::TabContainer",
        "Standard::Table",
        "Standard::TextBox",
        "Standard::VideoDisplay",

        // Unqualified short names as used in .ui files
        "AncestralTrialRespawnTotemHealthDisplay",
        "BuffDisplay",
        "Clock",
        "ExertedAttackDisplay",
        "ExperienceBar",
        "FlaskBar",
        "FlaskBar_Console",
        "MapView",
        "MicrotransactionContextMenu",
        "MinionDisplay",
        "MOTDDisplay",
        "Orb",
        "PbrPanel",
        "PlayerShield",
        "SigilDisplay",
        "SkillBinder_Console",
        "SkillsBar",
        "SkillsBar_Console",
        "SkillsList",
        "TrinitySupportHudDisplay",
        "TutorialArticle",
        "UnholyTrinitySupportHudDisplay",
        "FlowLayout",
        "StackLayout",
        "SkillIcon",
        "BasicButton",
        "Button",
        "CheckBox",
        "CollapsableFrame",
        "ComboBox",
        "ContextMenu",
        "ControlListBox",
        "CreditsPicture",
        "Frame",
        "GreenButton",
        "IconLabelButton",
        "ImageButton",
        "Label",
        "Picture",
        "Popup",
        "ProgressBar",
        "ScalableTextLabel",
        "ScrollableFrame",
        "TabContainer",
        "Table",
        "TextBox",
        "VideoDisplay"
    };

    /// <summary>
    /// Known layout properties seen across stock 3.29 <c>.ui</c> files and spikes.
    /// Unknown properties cause the game's parser to throw <c>error: ":" expected</c> and exit the client.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "id", "aspect_mode", "size", "width", "height", "min_size", "max_size",
        "x", "y", "location", "float", "vertical_float", "align", "vertical_align",
        "visible", "enabled", "mouse_enabled", "keyboard_enabled", "propagate_properties",
        "auto_size", "margin", "padding", "spacing", "orientation", "direction",
        "border", "border_position", "pixel_border_colour", "background", "background_colour",
        "foreground_colour", "colour", "color", "alpha", "opacity",
        "font", "text", "text_align", "text_side", "word_wrap", "multiline", "max_length",
        "caret_colour", "selection_colour", "placeholder", "password",
        "source", "image", "hover_image", "pressed_image", "disabled_image", "checked_image",
        "progress", "min", "max", "value", "fill_colour", "bar_colour",
        "checked", "selected", "tooltip", "clip_children", "z_order", "layer",
        "stretch", "tile", "scale", "icon", "scroll_vertical", "scroll_horizontal",
        "highlight_colour", "disabled_colour", "shadow_colour", "outline_colour"
    };

    [GeneratedRegex(@"\Abegin\s+(?<type>[A-Za-z0-9_:]+)(?:\s+based\s+on\s+(?<base>[A-Za-z0-9_.:-]+))?\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex BeginBlockRegex();

    [GeneratedRegex(@"\Aconst\s+\$(?<name>[A-Za-z0-9_]+)\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex ConstDeclRegex();

    [GeneratedRegex(@"\A(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<val>.*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyAssignmentRegex();

    private sealed class ControlFrame
    {
        public required string TypeName { get; init; }
        public required int StartLine { get; init; }
        public string? Id { get; set; }
        public HashSet<string> ChildIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasMargin { get; set; }
        public int? MarginLine { get; set; }
    }

    /// <summary>
    /// Lints a <c>.ui</c> layout text and returns all findings.
    /// </summary>
    /// <param name="content">UTF-8 or decoded UTF-16 text of the <c>.ui</c> file.</param>
    /// <param name="filePath">Optional file path for diagnostic attribution.</param>
    /// <param name="customControls">Optional custom UI control type names registered by the mod.</param>
    /// <param name="knownModAssets">Optional set of virtual paths shipped in the mod's payload (lower-case, forward-slash).</param>
    public static IReadOnlyList<ValidationDiagnostic> Lint(
        string content,
        string? filePath = null,
        IEnumerable<string>? customControls = null,
        IReadOnlySet<string>? knownModAssets = null)
    {
        var diagnostics = new List<ValidationDiagnostic>();
        if (string.IsNullOrWhiteSpace(content))
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "UI001", "UI layout file is empty.", filePath, 1));
            return diagnostics;
        }

        // Strip UTF-8 / UTF-16 BOM if caller passed raw decoded text
        string normalized = content.TrimStart('\uFEFF');

        // Pitfall from 07-spike-results.md:
        // "Syntax error. You must have a space before <DEFINITIONS>" if <DEFINITIONS> starts at byte 0.
        if (normalized.StartsWith("<DEFINITIONS>", StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "UI002",
                "File starts directly with '<DEFINITIONS>' without leading whitespace, comment, or blank line. " +
                "The 3.29 parser throws 'Syntax error. You must have a space before <DEFINITIONS>' and closes the client.",
                filePath,
                1));
        }

        var allowedControls = new HashSet<string>(KnownControlTypes, StringComparer.OrdinalIgnoreCase);
        if (customControls is not null)
        {
            foreach (string c in customControls)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    allowedControls.Add(c.Trim());
            }
        }

        string[] lines = normalized.Replace("\r\n", "\n").Split('\n');
        bool sawDefinitions = false;
        bool inMultilineProperty = false;
        var stack = new Stack<ControlFrame>();
        var topLevelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allDefinedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int topLevelControlCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            string rawLine = lines[i];
            string stripped = StripLineComment(rawLine).Trim();

            if (stripped.Length == 0)
                continue;

            if (stripped.Equals("<DEFINITIONS>", StringComparison.Ordinal))
            {
                if (sawDefinitions)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "UI003", "Duplicate <DEFINITIONS> header.", filePath, lineNo));
                }
                sawDefinitions = true;
                continue;
            }

            if (!sawDefinitions)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "UI004", $"Unexpected content before <DEFINITIONS>: '{stripped}'.", filePath, lineNo));
                continue;
            }

            if (inMultilineProperty)
            {
                if (stripped.EndsWith(';'))
                    inMultilineProperty = false;
                continue;
            }

            if (ConstDeclRegex().IsMatch(stripped))
            {
                if (!stripped.EndsWith(';'))
                    diagnostics.Add(new(DiagnosticSeverity.Error, "UI011", "const declaration must end with ';'.", filePath, lineNo));
                continue;
            }

            if (stripped.StartsWith("begin ", StringComparison.Ordinal))
            {
                var match = BeginBlockRegex().Match(stripped);
                if (!match.Success)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "UI005", $"Malformed 'begin' statement: '{stripped}'.", filePath, lineNo));
                    continue;
                }

                string controlType = match.Groups["type"].Value;
                if (!allowedControls.Contains(controlType))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "UI006",
                        $"Unknown UI control type '{controlType}'. Declare custom controls in nativeClient.customControls or use a registered 3.29 control.",
                        filePath,
                        lineNo));
                }

                if (match.Groups["base"].Success)
                {
                    string baseId = match.Groups["base"].Value;
                    if (!allDefinedIds.Contains(baseId))
                    {
                        diagnostics.Add(new(
                            DiagnosticSeverity.Warning,
                            "UI012",
                            $"'based on {baseId}' references an id not yet declared earlier in this file.",
                            filePath,
                            lineNo));
                    }
                }

                if (stack.Count == 0)
                    topLevelControlCount++;

                stack.Push(new ControlFrame { TypeName = controlType, StartLine = lineNo });
                continue;
            }

            if (stripped.Equals("end", StringComparison.Ordinal))
            {
                if (stack.Count == 0)
                {
                    diagnostics.Add(new(DiagnosticSeverity.Error, "UI007", "Unexpected 'end' without matching 'begin'.", filePath, lineNo));
                    continue;
                }

                var completed = stack.Pop();

                // Known hang hazard from 07-spike-results.md:
                // "A Picture with a margin inside a StackLayout froze the main thread at 100% CPU."
                if (stack.Count > 0 &&
                    IsShortControlName(completed.TypeName, "Picture") &&
                    IsShortControlName(stack.Peek().TypeName, "StackLayout") &&
                    completed.HasMargin)
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "UI008",
                        "Known 3.29 infinite-layout-loop hazard: a 'Picture' with 'margin' directly inside a 'StackLayout' freezes the client at 100% CPU. Use a spacer Frame instead.",
                        filePath,
                        completed.MarginLine ?? completed.StartLine));
                }

                continue;
            }

            if (stack.Count == 0)
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "UI009", $"Unexpected statement outside any control block: '{stripped}'.", filePath, lineNo));
                continue;
            }

            var propMatch = PropertyAssignmentRegex().Match(stripped);
            if (!propMatch.Success)
            {
                diagnostics.Add(new(
                    DiagnosticSeverity.Error,
                    "UI010",
                    $"Invalid property syntax '{stripped}'. Missing '=' causes the 3.29 parser to throw 'error: \":\" expected'.",
                    filePath,
                    lineNo));
                continue;
            }

            string propName = propMatch.Groups["prop"].Value;
            string propVal = propMatch.Groups["val"].Value.Trim();

            if (!KnownProperties.Contains(propName))
            {
                // Custom controls may introduce custom properties; downgrade to Warning for custom controls, Error for stock controls
                bool isCustomControl = !KnownControlTypes.Contains(stack.Peek().TypeName);
                diagnostics.Add(new(
                    isCustomControl ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    "UI013",
                    $"Unknown property '{propName}' on control '{stack.Peek().TypeName}'. Unknown properties on stock controls throw 'error: \":\" expected' in 3.29.",
                    filePath,
                    lineNo));
            }

            if (!stripped.EndsWith(';'))
            {
                if (propName.Equals("margin", StringComparison.OrdinalIgnoreCase))
                {
                    stack.Peek().HasMargin = true;
                    stack.Peek().MarginLine = lineNo;
                }
                inMultilineProperty = true;
            }
            else
            {
                string valWithoutSemi = propVal.TrimEnd(';').Trim();
                HandleCompletedProperty(stack, topLevelIds, allDefinedIds, propName, valWithoutSemi, filePath, lineNo, knownModAssets, diagnostics);
            }
        }

        if (!sawDefinitions)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, "UI004", "Missing required '<DEFINITIONS>' header.", filePath, 1));
        }

        while (stack.Count > 0)
        {
            var unclosed = stack.Pop();
            diagnostics.Add(new(
                DiagnosticSeverity.Error,
                "UI007",
                $"Unclosed 'begin {unclosed.TypeName}' block (opened on line {unclosed.StartLine}) is missing 'end'.",
                filePath,
                unclosed.StartLine));
        }

        if (sawDefinitions && topLevelControlCount == 0)
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "UI014", "UI file defines no top-level controls.", filePath, 1));
        }

        return diagnostics;
    }

    private static void HandleCompletedProperty(
        Stack<ControlFrame> stack,
        HashSet<string> topLevelIds,
        HashSet<string> allDefinedIds,
        string propName,
        string value,
        string? filePath,
        int lineNo,
        IReadOnlySet<string>? knownModAssets,
        List<ValidationDiagnostic> diagnostics)
    {
        var current = stack.Peek();

        if (propName.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            string id = value.Trim('"');
            if (string.IsNullOrWhiteSpace(id))
            {
                diagnostics.Add(new(DiagnosticSeverity.Error, "UI015", "Control 'id' cannot be empty.", filePath, lineNo));
                return;
            }

            current.Id = id;
            allDefinedIds.Add(id);

            // Check sibling uniqueness (07-spike-results.md: duplicate sibling id throws uncaught C++ exception!)
            if (stack.Count == 1)
            {
                if (!topLevelIds.Add(id))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "UI016",
                        $"Duplicate top-level control id '{id}'. Duplicate sibling IDs throw an uncaught parser exception and close the 3.29 client.",
                        filePath,
                        lineNo));
                }
            }
            else
            {
                // Peek parent (second element on stack)
                var parent = stack.Skip(1).First();
                if (!parent.ChildIds.Add(id))
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Error,
                        "UI016",
                        $"Duplicate sibling control id '{id}' under parent '{parent.Id ?? parent.TypeName}'. Duplicate sibling IDs crash the 3.29 client.",
                        filePath,
                        lineNo));
                }
            }
        }
        else if (propName.Equals("margin", StringComparison.OrdinalIgnoreCase))
        {
            current.HasMargin = true;
            current.MarginLine = lineNo;
        }
        else if (knownModAssets is not null &&
                 (propName.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                  propName.Equals("image", StringComparison.OrdinalIgnoreCase)))
        {
            string assetPath = value.Trim('"').Replace('\\', '/');
            if (assetPath.Contains("/mods/", StringComparison.OrdinalIgnoreCase) ||
                assetPath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            {
                string lower = assetPath.ToLowerInvariant();
                bool found = knownModAssets.Contains(lower) ||
                             knownModAssets.Contains(lower + ".dds") ||
                             knownModAssets.Contains(lower + ".png");
                if (!found)
                {
                    diagnostics.Add(new(
                        DiagnosticSeverity.Warning,
                        "UI017",
                        $"Referenced mod asset '{assetPath}' was not found in the mod's payload directory.",
                        filePath,
                        lineNo));
                }
            }
        }
    }

    private static bool IsShortControlName(string controlType, string expectedShort)
    {
        int sep = controlType.LastIndexOf("::", StringComparison.Ordinal);
        string shortName = sep >= 0 ? controlType[(sep + 2)..] : controlType;
        return shortName.Equals(expectedShort, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripLineComment(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inQuotes = !inQuotes;
            else if (!inQuotes && line[i] == '/' && line[i + 1] == '/')
                return line[..i];
        }
        return line;
    }
}

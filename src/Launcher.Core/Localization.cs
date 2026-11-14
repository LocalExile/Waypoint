using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

[assembly: NeutralResourcesLanguage("en")]

namespace Launcher.Core;

/// <summary>An interface language: its BCP 47 tag, which names its catalog (Strings.&lt;tag&gt;.resx) and is what the settings
/// keep, and its name in itself, so that it reads the same in every language.</summary>
public sealed record InterfaceLanguage(string Tag, string Name);

/// <summary>Presentation text only. Protocol tokens, persisted data and raw diagnostics stay unchanged.</summary>
public sealed class L : INotifyPropertyChanged
{
    static readonly ResourceManager Resources = new("Launcher.Core.Resources.Strings", typeof(L).Assembly);
    /// <summary>Windows' display language (on Linux, the locale), read before Waypoint sets its own.</summary>
    static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    /// <summary>Every interface language, English (the source text) first. Another is a catalog beside the others and a line
    /// here (docs/localization.md).</summary>
    public static IReadOnlyList<InterfaceLanguage> Languages { get; } =
    [
        new("en", "English"),
        new("ru", "Русский"),
        new("zh-CN", "简体中文"),
    ];
    public static L Current { get; } = new();
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    /// <summary>The shown language's tag, one of <see cref="Languages"/>.</summary>
    public string Language { get; private set; } = "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Resources.GetString(key, Culture) ?? key;
    public static string ResourceKey(string source) => "Text_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))))[..12];
    public static string Text(string source, params object?[] arguments)
    {
        string format = Resources.GetString(ResourceKey(source), Current.Culture) ?? source;
        return arguments.Length == 0 ? format : string.Format(Current.Culture, format, arguments);
    }
    // These are presentation wrappers around the adapter's stable English contract. Captured
    // paths, process IDs, mod IDs and raw compiler/runtime diagnostics are never rewritten.
    static readonly (string Pattern, string Format)[] Descriptions =
    [
        (@"\ALocal runtime uses (.+)\z", "Local runtime uses {0}"),
        (@"\ANeeds: (.+)\z", "Needs: {0}"),
        (@"\ALinked to (.+)\z", "Linked to {0}"),
        (@"\AClient links to (.+), which has no client\. Run the setup guide to replace the link\.\z", "Client links to {0}, which has no client. Run the setup guide to replace the link."),
        (@"\AClient is a link to (.+), and other copies of LocalExile may share that folder, so Waypoint will not change it\. Prepare it there, or run the setup guide to put a client here\.\z",
            "Client is a link to {0}, and other copies of LocalExile may share that folder, so Waypoint will not change it. Prepare it there, or run the setup guide to put a client here."),
        (@"\A(.+) \| hot reload watcher PID (\d+)\z", "{0} | hot reload watcher PID {1}"),
        (@"\A(.+) · Checked (.+)\z", "{0} · Checked {1}"),
        (@"\AMultiple (Patch|Gateway|Auth) processes belong to this checkout\.\z", "Multiple {0} processes belong to this checkout."),
        (@"\APort (\d+) is used by another program \(PID (\d+)\)\. (Patch|Gateway|Auth) needs this port\.\z", "Port {0} is used by another program (PID {1}). {2} needs this port."),
        (@"\APort (\d+) is used by (.+) \(PID (\d+)\)\. (Patch|Gateway|Auth) needs this port\.\z", "Port {0} is used by {1} (PID {2}). {3} needs this port."),
        (@"\AAnother server or watcher is running \(PID (\d+)\)\. Stop it from its own installation before launching here\.\z", "Another server or watcher is running (PID {0}). Stop it from its own installation before launching here.")
    ];
    public static string Describe(string source)
    {
        foreach (var (pattern, format) in Descriptions)
        {
            var match = Regex.Match(source, pattern, RegexOptions.CultureInvariant);
            if (match.Success) return Text(format, match.Groups.Cast<Group>().Skip(1).Select((g, i) =>
                (object)(i == 0 && (format == "{0} | hot reload watcher PID {1}" || format == "{0} · Checked {1}")
                    ? Text(g.Value) : g.Value)).ToArray());
        }
        return Text(source);
    }
    /// <summary>The interface language a tag asks for, matched as .NET finds a culture's resources: the tag itself in any
    /// case ("zh-cn"), or a regional form of a language with the same script ("ru-RU" → ru, "en-GB" → en, "zh-SG" → zh-CN).
    /// Traditional Chinese is not Simplified Chinese, so "zh-TW" has none. Null when Waypoint doesn't have it.</summary>
    public static string? Supported(string? tag)
    {
        CultureInfo asked;
        try { asked = CultureInfo.GetCultureInfo(tag?.Trim() ?? ""); }
        catch (CultureNotFoundException) { return null; }
        for (var culture = asked; culture.Name.Length > 0; culture = culture.Parent)
            foreach (var language in Languages)
            {
                var own = CultureInfo.GetCultureInfo(language.Tag);
                // zh-CN's parent is zh-Hans, the script; zh-Hans's is zh, the language of both scripts.
                if (own.Name == culture.Name || own.Parent.Name == culture.Name && own.Parent.Parent.Name.Length > 0) return language.Tag;
            }
        return null;
    }

    /// <summary>The language to show for a saved choice: nothing saved follows the system's language when Waypoint has it,
    /// and anything Waypoint doesn't have is English.</summary>
    public static string Choose(string? saved, string system) =>
        string.IsNullOrWhiteSpace(saved) ? Supported(system) ?? "en" : Supported(saved) ?? "en";

    /// <summary>Shows the interface in a saved language (<see cref="Choose"/>, with <see cref="SystemCulture"/>). The UI
    /// culture follows, so the toolkits' own text (a text box's menu) matches; number formats and the culture commands are
    /// written in stay as they are.</summary>
    public void SetLanguage(string? language)
    {
        string selected = Choose(language, SystemCulture.Name);
        Culture = CultureInfo.GetCultureInfo(selected);
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = Culture;
        if (Language == selected) return;
        Language = selected;
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
}

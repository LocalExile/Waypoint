using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

[assembly: NeutralResourcesLanguage("en")]

namespace Launcher.Core;

/// <summary>Presentation text only. Protocol tokens, persisted data and raw diagnostics stay unchanged.</summary>
public sealed class L : INotifyPropertyChanged
{
    static readonly ResourceManager Resources = new("Launcher.Core.Resources.Strings", typeof(L).Assembly);
    public static L Current { get; } = new();
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public string Language => Culture.TwoLetterISOLanguageName;
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
        (@"\A(.+) \| hot reload watcher PID (\d+)\z", "{0} | hot reload watcher PID {1}"),
        (@"\A(.+) · Checked (.+)\z", "{0} · Checked {1}"),
        (@"\AMultiple (Patch|Gateway|Auth) processes belong to this checkout\.\z", "Multiple {0} processes belong to this checkout."),
        (@"\APort (\d+) is occupied by another process \(PID (\d+)\); (Patch|Gateway|Auth) is not owned by this checkout\.\z", "Port {0} is occupied by another process (PID {1}); {2} is not owned by this checkout."),
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
    public void SetLanguage(string? language)
    {
        string selected = language == "ru" ? "ru" : "en";
        if (Language == selected) return;
        Culture = CultureInfo.GetCultureInfo(selected);
        PropertyChanged?.Invoke(this, new("Item[]"));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
}

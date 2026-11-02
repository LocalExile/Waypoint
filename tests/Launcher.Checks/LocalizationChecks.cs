using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Launcher.Core;

static class LocalizationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var manager = new ResourceManager("Launcher.Core.Resources.Strings", typeof(L).Assembly);
        var english = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var russian = manager.GetResourceSet(CultureInfo.GetCultureInfo("ru"), true, false)!;
        var originals = english.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var translated = russian.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        check(originals.Count >= 400 && originals.Keys.Order().SequenceEqual(translated.Keys.Order()), "Russian satellite contains every English resource without relying on fallback");
        foreach (var (key, source) in originals)
        {
            if (key != L.ResourceKey(source) || string.IsNullOrWhiteSpace(translated[key])) throw new Exception("Invalid translation resource: " + source);
            if (CompositeFormat.Parse(source).MinimumArgumentCount != CompositeFormat.Parse(translated[key]).MinimumArgumentCount)
                throw new Exception("Translation placeholders differ: " + source);
            static IEnumerable<string> Slots(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?=[,:}])").Select(m => m.Groups[1].Value).Order();
            if (!Slots(source).SequenceEqual(Slots(translated[key]))) throw new Exception("Translation placeholder slots differ: " + source);
        }
        check(true, "translation identities, nonempty values and format placeholders are valid");
        try
        {
            L.Current.SetLanguage("ru");
            check(L.Text("Play") == "Играть" && L.Text("Setup checks · {0} need attention", 3).Contains('3'), "Russian resources load and format actual interface text");
            string technical = "main /mod_enable hello F:\\repo\\Client 0123456";
            check(L.Text(technical) == technical, "unknown repository, command, path and diagnostic text is preserved verbatim");
            check(L.Describe("Needs: Play") == "Требуется: Play" && L.Describe("Local runtime uses C:\\Play\\saves.db").EndsWith(@"C:\Play\saves.db"), "captured mod IDs and paths are never translated even when they match an interface label");
            var step = SetupGuidance.Describe(new("Original client", false, "Ready"), @"C:\Russian check\LocalExile");
            check(step.Name == "Original client" && step.Action == "ClientFolder" && step.DisplayName == "Исходный клиент" && step.Instructions.Contains(Path.Combine(@"C:\Russian check\LocalExile", "Client")), "Russian setup guidance preserves action routing and the physical copy destination");
            var mod = new ModEntry("hello", "1.0", "script", [], true, "Not checked", "", "", "", "");
            check(mod.Status == "Not checked" && mod.Summary.Contains("Не проверен"), "Russian mod summaries retain canonical runtime status");
            L.Current.SetLanguage("unsupported");
            check(L.Current.Language == "en" && L.Text("Play") == "Play", "unsupported language preferences fall back safely to English");
        }
        finally { L.Current.SetLanguage("en"); }
    }
}

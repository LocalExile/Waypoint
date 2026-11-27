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
        static Dictionary<string, string> Entries(ResourceSet set) => set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var originals = Entries(manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!);
        check(originals.Count >= 400 && originals.All(e => e.Key == L.ResourceKey(e.Value)), "English resource keys are the identities of their sources");
        var tags = L.Languages.Select(l => l.Tag).ToArray();
        check(tags[0] == "en" && tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tags.Length
            && tags.All(t => CultureInfo.GetCultureInfo(t).Name == t) && L.Languages.All(l => l.Name.Length > 0),
            "interface languages are distinct canonical tags, English first, each with its own name");
        var built = Directory.GetDirectories(AppContext.BaseDirectory)
            .Where(d => File.Exists(Path.Combine(d, "Launcher.Core.resources.dll"))).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal);
        check(built.SequenceEqual(tags.Skip(1).Order(StringComparer.Ordinal)), "a satellite assembly is built for every interface language and for no other");
        var catalogs = new Dictionary<string, Dictionary<string, string>>();
        foreach (string tag in tags.Skip(1))
        {
            var set = manager.GetResourceSet(CultureInfo.GetCultureInfo(tag), true, false);
            var translated = catalogs[tag] = set is null ? [] : Entries(set);
            check(originals.Keys.Order().SequenceEqual(translated.Keys.Order()), $"{tag} satellite contains every English resource without relying on fallback");
            foreach (var (key, source) in originals)
            {
                if (string.IsNullOrWhiteSpace(translated.GetValueOrDefault(key))) throw new Exception($"Invalid {tag} translation resource: " + source);
                if (CompositeFormat.Parse(source).MinimumArgumentCount != CompositeFormat.Parse(translated[key]).MinimumArgumentCount)
                    throw new Exception($"{tag} translation placeholders differ: " + source);
                static IEnumerable<string> Slots(string text) => Regex.Matches(text, @"(?<!\{)\{(\d+)(?=[,:}])").Select(m => m.Groups[1].Value).Order();
                if (!Slots(source).SequenceEqual(Slots(translated[key]))) throw new Exception($"{tag} translation placeholder slots differ: " + source);
            }
            check(true, $"{tag} translation identities, nonempty values and format placeholders are valid");
        }

        check(L.Supported("zh-cn") == "zh-CN" && L.Supported("zh-SG") == "zh-CN" && L.Supported("zh-Hans") == "zh-CN" && L.Supported("zh-TW") is null
            && L.Supported("ru-RU") == "ru" && L.Supported("en-GB") == "en" && L.Supported("fr-FR") is null && L.Supported("unsupported") is null && L.Supported(null) is null,
            "a tag matches its language and its regional forms of the same script, and nothing else");
        check(L.Choose("", "ru-RU") == "ru" && L.Choose(null, "zh-CN") == "zh-CN" && L.Choose("", "de-DE") == "en" && L.Choose("en", "ru-RU") == "en"
            && L.Choose("zh-CN", "en-US") == "zh-CN" && L.Choose("unsupported", "ru-RU") == "en",
            "nothing saved follows the system's language when there is a catalog for it; a saved language wins; an unknown one is English");
        try
        {
            foreach (string tag in tags.Skip(1))
            {
                var catalog = catalogs[tag];
                string Translated(string source) => catalog[L.ResourceKey(source)];
                L.Current.SetLanguage(tag);
                check(L.Current.Language == tag && L.Current.Culture.Name == tag && CultureInfo.CurrentUICulture.Name == tag, $"{tag} is shown, and the UI culture follows it");
                check(L.Text("Play") == Translated("Play") && L.Text("Play") != "Play" && L.Text("Setup checks · {0} need attention", 3).Contains('3'),
                    $"{tag} resources load and format actual interface text");
                string technical = "main /mod_enable hello F:\\repo\\Client 0123456";
                check(L.Text(technical) == technical, $"{tag}: unknown repository, command, path and diagnostic text is preserved verbatim");
                check(L.Describe("Needs: Play") == string.Format(L.Current.Culture, Translated("Needs: {0}"), "Play") && L.Describe("Local runtime uses C:\\Play\\saves.db").EndsWith(@"C:\Play\saves.db"),
                    $"{tag}: captured mod IDs and paths are never translated even when they match an interface label");
                check(L.Describe("Port 8080 is used by steamwebhelper.exe (PID 29304). Patch needs this port.") == string.Format(L.Current.Culture, Translated("Port {0} is used by {1} (PID {2}). {3} needs this port."), "8080", "steamwebhelper.exe", "29304", "Patch")
                    && L.Describe("Port 8080 is used by another program (PID 29304). Patch needs this port.") == string.Format(L.Current.Culture, Translated("Port {0} is used by another program (PID {1}). {2} needs this port."), "8080", "29304", "Patch"),
                    $"{tag}: a port conflict is translated around the program's name, PID and server, which stay as they are");
                var step = SetupGuidance.Describe(new("Original client", false, "Ready"), @"C:\Language check\LocalExile");
                check(step.Name == "Original client" && step.Action == "ClientFolder" && step.DisplayName == Translated("Original client") && step.Instructions.Contains(Path.Combine(@"C:\Language check\LocalExile", "Client")),
                    $"{tag} setup guidance preserves action routing and the physical copy destination");
                var mod = new ModEntry("hello", "1.0", "script", [], true, "Not checked", "", "", "", "");
                check(mod.Status == "Not checked" && mod.Summary.Contains(Translated("Not checked")), $"{tag} mod summaries retain canonical runtime status");
                var idols = mod with { Id = "idols", Name = "Idols", Description = "Legacy of Phrecia's Idols: items with map modifiers that affect every map you open while they lie in the Idol inventory." };
                check(idols.Title == "Idols" && idols.Id == "idols" && idols.DisplayTitle == Translated(idols.Name)
                    && idols.DisplayTitle != idols.Title && idols.DisplayDescription == Translated(idols.Description)
                    && idols.DisplayDescription != idols.Description && L.Text("Keep the Atlas passive tree") != "Keep the Atlas passive tree",
                    $"{tag}: a listed official mod has localized display text and settings while retaining its manifest identity");
            }
            L.Current.SetLanguage("zh-CN");
            var exchange = new GameSystem("CurrencyExchange", "Core/NPC/CurrencyExchange", "CurrencyExchange", "", [], "");
            check(exchange.DisplayTitle == "通货交易所" && exchange.DisplayDescription == "通货兑换与交易订单。"
                && exchange.SearchText.Contains("通货兑换") && exchange.SearchText.Contains("Currency Exchange")
                && exchange.SearchText.Contains(exchange.Path) && exchange.ToString() == "CurrencyExchange",
                "module playback uses Simplified Chinese resources and search retains the original name and path");
            var custom = exchange with { Name = "My local market", Description = "My custom description" };
            check(custom.DisplayTitle == custom.Name && custom.DisplayDescription == custom.Description,
                "custom module metadata falls back to the manifest if no translation exists");
            check(L.Text("Legacy hideout source") == "旧藏身处来源角色"
                && L.Text("Evolution local decline coefficient") == "进化圣坛区域衰减系数"
                && L.Text("Boss life on player death") == "玩家死亡时首领回复生命",
                "module setting labels use the Simplified Chinese satellite");
            L.Current.SetLanguage("ru");
            check(exchange.DisplayTitle == "Обмен валюты" && exchange.DisplayDescription.Length > 0,
                "module titles switch language without reloading the manifest");
            check(L.Text("Play") == "Играть" && L.Describe("Needs: Play") == "Требуется: Play" && SetupGuidance.Describe(new("Original client", false, "Ready"), @"C:\Russian check").DisplayName == "Исходный клиент",
                "Russian text is the reviewed text");
            L.Current.SetLanguage("unsupported");
            check(L.Current.Language == "en" && L.Text("Play") == "Play" && CultureInfo.CurrentUICulture.Name == "en", "unsupported language preferences fall back safely to English");
        }
        finally { L.Current.SetLanguage("en"); }
    }
}

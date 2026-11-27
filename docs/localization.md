# Interface localization

Waypoint's interface is in English (`en`, the source text), Russian (`ru`) and Simplified Chinese (`zh-CN`). **Settings →
Language** changes it at once and saves the choice in Waypoint's settings. Until a player picks one, Waypoint follows the
system's display language (Windows', or the locale on Linux) when it has that language, and English otherwise; a regional form
of a language counts (`ru-RU` → `ru`, `zh-SG` → `zh-CN`), but another script does not (`zh-TW` gets English). Each language is
listed under its own name, so it reads the same whichever language is shown. Existing settings need no migration: `en` and
`ru` mean what they did, and a value Waypoint doesn't have is English. This setting does not select the game's language.

Coverage includes navigation, page controls, setup instructions, mod summaries, backup history and confirmations, operation
stages, tooltips and application-provided accessibility names. Technical identifiers, repository/PR titles, paths, hashes,
commands and raw diagnostic/log output keep their original text. Native Windows dialogs and system window menus follow the
Windows display language.

The official mod catalog's titles, summaries and settings (including their descriptions, units and choices) use the same
resources in Browse, My Mods and each mod's page. The saved setting names and choice values are the ones in the mod's schema.

## Implementation

The languages are one list, `L.Languages` (`src/Launcher.Core/Localization.cs`): a BCP 47 tag and the language's own name.
The tag names the catalog, `src/Launcher.Core/Resources/Strings.<tag>.resx`, which the .NET SDK compiles into a satellite
assembly beside the neutral English resources of `Strings.resx`; the published single file carries them all. No runtime
download, translation service or additional NuGet package is used. Both Settings sections build their language choices from
the list, and `L.Supported` matches a tag to it the way .NET looks for a culture's resources.

`L.Text("English source", arguments...)` resolves a resource with English fallback and formats values using the selected
UI culture. `L.Describe` translates known adapter presentation templates while preserving captured identifiers. Canonical
model/action/status values and persisted runtime/backup metadata remain unchanged. Selecting a language sets the UI culture
(`CultureInfo.CurrentUICulture`), so the toolkits' own text, such as a text box's menu, follows it; the number culture and the
culture commands are written in are unchanged. WPF's `Language` follows the selected language, which also picks the right
glyphs for Chinese text.

XAML uses `{l:Loc 'English source'}` (`src/Waypoint/Loc.cs`, and `Views/Loc.cs` on Linux), which updates when the selection
changes; an apostrophe in the source is written `\'`. Text written from code is refreshed by the section that writes it, on
`L.Current.PropertyChanged`; the selected section, runtime state and input fields are retained. Dialog confirmations support
Escape in every language.

## Updating a translation

Use Python 3 from the repository root. Every catalog has an option of its own:

```powershell
python tools/update-translation.py --source "Play" --ru "Играть" --zh-CN "开始游戏"
```

A new English source needs a translation in every catalog; an existing one takes any of them. Multiline text can be supplied
with `--source-file <UTF-8 file>` and `--<tag>-file <UTF-8 file>`. `tools/add-translations.py` does the same for a UTF-8
JSON file of `{"English": {"ru": "…", "zh-CN": "…"}}`, or of `{"English": "…"}` with `--language <tag>`.
`python tools/missing-translations.py` lists every source used in `src/` (`.cs`, `.xaml`, `.axaml`) that a catalog lacks, and
every catalog that does not hold exactly the English catalog's sources; CI runs it. Keys are `Text_` plus the first 12
lowercase SHA-256 digits of the English UTF-8 text with LF line endings, so changing the English source creates a new key.
Preserve placeholders such as `{0}`, `{1:N0}` and `{0:g}`. Do not pass protocol tokens through translation before invoking
runtime operations.

## Words

Path of Exile's own words use the game's official text in that language: the game client holds its Russian and Traditional
Chinese strings (`Data/Russian`, `Data/Traditional Chinese`), and the Simplified Chinese catalog uses the Traditional Chinese
client's terms in simplified characters, which is what a player of the international client sees. For example a modifier is
«свойство» and 词缀, Lockstep and Predictive are «Синхронизация» / «Рассинхронизация» and 同步模式 / 预测模式, and the
login screen's field is «Эл. почта» and 电邮. A path through the game's own menus gives the translated names with the English
ones after them, since the game may run in another language than Waypoint.

Other technical terms are translated only where an accepted translation exists (git's and GitHub's own Chinese terms: 分支,
提交, 拉取请求, 复刻, 贮藏; Microsoft's 热重载). Product names stay in English, and so do the names of LocalExile's server
programs, Gateway, Auth, Patch and Dev host, as they appear in logs and process lists. In Chinese text, a half-width space
separates Chinese from Latin letters, digits and placeholders, and punctuation is full-width.

## Importing Simplified Chinese item data

On Windows, the item crafter's controls follow **Settings → Language**. To also display the game's item classes, bases,
currency, modifier names and descriptions, cluster themes and rare names in Simplified Chinese, generate the local
display catalog from your client. It uses the client's Simplified Chinese stat descriptions where available and converts
Traditional Chinese tables to simplified characters. A client patch with Simplified Chinese tables can supply those
instead. Waypoint reads no client archives itself and ships no client strings or tables with its binaries.

Use a LocalExile checkout with its [GgDump prerequisites](https://github.com/LocalExile/LocalExile/blob/main/Agents/docs/re/tooling.md#3-ggpk-file-dump-scriptsreggdump)
installed. Use the schema for your client version. From the LocalExile checkout root, set `$client` to your game directory
or `Content.ggpk`, and extract the following files to a local directory:

```powershell
$localExile = (Get-Location).Path
$export = Join-Path $env:TEMP 'waypoint-crafter-data'
New-Item -ItemType Directory -Force $export | Out-Null
dotnet run --project scripts/re/ggdump -- $client $export `
    'data/traditional chinese/baseitemtypes.datc64' 'data/traditional chinese/itemclasses.datc64' `
    'data/traditional chinese/mods.datc64' 'data/traditional chinese/words.datc64' `
    'data/traditional chinese/passiveskills.datc64' 'data/passiveskills.datc64' `
    'metadata/statdescriptions/stat_descriptions.txt'
```

From the Waypoint checkout root, run the importer and use the folder shown under **Settings → Data folder** for
`$waypointData`. The default is `%LOCALAPPDATA%\EmulatorWorkspaceLauncher`.

```powershell
python tools/import-crafter-zh-cn.py $export "$localExile/data/schema/schema.min.json" `
    "$waypointData/localization/crafter.zh-CN.jsonl"
```

If the extracted tables are under `data/simplified chinese`, use those paths for the five Traditional Chinese tables
above and pass `--table-language 'simplified chinese'`. Reopen the item crafter with **简体中文** selected. Missing
translations use the Gateway's English names and descriptions. Search accepts both languages. Crafting, saving and giving
an item still use the Gateway's original IDs and numeric rolls. The clipboard importer reads the existing English PoE
and Path of Building formats.

## Another language

1. Add `Strings.<tag>.resx` with every source of `Strings.resx` translated (`add-translations.py --language <tag>` fills it
   from a JSON file).
2. Add the tag and the language's own name to `L.Languages`, and the tag to `SatelliteResourceLanguages` in
   `src/Waypoint/Waypoint.csproj` and `src/Waypoint.Linux/Waypoint.Linux.csproj`.
3. Run the checks in CONTRIBUTING. `tests/Launcher.Checks` loops over `L.Languages`: each language has a satellite assembly
   and no other is built, each catalog holds every English source with nonempty text and the same format placeholders,
   resources load and format, and technical text, captured paths and action routing stay unchanged.
4. Look at the running app in the new language at the minimum window size, and have a fluent speaker review the text before
   calling it language-reviewed.

## Guidance

This follows Microsoft's [.NET resource-file guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/create-resource-files)
and [WPF localization guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview).
The WPF app remains on .NET 10; the .NET Framework-only LocBaml tool is not used.

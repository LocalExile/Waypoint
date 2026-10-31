# Interface localization

Waypoint supports English (`en`, default) and Russian (`ru`). **Settings → Language → Русский** changes the
interface immediately and persists the choice in the existing launcher settings. Language names stay readable in
either language. Existing settings require no migration; unknown language values fall back to English.

Coverage includes navigation, page controls, setup instructions, mod summaries, backup history and confirmations,
operation stages, tooltips and application-provided accessibility names. Technical identifiers, repository/PR titles,
paths, hashes, commands and raw diagnostic/log output retain their original text. Native Windows dialogs and system
window menus follow the Windows display language. This setting does not select the game's language.

## Implementation

The .NET SDK compiles `src/Launcher.Core/Resources/Strings.resx` and `Strings.ru.resx` into neutral English resources
and a Russian satellite assembly. The published single file carries it. No runtime download, translation service or additional NuGet package is used.

`L.Text("English source", arguments...)` resolves a resource with English fallback and formats values using the selected
UI culture. `L.Describe` translates known adapter presentation templates while preserving captured identifiers.
Canonical model/action/status values and persisted runtime/backup metadata remain unchanged. The process's number
and command-serialization culture is unchanged. WPF's `Language` follows the selected interface language.

XAML uses `{l:Loc 'English source'}` (`src/Waypoint/Loc.cs`), which updates when the selection changes; an apostrophe in
the source is written `\'`. Text written from code is refreshed by the section that writes it, on `L.Current.PropertyChanged`;
the selected section, runtime state and input fields are retained. Dialog confirmations support Escape in both languages.

## Updating a translation

Use Python 3 from the repository root to update both catalogs:

```powershell
python tools/update-translation.py --source "Play" --ru "Играть"
```

`tools/add-translations.py` does the same for a UTF-8 JSON file of `{"English": "Русский"}` pairs, and
`python tools/missing-translations.py` lists every source used in `src/` that a catalog lacks; CI runs it. Keys are `Text_` plus the first 12 lowercase SHA-256 digits
of the English UTF-8 text with LF line endings. English source changes create a new key. Multiline text can be supplied
with `--source-file <UTF-8 file>` and `--ru-file <UTF-8 file>`. Preserve placeholders such as `{0}`, `{1:N0}` and `{0:g}`.
Do not pass protocol tokens through translation before invoking runtime operations.

For another language, add `Strings.<culture>.resx`, extend the supported-language selector and normalization,
and add catalog/layout coverage. Review translations with a fluent speaker before declaring them language-reviewed.

Run the checks in CONTRIBUTING after changes. Core checks verify catalog parity, nonempty entries, resource identities,
format placeholders, English fallback, settings compatibility and technical-token preservation.

## Guidance

This follows Microsoft's [.NET resource-file guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/create-resource-files)
and [WPF localization guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview).
The WPF app remains on .NET 10; the .NET Framework-only LocBaml tool is not used.

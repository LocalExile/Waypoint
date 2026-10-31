# Local Russian localization review

This records local validation completed before 0.5.0 release preparation. At the time, product version remained
0.4.0; the private review build was not an updated public 0.4.0 package. See the [0.5.0 notes](../releases/0.5.0.md)
for the release containing these changes.

Implemented native .NET English/Russian resources, immediate persisted language selection, localized WPF labels,
setup instructions, dynamic presentation, dialogs, tooltips and accessibility names. Canonical runtime operations and
technical text remain unchanged. The sidebar was widened from 196 to 216 DIPs for Russian navigation labels.

## Verification performed

- Full solution build: no warnings or errors.
- Documented checks: Desktop 229, Core 88, Bridge 18, Mods 13, ModControl 18, ModTransport 9, Source 25: **400 total**.
- Real development app: switched English → Russian → English → Russian with mouse and arrow keys. Verified page
  shortcuts and Cyrillic mod-status search; selecting `hello` retained `/mod_enable hello` and `/mod_reload hello`.
- Real Russian UI: inspected all eight pages, including scrolling the build/hot-reload section, at 960 × 730.
  Also reviewed Play and Settings at the default 1240 × 900 size. Longer content wraps or uses existing scroll areas.
- Opened a backup deletion confirmation, verified Russian text and focused Cancel, then pressed Escape. All 29
  validation backups remained. No save restore/delete, source switch, mod change or game/server startup was performed.
- Created self-contained Windows x64 **local build output** in a separate review folder and launched it outside the
  development directory. The Russian satellite was included and the saved Russian language loaded after restart.
  No GitHub release, upload, tag or release ZIP was created.
- Restored the owner's original settings byte-for-byte after preference-persistence checks. Used the local validation
  checkout for inspection; the owner's source/client/saves were not modified.

The test harness supplies a single unshown window before pumping WPF bindings. Explicit application startup avoids
creating an extra real startup window, which could otherwise reset the test locale from saved settings and start
inspection timers. Actual standalone startup was checked after this adjustment.

Screenshots are local review evidence in `.local/localization-review/`: `english-play.jpg`, `english-settings.jpg`,
`russian-play.jpg`, `russian-settings.jpg`, page captures named `russian-*-minimum.jpg`,
`russian-confirmation.jpg`, and `standalone-russian-startup.jpg`. These are deliberately excluded from Git.

## Limits

Russian wording has not been reviewed by a native speaker. Accessibility names were checked through UI Automation;
no screen-reader listening test was performed. Scaling at 100/125/150/200% used in-process WPF layout transforms,
not physical monitor changes. Existing High Contrast resource-switch checks pass; Windows display settings were
not changed. Third-party diagnostics, raw logs and Windows-owned UI can remain in their original language.

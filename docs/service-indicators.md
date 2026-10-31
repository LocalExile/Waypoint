# Persistent runtime status indicators

Waypoint 0.6.4 adds one compact, four-column strip below the page host and above the inspection timestamp. It is outside
the scrolling/page content. Patch, Gateway, Auth and Game keep a stable order, with explicit localized labels and states.
Clicking a status navigates to Play and brings its existing service row/actions into view; no runtime operation is invoked.

| Verified state | Normal color | Non-color cue |
| --- | --- | --- |
| Server Ready with owned process; Game Running with owned process | Success / green | Check, Ready or Running |
| Stopped with no owned process/watcher | Error / red | Minus, Stopped |
| Starting or owned process/watcher not yet ready | Warning / amber | Clock, Starting |
| Explicit failed/error/conflict state | Warning / amber | Exclamation, Needs attention |
| No snapshot, missing service, failed inspection or unfamiliar status | Muted / neutral | Question, Not checked |

State strings alone cannot turn a light green without an owned process in the inspected service inventory. Watcher-only
Gateway gaps stay amber. The indicators share the existing RuntimeBridge snapshots and five-second desktop polling;
they introduce no second inventory loop. Polling still pauses while hidden to tray and refreshes on return. These are
last-verified statuses, not independent live process monitors. In-flight operations retain their last verified inventory
until completion/inspection. Checkout changes and inspection failures reset all four to unknown.

`ServiceStatusIndicator` uses a standard WPF Button and the app's theme/focus resources. Static glyph geometries are
frozen/shared. The small shadow is static and reflects the semantic brush; it never flashes or pulses. Labels and symbols
supplement color, and accessible names include the component, state and details action. The existing Success, Error,
Warning and Muted color roles apply to the strip, including custom colors and all color-vision profiles. High Contrast
uses system-colored outlines with no glow; increased contrast also disables glow. Reading font/text size affect the labels.

## Design resources

Used the already installed project UI/UX skill's color-only guidance and WPF resource/performance guidance, alongside
[Microsoft accessibility best practices](https://learn.microsoft.com/en-us/windows/win32/winauto/accessibility-best-practices)
and [Windows color guidance](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-color). No new runtime dependency,
asset download, skill install, global configuration or external account was required. Functional status colors remain
independent of the decorative scheme and yield to the selected accessibility/custom palette.

## Validation

The desktop checks include mapping of ready/stopped/missing/invalid/error/watcher states; actual live WPF brush updates
across all 18 schemes, Light/Dark/System and four vision profiles; custom colors; static glow with reduced motion;
crisp increased/High Contrast treatment; accessible state names; details navigation and clearing stale inventory.
All four labels are arranged on every page at 960×730 in English/Russian, including 150% OpenDyslexic text.

Interactive screenshots use a clearly marked synthetic runtime preview. Its bridge permits only Inspect and never
inventories or signals actual processes. Dark 1240×900 and light 960×730/150% OpenDyslexic were visually inspected.
Changing to Settings retained the strip, and clicking Auth brought its Play row and action into view. Screenshots:
`.local/status-review/dark-preview.png` and `.local/status-review/light-large-preview.png`.
These screenshots demonstrate presentation states, not real servers/game running. Actual Windows monitor/DPI switching
and screen-reader narration are not claimed.

The solution builds with zero warnings/errors. The full regression suite passed **1,764 assertions/tests plus one
skipped Python symlink test**: 1,498 desktop, 143 core, 18 bridge, 13 mod startup, 18 mod control, nine mod transport,
25 source/save, 21 account bridge and 19 Python account-data checks. No real game/server action or saved preference
change was performed by the automated/preview checks.

## Portable review build

- Source: `483b63d9454322f284b626b3f812ce5c27e65a63`.
- ZIP: `.local/releases/483b63d-20261031-011318-0770ba84/Waypoint-0.6.4-win-x64.zip`.
- SHA-256: `ca0f7708bb2e48326b84ed9762f59a6eae757ca2d75bdc88febb6530322acd8a`.
- External EXE: `F:\Waypoint Release Validation\0.6.4-review-483b63d\Waypoint\Waypoint.exe`.
- The actual ZIP passed all 415 payload hash/size, branding/version, runtime/license, Russian satellite and absence
  of extra/private game-file checks. The extracted app showed Version 0.6.4 and all four actual checkout components
  as Stopped, matching their service rows. Screenshot: `.local/status-review/release-0.6.4.png`.
- The older review copy was exited normally with leave-services selected. Original owner preferences were restored
  byte-for-byte (SHA-256 `abc218bd7570e3027e0628b135774d372ddeaad4ac5037623e9a914695c9da79`).
  One extracted 0.6.4 workspace was left open for review. No real service or game was started/stopped, and no changes
  were pushed, uploaded or published.

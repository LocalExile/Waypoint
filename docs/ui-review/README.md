# Local UI/UX review

This is the historical local review record for UI work following Waypoint 0.4.0. During that review, no release,
tag, push, deployment or upload was performed, and the product version stayed unchanged. The owner subsequently
authorized inclusion in [Waypoint 0.5.0](../releases/0.5.0.md). The stable settings identity remains unchanged.

The build fingerprint in this directory records the pre-commit UI-review snapshot. Subsequent local commits do not change that historical artifact or turn it into a published release. Before grouping the pending work into commits, all documented checks were rerun: 158 desktop, 78 Core, 18 bridge, 13 mod-startup, 18 mod-control, 9 mod-transport and 25 source/save assertions passed (319 total).

## App and scope

- Framework: native WPF, `net10.0-windows`, .NET SDK 10.0.403 on the validation machine; no third-party UI framework or package dependency.
- Architecture: XAML shell/shared resources; desktop partial classes for page interactions; independent Launcher.Core models/catalogs and PowerShell runtime adapter. This change preserves that architecture and the existing operation handlers.
- Main tasks: play locally, inspect/control owned services, prepare a checkout, test branches/PRs, build/reload mods, protect/restore characters, read logs. The owner/user need not know Git or build commands to use the main workflows.
- Existing instructions: the owner's local Git permission and publishing restriction apply. No on-disk AGENTS.md was found in Launcher or its ancestor directories during initial inspection. The working tree was clean at the start.

## Implemented

- Sidebar navigation for all eight pages, selected state and keyboard page/search/refresh commands. Existing programmatic navigation (setup, backup manager, Gateway logs) stays synchronized with the sidebar.
- Play uses actual readiness in its title, disables unavailable start/stop/close actions, explains why stopping is unavailable with the game running, and collapses detailed setup checks behind their summary.
- Mods and Backups use the available height for the list beside a scrollable detail pane. Actions stay above long details; explanatory text is an optional disclosure below. Existing backup switches remain directly available.
- Consistent list selection/focus, themed checkboxes and scrollbars, reserved focus border space, semantic dynamic brushes, app-level Windows High Contrast resources, explicit PerMonitorV2 manifest and layout rounding.
- Named service restart controls, navigation/page automation peers, search names, written empty/no-match states, clear-search control and inline PR-number validation.
- Stable mod and service item containers through unchanged polling; service metrics update through property notifications. Log output retains selection/scroll position on updates; selected log source is a radio choice and a visible heading.
- Failed inspection clears stale service/mod/backup actions. Older revisions without a mod host remain safe to display even when their game is running.

Keyboard commands: **Ctrl+1–8** pages in sidebar order; **Ctrl+Tab / Ctrl+Shift+Tab** next/previous page; **Ctrl+F** current page search (or repository field on Settings); **F5** local status refresh. Native arrows select list items; Tab/Shift+Tab move between controls.

## Resources

See [resource review, compatibility, licensing and pinned versions](design-resources.md) and [installed file hashes](design-skill.lock.json). UI UX Pro Max is project-scoped under `.agents/skills`, not installed globally and not bundled into the product.

## Verification

Exact commands, from `F:\evry\evryPath\Launcher` in PowerShell:

```powershell
dotnet build Launcher.slnx --nologo -v quiet
dotnet run --project tests/Launcher.Desktop.Checks --no-build
dotnet run --project tests/Launcher.Checks --no-build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Bridge.Tests.ps1
```

Desktop checks load real WPF resources and page layout without showing a window, starting runtime services, writing settings or fetching source metadata. They cover two-way navigation, state-dependent actions, unsupported mod hosts, no-match recovery, container retention, text contrast, system-color resources/restoration and layout at three logical layout sizes (960×730, 1006×691 and 1226×861) at simulated 100/125/150/200% scales. Simulation does not change Windows display settings or prove real monitor transitions.

Results: build passed with **zero warnings/errors**; **158 desktop checks**, **78 Core checks**, and **18 bridge checks** passed (**254 assertions total**, including custom-frame/Snap coordinate and additional narrow-layout checks). `git diff --check` passed. All 74 pinned skill/license files matched the recorded SHA-256 inventory.

Live inspection used the existing independent validation checkout (`LauncherValidation-20261030-7d19e778/LocalExile`, commit `89b7aa9`) rather than changing the owner's checkout. Observed Play, Builds, Mods, Backups and Logs in the running app at default and/or minimum sizes. Minimum outer window was 1020×730; default was 1240×900. Exercised Ctrl+4/5/7, Tab, list-arrow selection, clear-search recovery, selected-mod details, backup selection/history, log-source radio choices, Ctrl+Tab, Ctrl+Shift+Tab and F5. Mod selection survived subsequent five-second refreshes. Accessibility capture confirmed page contents remain exposed and lists/search/restart controls have readable names.

A real **Build selected** of Hello completed from the GUI using keyboard focus and Enter. Busy state disabled mutating mod actions and the backup preference, progress/completion was visible, automatic backup increased the validation database's history from 28 to 29, and controls recovered. The operation result was `Ok: true`, `BuildMod completed.` No mod choices were changed, no game/server was started, and no save restore/delete was performed. Validation source stayed clean and the original absent mod-state file stayed absent. The saved launcher settings SHA-256 remained `abc218bd7570e3027e0628b135774d372ddeaad4ac5037623e9a914695c9da79` throughout.

Iteration found and fixed: default page selection being cleared during navigation setup; black inherited text and missing accessibility descendants after removing visible tab headers; selection binding preservation; polling replacing focused rows; a null ModEnvironment on older running revisions; and WPF's native Ctrl+Tab handler crashing when header containers are absent. The last issue was reproduced in the running app, fixed in PageHost, and both cycle directions were then exercised successfully from inside page content. The native header handler is bypassed; page cycling uses shell routed commands.

**Limits:** no Narrator/screen-reader end-to-end session, real monitor DPI transition, Windows text-size change, or OS High Contrast switch was performed. Scale and High Contrast verification here are in-process layout/resource checks; only the normal dark theme was visually inspected. No light theme is supported or newly claimed. Destructive save actions and live in-game reload were not repeated in this UI pass; existing runtime safety checks remain in place. This is a local development build from the modified working tree, not a published package or a new release. Branding still displays 0.4.0; the development DLL retains default informational version `1.0.0+9bc82f97758133b4d1f7b5faa4072e4f7b36b4aa`. Its exact SHA-256 and check totals are in [local build verification](local-build-verification.json); the commit suffix alone does not identify these uncommitted changes.

## Screenshots

### Integrated window frame follow-up

The owner requested replacing the operating-system title bar. `WindowChrome` now provides native caption/resize hit testing with app-styled minimize, maximize/restore and close commands. The existing sidebar is the single visible app identity; there is no repeated caption icon/name or colored Windows title bar. Caption buttons have accessible names and keyboard focus. Close still passes through the existing busy-operation guard. Maximized content is inset by the current DPI's native sizing frame/padded border metrics so controls are not cropped.

Built with zero warnings/errors; 115 desktop checks passed after the initial frame change. Live checks exercised minimize and return, maximize, double-click caption restore, the close button, caption dragging (observed position changed), and corner resizing down to 1020×730 with all services/setup summary visible. Saved settings remained unchanged. Native caption dragging/resize behavior comes from WindowChrome. Snap Layout support was added in the subsequent follow-up below; physical multi-monitor transitions remain untested.

### Native Windows 11 Snap Layouts

The custom maximize/restore target now returns native `HTMAXBUTTON` hit tests on Windows 11, allowing the actual Windows-owned layout picker to appear on hover. Physical bounds are recalculated from WPF screen transforms after resize, maximize, restore and DPI changes; signed packed coordinates support monitors left of or above the primary. The restored window's upper resize strip and neighboring caption buttons retain their own hit regions. Windows 10 keeps the WPF caption behavior without installing this hook.

Hover/leave feedback and press tracking are app-styled. Mouse clicks are captured and routed once to the existing maximize/restore command; releasing outside cancels, and capture loss/deactivation/state changes/close clear the pressed state. This avoids DefWindowProc tracking/painting its hidden system button instead of the custom rectangle. WPF keyboard/automation activation continues through the same command.

Live checks confirmed the native picker appears with the restored and maximized window, normal mouse maximize/restore each toggle once, Space activates the focused control in both states, and pressing then dragging off the button cancels without trapping the pointer. The initial direct native-click approach swallowed clicks and painted a duplicate native glyph; custom press tracking fixed both. The minimum width is now **960 DIPs**, down from 1020, to permit a half-width zone on a 1920-wide display at 100% scaling. The real app was resized to 960×730, with Play/services/setup remaining usable, and all relevant pages passed simulated layout checks at this width. Narrower zones or higher display scaling remain limited by minimum size.

**Verification limit:** selecting a native layout zone through the automation tool was blocked because the flyout belongs to Explorer's `PopupHost`, not the Waypoint window, and it is not exposed as a separate target. No zone selection was completed and no end-to-end snap placement is claimed. The actual popup and all app-owned button behavior were verified. No Windows setting was changed.

Final build: zero warnings/errors; **158 desktop assertions passed**. This includes signed/negative screen-coordinate decoding, exclusive edges beside the close/client areas, empty/scaled bounds, existing interaction checks, and three layout widths at simulated 100/125/150/200%. Core (78) and bridge (18) results above remain from the preceding UI verification on unchanged components.

### Sidebar marker refinement

Replaced the small Unicode northeast arrow with an original two-facet vector waypoint/navigation marker. It keeps the existing mint palette and 32-DIP badge, with explicit centered geometry instead of font glyph metrics. The graphic is decorative beside the readable wordmark. In High Contrast, its plate now uses the window background rather than the same highlight brush as the marker, keeping the silhouette distinct.

Inspected the changed header in the running app; solution build passed without warnings/errors and all 158 desktop checks passed. The close-up after previews below render the actual WPF header at 100% and 200% pixel density without starting runtime services or changing settings. They are component renders, not claims of a physical monitor DPI transition. The before image is the owner's supplied crop. Existing pre-commit regression results (319 assertions) are recorded above; this isolated graphic/theme adjustment required the desktop build/checks and visual review.

- [Before: Play at minimum size](screenshots/before-play.png)
- [Current: integrated window frame at minimum size](screenshots/custom-window-frame.png)
- [Current: native Windows Snap Layout picker](screenshots/native-snap-layouts.png)
- [Current: half-screen-compatible 960×730 layout](screenshots/snap-fit-960.png)
- [Sidebar marker before](screenshots/brand-header-before.png)
- [Sidebar marker after, 100% render](screenshots/brand-header-after.png)
- [Sidebar marker after, 200% render](screenshots/brand-header-after-2x.png)
- [Before: Mods with actions below the viewport](screenshots/before-mods.png)
- [After: Play at default size](screenshots/after-play.png)
- [After: Play at minimum size](screenshots/after-play-minimum.png)
- [After: Mods at minimum size](screenshots/after-mods-minimum.png)
- [After: Mods at default size](screenshots/after-mods.png)
- [After: backup details](screenshots/after-backups.png)
- [After: backup details at minimum size](screenshots/after-backups-minimum.png)
- [After: search recovery state](screenshots/after-mods-no-results.png)

Screenshots are local artifacts of the validation installation. No screenshot or diagnostic data was uploaded.

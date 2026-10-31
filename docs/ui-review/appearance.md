# Appearance and accessibility — local review

Initial local review following 0.5.0, before preparing the 0.5.1 release. No commit, push, tag, release or upload
had been performed at the time of this review. The private standalone review build used product version 0.5.0.

## Implemented

18 presets with independent Light/Dark/System mode; one custom scheme with separate light/dark overrides;
27 semantic color roles; wheel, keyboard HSV sliders and hex input; three reading fonts including embedded
OpenDyslexic; 100/110/125/150% text; color-vision profiles; contrast protection/enhancement; stronger focus borders;
reduced progress animation; retained Windows High Contrast priority. Apply saves, Cancel/Escape restores the saved
appearance. Ctrl+R resets the editor; Ctrl+Shift+R restores default workspace appearance.

Font resources are licensed, pinned and hashed in [the font lock](../appearance-fonts.lock.json), with the full
OFL copied into output. No fonts are installed in Windows. Theme settings never reach LocalExile's runtime adapter.
The existing WPF project design skill remains in use; no new framework, package service or global configuration was added.

## Checks performed

- Solution build with zero warnings/errors.
- 554 desktop checks, 89 core checks, 18 bridge, 13 mod startup, 18 mod control, 9 mod transport and 25 source/save
  checks: **726 total**. Preset tests cover light/dark, all four vision profiles and ordinary/enhanced foreground contrast.
  They also cover invalid preferences, separate custom colors, exact RGB/HSV round trips, actual bundled-font loading,
  High Contrast overrides, live resources, preview restoration and larger-font layouts across all eight pages.
- Running standalone application outside the development folder: English and Russian appearance screens, Light/Dark
  and current-Windows System selection, preset/custom previews, Apply, Cancel/Escape, and saved custom colors after restart.
- Pointer wheel input, exact hex entry, Shift+Tab to the brightness slider and Right to adjust its value. Invalid hex
  remains unapplied; its translated error is visible above the footer actions and exposed as field help/live feedback.
- OpenDyslexic at 100% and 150%; main window at 1240×900 and 960×730. Large-text Mods content and actions are reachable
  through whole-page scrolling. Sidebar metadata compacts under Details so navigation remains reachable.
- Recovery shortcut verified in the running app. The initial Ctrl+Alt+R chord did not trigger reliably in this environment;
  it was replaced with the app's routed Ctrl+Shift+R command and retested successfully.
- A source encoding issue found in early labels was corrected. A low-contrast hover fade was replaced by an opaque
  border cue. Invalid-hex feedback was moved out of the scrolling body into the fixed footer. Secondary sidebar metadata
  was compacted after observing the 150% layout. These were corrected before final review output.
- Original owner settings restored byte-for-byte after the persistence checks. Inspection used the isolated validation
  checkout; no game/server startup, source switch, mod change, save restore or backup deletion was performed by the UI review.

Screenshots and executable/source fingerprints are local in `.local/appearance-review/`. Representative captures:
`default-dark.jpg`, `ocean-light-editor.jpg`, `custom-light-play.jpg`, `color-wheel.jpg`,
`reading-font-150-editor.jpg`, `large-text-mods-minimum.jpg`, `russian-system-editor.jpg` and
`russian-color-validation.jpg`.

## Limits

No physical display-scaling transition, live Windows theme/High Contrast setting change, screen-reader listening,
native Russian review or participant testing with dyslexic/color-vision-impaired users was performed. Readability
preferences are personal choices, not a promise of benefit. Arbitrary mixed custom backgrounds can still conflict
with shared text colors; the editor reports low contrast. Native Windows title bars/dialog chrome follow Windows.
The public release ZIP was not rebuilt or replaced; this is a separate local review output.

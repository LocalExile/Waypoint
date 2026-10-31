# Appearance and accessibility

Open **Settings → Appearance and accessibility → Change appearance…**. The complete workspace previews your
changes. **Apply** saves them; **Cancel**, Escape or closing the editor restores the previous appearance. Runtime
actions, checkout selection, saves and game settings are unaffected.

## Modes and schemes

**Light**, **Dark** and **System** are independent of the color scheme. System reads Windows' application light/dark
preference and listens for changes while Waypoint runs. Choose any of the 18 schemes, including the existing Waypoint
mint, neutral Slate/Monochrome, Ocean/Ice/Teal, Forest/Lime, Gold/Amber/Copper, Sunset/Ruby/Rose/Orchid, Violet/Indigo
and Sand. Existing settings retain the familiar dark Waypoint appearance.

Expand **Customize colors** to edit the currently displayed light/dark mode. Choose another appearance mode above to
edit its other set. Picking a color creates the **Custom** scheme from the selected preset. Custom light and dark colors
are stored independently. Selecting a preset preserves your custom palette until you customize a different preset;
Cancel restores the palette saved before the editor opened. Reset these custom colors clears only the current mode's
overrides. Reset to defaults restores all appearance/accessibility defaults in the preview.

Eight common roles appear first. **More interface colors** exposes 19 additional roles: inputs, hovered/selected controls,
focus, borders, text on buttons/selections, logo and status backgrounds, commit badges, success/attention/error colors,
window controls, scrollbars, checkbox marks and close-button colors. Primary buttons and accent text are independent.

The picker offers a wheel, Hue/Saturation/Brightness sliders and exact `#RRGGBB` input. Sliders work with arrow keys;
typing a valid hex color previews it. Invalid hex input shows an error and cannot be applied. Escape cancels the current
color change; Cancel on the parent editor cancels the complete appearance preview.

## Reading and access options

- Color vision: Standard, red–green-friendly, blue–yellow-friendly and Grayscale. Profiles replace accent/status colors
  even in a custom palette; they are selectable color choices, not a simulation or a guarantee for every visual condition.
  Existing text labels and ✓/! symbols remain available alongside color.
- Reading font: Segoe UI, Verdana or the bundled **OpenDyslexic**. Fonts are personal preferences; no reading improvement
  is guaranteed. No system installation or runtime font download is required. Unavailable glyphs use Windows fallback.
  Branding stays in its original font, and logs/hashes remain monospaced.
- Text size: 100%, 110%, 125%, 150%. Shared text sizes update immediately, including captions. Mods and Backups gain
  whole-page scrolling as more room is needed, while retaining finite list viewports. At larger sizes or with
  OpenDyslexic, secondary sidebar metadata collapses under Details and the subtitle is hidden to leave room for navigation.
- Protect text contrast: adjusts foregrounds where possible toward 4.5:1. Increase text contrast targets 7:1 and may
  adjust button fills. Highly mixed custom backgrounds can defeat a shared text color; the preview reports low-contrast
  combinations so they can be corrected. This is not a claim of complete WCAG conformance.
- Stronger keyboard focus borders: increases focus borders from 2 to 4 DIPs.
- Reduce motion: disables the indeterminate progress animation. Existing stage, elapsed-time and live-region feedback
  remains. Windows' client animation preference is respected automatically.
- Windows High Contrast takes precedence over all palettes, including custom colors, and retains the selected reading
  font/text size. System theme and High Contrast preferences are read only.

Recovery: **Escape** cancels the current preview. **Ctrl+R** resets the appearance editor. In the workspace,
**Ctrl+Shift+R** saves default appearance preferences without changing the checkout or saves. Existing page/search/refresh
keyboard shortcuts remain available. The new controls have application-provided accessible names and Russian labels.

## Implementation and resources

The WPF framework is unchanged. `ThemePalette` generates/validates semantic roles; `ThemeResources` applies brushes,
font sizes and motion/focus values to existing dynamic resources. Appearance preferences are typed launcher settings.
Preview edits copy dictionaries so Cancel never mutates saved custom colors. Unknown mode/scheme/font IDs, invalid
hex values and excessive text scales are normalized safely. `Microsoft.Win32.SystemEvents` handlers are detached on exit.

The project-scoped UI UX Pro Max WPF guidance informed resource dictionaries, system contrast and redundant state cues.
Official guidance consulted: [WPF font packaging](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/packaging-fonts-with-applications),
[Windows preference events](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.userpreferencechanged?view=windowsdesktop-10.0),
[W3C text contrast](https://www.w3.org/WAI/WCAG21/Understanding/contrast-minimum) and
[color-independent information](https://www.w3.org/WAI/WCAG21/Understanding/use-of-color.html).
Web contrast guidance supplies numeric/readability principles; native WPF resources and UI Automation provide the implementation.

### OpenDyslexic provenance

Unmodified regular/bold OpenType assets and the complete SIL OFL 1.1 notice are pinned to the author's
[GitHub snapshot](https://github.com/antijingoist/opendyslexic/tree/1824da5c0e41dc3e13ffc7f3a636dcaf695d61b7).
The author's [distribution page](https://antijingoist.itch.io/opendyslexic) identifies the license and explains that current
development moved away from the archived GitHub mirror. That mirror is a reproducible compatible snapshot, not a claim
to contain the latest font release. No upstream scripts were executed. The font remains under its own license rather
than Waypoint's MIT license. Exact source/file hashes are in [the font lock](appearance-fonts.lock.json).

## Validation limits

Automated checks cover every preset in both modes and all four color-vision profiles, standard/enhanced contrast,
malformed preferences, separate custom colors, HSV/RGB round trips, embedded font loading, High Contrast overrides,
resource changes, preview cancellation and representative larger-font page layouts. Settings checks preserve repository
selection. Live UI checks and screenshots are recorded locally during review. Physical monitor/DPI transitions, a live
Windows theme/High Contrast setting change, screen-reader listening, native-speaker review and user testing with people
with dyslexia or color-vision differences require additional human review. No Windows setting is changed by these checks.

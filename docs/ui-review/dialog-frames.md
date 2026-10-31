# Shared dialog frames — 0.5.2

## Scope and design

The app-owned window inventory is MainWindow plus six modal dialogs. DialogWindow now supplies a shared frame for
AppearanceDialog, ColorPickerDialog, ConfirmationDialog, SetupInstructionsDialog, CreateAccountDialog and
DiagnosticPreviewDialog. DialogLayout accepts that base type, so new dialogs using the shared setup also get the frame.
The Windows-owned folder picker and external browser/File Explorer windows keep their native interface.

The frame uses the existing background/foreground/outline, caption-button and focus resources. It keeps a compact
title, full-width drag area, an accessible localized Close command, and native resize/system-menu behavior through
WindowChrome. It does not add repeated branding or independent modal taskbar entries. The caption grows with the
selected reading font/text size; its native drag region follows the measured height. Each dialog owns its own chrome
instance. Modal minimize/maximize commands are disabled; the main window's Snap integration is untouched.

The project-scoped UI UX Pro Max WPF search selected keyboard navigation and routed-command guidance. Official
[WindowChrome documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome?view=windowsdesktop-10.0)
supports retaining native behavior while replacing visuals. Current [Windows title-bar design](https://learn.microsoft.com/en-us/windows/apps/design/basics/titlebar-design)
informs theme blending, scaling, ellipsis and caption controls. The older
[Windows dialog guidance](https://learn.microsoft.com/en-us/windows/win32/uxguide/win-dialog-box#title-bars)
supplies still-relevant modal conventions: no redundant title icon, available Close, and Close behaving as cancellation.
Its older visual examples are not the product's style source. No dependencies or system settings were added.

## Verification

680 desktop checks plus 89 core and 83 runtime/mod/source checks: 852 total. New checks exercise every dialog in
English/dark, Russian/light/OpenDyslexic/150%, and simulated High Contrast resource states at minimum widths.
They verify full titles, caption height/hit regions, Close target size, ownership/taskbar behavior, native command
availability, themed frame colors and routed Close cancellation without saving, approving, creating an account or
copying a diagnostic report. Longer forms use a bounded scrolling body and a fixed action area.

Native review confirmed Appearance light/dark preview, native caption movement, resize down to approximately
640×540, system Move/Size/Close with modal Minimize/Maximize disabled, title-bar cancellation, and the matching
nested color picker. Screenshots and release-build fingerprints are kept locally under `.local/release-validation-0.5.2`.
The final package is also checked after extraction outside the development tree.

The automation helper does not list owned dialogs without taskbar entries as distinct keyboard targets. Their
screenshots and pointer interaction are available through the owner; keyboard Close routing is tested in-process.
An attempted owner-targeted Alt+F4 did not dismiss the nested picker and is not claimed as a successful native
keyboard test. No physical DPI transition, real High Contrast toggle or screen-reader listening was performed.

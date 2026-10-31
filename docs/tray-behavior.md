# Window close and notification area

**Settings → Window behavior** controls X and Alt+F4 through two explicit groups: **When I close the window** (keep
Waypoint running in the tray or Exit Waypoint), then **When Waypoint exits** (leave LocalExile services running or
stop services before exiting). Defaults remain Exit/leave-services. Settings also keeps full-exit preferences available
when X hides to tray, since a later tray-menu Exit still uses them. Changes save immediately.

The first-close prompt uses the same shared controls and labels, with the window decision first. Service options
appear only for Exit. One final button describes the actual result: Hide to tray, Exit Waypoint or Stop services and
exit. The body comes before the footer in both visual and accessibility reading order. Cancel/Enter/Escape leave
everything open. Remember this for next time saves the selected behavior; unchecked performs only this action.

**Always ask when I close the window** is a separate opt-in. It keeps opening the prompt even when a default close
action is already saved, and opens Remember unchecked. Cancelling or taking a one-time action preserves Always ask.
Remembering a choice turns Always ask off. Explicit tray-menu Exit and Windows session ending bypass this prompt.
Changing only service behavior does not itself confirm an outstanding first-close choice. A saved tray choice may
be combined with stopping services on a later full exit.

The ordinary minimize button still uses the Windows taskbar. Close-to-tray hides the existing WPF workspace and keeps
the process, operation, discovery and single-instance endpoint alive. It adds the existing Windows app icon only while
hidden. Open restores the same workspace; Exit bypasses the hide preference. If an operation is busy, Exit reopens the
workspace with the existing wait message. Hiding never stops the game or services.

On an ordinary full exit with service stopping enabled, Waypoint reopens a hidden workspace to show progress. The
existing operation gate covers a fresh inventory, graceful Stop and verification. Only this checkout’s owned servers/
Gateway watcher are eligible under the existing adapter rules. If the game is running, a themed prompt defaults to
Cancel; the alternative exits while leaving everything running just this time. The game is never automatically closed.
Failed inspection, a failed graceful stop or remaining game/server/watcher keeps the workspace open with error details.
Turning the option off in Settings permits a normal exit without stopping services. No force termination is introduced.

Only periodic status inspection pauses while hidden; in-flight operations continue. Restoring performs a status refresh
and resumes polling. A repeated launch restores the window before applying the existing focus/build/checkout policy;
it never adopts a different checkout. Window state and owned dialogs retain the normal single-instance behavior.

The implementation uses Shell_NotifyIcon on the workspace HWND and a WPF context menu with shared dynamic color/font/
focus resources. It supports the version-4 keyboard activation/context-menu messages. A TaskbarCreated broadcast restores
registration after Explorer recreation; a failed registration shows the workspace with an explanation. Initial registration
also precedes hiding, so an unavailable shell cannot strand the workspace. Real exit removes the icon and its HWND hook.
Windows session ending bypasses first-use/tray/service prompts and does not obstruct sign-out or shutdown; Windows
manages process termination in that case. The stop-services preference applies to normal Waypoint exits.

Presentation preferences are stored beside the existing settings using optional `CloseToTray` and `CloseBehaviorConfirmed`
fields, plus optional `StopServicesOnExit` and `AlwaysAskOnClose`. No emulator code, database, Windows notification-area setting or startup
registration is changed. Service shutdown reuses the existing runtime bridge and operation logs.

## References

- [Microsoft notification-area guidance](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
- [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
- [NOTIFYICONDATA](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)
- [WPF window closing](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/how-to-close-window-dialog-box)

## Initial validation (before the clarity follow-up)

The full regression suite passes: **1,478 assertions/tests plus one skipped Python symlink test**. This includes
1,215 desktop, 140 core, 18 bridge, 13 mod startup, 18 mod control, nine mod transport, 25 source/save, 21 account
bridge and 19 Python account-data checks. The solution builds with zero warnings/errors.

- Optional-setting migration/round-trip and first-use, busy, explicit-exit and Windows-session-ending decisions.
- The real English/Russian prompt and explicit either/or service choices, Cancel defaults, light/dark appearance,
  large OpenDyslexic text, High Contrast and fixed reachable actions around a scrollable explanation.
- Real Shell_NotifyIcon registration/removal and native callback dispatch through a disposable window HWND, including
  mouse/keyboard Open, context-menu focus, menu Exit and delayed callbacks after icon removal. These exercise the
  native message contract; they do not simulate a physical click in Windows' notification-area overflow.
- Real WPF close lifecycle against a disposable bridge with synthetic inventories: service stopping disabled,
  successful graceful stop/verification, failed stop, open-game Cancel/leave, busy exit and busy tray hide/restore.
  The fixture never inventories or signals real game/server processes and never writes the owner's preferences.
- Service workflow checks additionally cover missing/failed inspection, a remaining watcher and newly observed runtime
  processes after stopping. The existing runtime adapter and its process-ownership/signal safeguards are unchanged.

Real Windows sign-out and Explorer restart are intentionally not performed during a working desktop session.
Mixed-monitor tray-menu placement and a screen-reader session remain manual checks. Layout scaling checks use WPF
layout/text transforms and do not claim an actual Windows DPI-setting or monitor change.

## Initial clean portable review build (superseded below)

- Package source: `d9bfd7c6efff45e487b618268f2ea718baacc1e9`.
- Local ZIP: `.local/releases/d9bfd7c-20261031-003251-592d582d/Waypoint-0.6.3-win-x64.zip`.
- ZIP SHA-256: `a0b5488976c4953417e472666fb2d09e6e5b4426307f2dd84886236d16c759a3`.
- Extracted review EXE: `F:\Waypoint Release Validation\0.6.3-review-d9bfd7c\Waypoint\Waypoint.exe`.
- Package checks passed for all 415 payload sizes/hashes, clean-source manifest, branding/version, Russian satellite,
  Windows x64 EXE and bundled runtime/licenses, with no extra/private game files.
- In the running external package, clicking title-bar X showed both choices with keep-running selected. Clicking
  Stop and then Keep changed the radio selection and explanatory text. Remembered tray mode hid the window while
  retaining process 43428; a second launch exited with code zero and restored that same process/window, with one
  instance remaining. Settings' Ask again reset the prompt, and Exit closed the process completely.
- The owner's original settings were restored byte-for-byte afterward (SHA-256
  `abc218bd7570e3027e0628b135774d372ddeaad4ac5037623e9a914695c9da79`) and the package was reopened for review.
  Tray mode/service stopping remain off and the first-close prompt is pending. No real service-stop operation was
  performed during interactive checks. Prompt screenshot: `.local/tray-review/first-close-0.6.3.png`.
- The desktop automation tool targets the owner when sending modal keyboard input, so the attempted live radio-arrow
  navigation did not exercise the dialog. Radio mutual exclusion/accessibility/defaults were checked in-process;
  physical modal arrow navigation and notification-area overflow clicking remain manual review items.

Nothing was pushed, uploaded or published. This documentation commit does not alter the packaged executable.

## Clarity and Always ask follow-up

The prompt previously displayed service-exit options before the user had decided to exit, then mixed that preference
with three immediate action buttons. Settings used two switches for independent lifecycle events. Both now use a shared
`WindowBehaviorChoices` view: window close first, service exit second, explicit alternatives and consistent labels.
The prompt hides service controls for tray mode and uses a single result-specific action plus Cancel. The body precedes
the fixed footer in visual, tab and accessibility reading order; accessible names identify each choice rather than its heading.

The full suite passed **1,499 assertions/tests plus one skipped Python symlink test**: 1,233 desktop, 143 core and
the unchanged bridge/mod/source/account suites described above. Additional checks cover conditional disclosure, preserved
pending choices, result-specific action text, always-ask persistence/policy and repeated real WPF close prompts with
synthetic runtime inventory. English/Russian, large OpenDyslexic text, light/dark and High Contrast checks pass.

Interactive checks used the real production prompt in an isolated standalone host, with no runtime operation or settings
write: Tab/directional keys moved focus, Space selected the choices, tray selection removed service controls, returning to
Exit restored them, choosing service shutdown changed the action label, and Escape cancelled. The standalone host avoids
the desktop automation tool's owner-window keyboard targeting limitation from the initial validation.

The actual development app's Settings view was inspected, Always ask was enabled, and X opened the new prompt with
Remember off. A one-time Exit left `AlwaysAskOnClose=true` persisted. Original owner settings were then restored to the
same SHA-256 recorded above. Screenshots are in `.local/close-ux-review/`: `dialog-tray.png`, `dialog-exit.png`, `settings.png`.
No real game/server stop was requested. Explorer restart, sign-out, physical notification-area overflow interaction,
screen-reader narration and mixed-monitor placement remain outside these checks.

### Updated portable review package

- Clean package source: `29325dfd631088a80e01cc188b71fd73ccce7f0f`.
- ZIP: `.local/releases/29325df-20261031-005539-ceceebf7/Waypoint-0.6.3-win-x64.zip`.
- SHA-256: `d169646c502d53dc00323f6c865f9a1cc33a669fa2075647dfb49e0768a19a1d`.
- External EXE: `F:\Waypoint Release Validation\0.6.3-review-29325df\Waypoint\Waypoint.exe`.
- The actual ZIP passed all 415 payload size/hash, branding/version, platform/runtime/license, Russian satellite and
  no-extra/private-files checks. Settings and the prompt were inspected in this extracted build.
- Always ask opened the prompt with Remember unchecked. Explicitly checking Remember and applying Exit wrote
  `AlwaysAskOnClose=false`, `CloseBehaviorConfirmed=true`, and preserved Exit/leave-services defaults. The process
  exited normally. Original owner settings were restored byte-for-byte and the updated package reopened for review.
- This supersedes the initial 0.6.3 review package above. No push, publication or real service shutdown was performed.

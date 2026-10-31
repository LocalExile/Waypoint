# Window close and notification area

The optional **Settings → Window behavior** preference controls closing the main window (including X and Alt+F4).
It defaults off. A separate **Stop this checkout’s services when exiting Waypoint** option also defaults off.
Missing fields in older settings keep both defaults and show a first-close prompt containing both choices. The prompt's
Cancel/Enter/Escape actions leave the workspace open. Remembering either explicit choice suppresses subsequent prompts;
an unchecked Remember box performs just that one action, including the one-time service-exit choice. Changing the
Settings tray checkbox is an explicit remembered close choice; changing only the service option leaves the first-close
prompt pending. A remembered tray choice may be combined with stopping services on a later full exit.

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
fields, plus optional `StopServicesOnExit`. No emulator code, database, Windows notification-area setting or startup
registration is changed. Service shutdown reuses the existing runtime bridge and operation logs.

## References

- [Microsoft notification-area guidance](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
- [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)
- [NOTIFYICONDATA](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)
- [WPF window closing](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/how-to-close-window-dialog-box)

## Validation

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

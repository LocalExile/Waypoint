# UI/UX resources and design decisions

Reviewed 2026-10-30 for Waypoint, an existing WPF/.NET 10 Windows desktop tool. These resources are development aids, not application dependencies. No framework migration, hosted services, accounts, global configuration, npm installation, hooks, or downloaded binary tools were required.

## Selected

- **UI UX Pro Max**, MIT, [source](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill), pinned commit `9a3f14f7423037ccb8feeb4fee7482291b9c820e` (2026-10-30). Installed the complete `.claude/skills/ui-ux-pro-max` directory into the supported project skill location `.agents/skills/ui-ux-pro-max`, retaining its instructions, data, references and Python scripts. The upstream root MIT license is included alongside the skill. [File hashes](design-skill.lock.json) cover the installed source and license, excluding generated Python caches. The app neither imports nor packages this skill.
- **Microsoft WPF guidance**, accessed 2026-10-30: [focus](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/focus-overview), [commands](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/commanding-overview), [resources](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/systems/xaml-resources-overview), [control virtualization](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls), and [DPI awareness](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows). Consulted as documentation; not copied into the app or installed as a skill.
- **Bundled Computer Use**, environment package `computer-use/26.1027.30824`, using `@oai/sky` through `node_repl` for screenshots, keyboard navigation, resizing and UI inspection. This is provided by the agent environment; no project or system installation was made.

The skill files were read before application. Search entry point and imports were reviewed: local Python standard-library CSV searches, no package installation, remote evaluation or network calls in the search path. Persistence is opt-in and was not used. Python 3 already existed on this machine. Project product requirements take precedence over generic skill recommendations, including mobile touch metrics, CSS, ARIA and web framework examples.

The vendored skill subtree is exempt from Git line-ending conversion and whitespace checks in `.gitattributes`. This preserves the exact pinned upstream bytes, including upstream whitespace, so a fresh Windows checkout still matches the lock file. Application source retains the repository's normal formatting checks.

For the custom title-bar follow-up, implementation follows Microsoft's [WPF WindowChrome documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.shell.windowchrome?view=windowsdesktop-10.0), [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi), and [sizing/padded-border metric definitions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics), accessed 2026-10-30. The skill's narrow chrome query returned no result; the broader custom-window query returned general commands/property guidance. WindowChrome behavior is based on the official documentation and running-app checks, not a claimed skill database match.

The Snap Layout addition follows Microsoft's [custom-caption Snap Layout integration](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu), [WM_NCHITTEST signed coordinates](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest), [non-client clicks](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nclbuttondown), and [mouse capture/loss](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcapture). Return `HTMAXBUTTON` only within the current physical maximize rectangle on Windows 11. Windows owns the actual layout popup; no fake picker or OS setting change is involved. Custom click tracking uses the existing routed maximize/restore command, avoiding DefWindowProc's hidden system-button rectangle and duplicate painting. Capture is released on mouse up, cancellation, deactivation, state changes and close.

The installer was the bundled Codex `skill-installer/scripts/install-skill-from-github.py`, with explicit `--dest` and immutable `--ref`. To reproduce without that helper, obtain the pinned repository snapshot, copy only the specified skill directory to `.agents/skills/ui-ux-pro-max`, include the root `LICENSE`, then compare the lock file hashes. Existing skill directories must not be overwritten silently. The skill is discoverable on the next agent turn; its instructions were read and applied directly during this task.

Queries actually used (from the repository root):

```powershell
python .agents/skills/ui-ux-pro-max/scripts/search.py "keyboard focus navigation" --stack wpf -n 3
python .agents/skills/ui-ux-pro-max/scripts/search.py "resource dictionaries high contrast" --stack wpf -n 3
python .agents/skills/ui-ux-pro-max/scripts/search.py "empty search results" --domain ux -n 2
```

Native matches informed routed commands, focus behavior, shared dynamic resources and High Contrast. The empty-search result was web-labelled; only the framework-independent recovery principle was applied. A master/detail query returned no match; a narrower hierarchy query returned color-only and breadcrumb guidance. The two-pane layout is an engineering/design judgment from inspecting Waypoint's actual data and task flow, not a claimed database recommendation.

## Considered, not installed

| Candidate | Snapshot reviewed | Decision |
| --- | --- | --- |
| [Microsoft win-dev-skills](https://github.com/microsoft/win-dev-skills) | `5ce74fa89c49ca353c6ab03bd5889aa519aa9e5e`; MIT; active, not archived | Present skill tree targets WinDbg investigation. Useful for native debugging, not the primary UI design work here. |
| [Windows LOB XAML](https://learn.microsoft.com/en-us/windows/apps/get-started/line-of-business/windows-lob-xaml-skill) | Microsoft Learn, accessed 2026-10-30 | Explicitly WinUI 3/Windows App SDK: Microsoft.UI.Xaml, x:Bind and ThemeResource. Waypoint uses System.Windows/WPF. Do not import incompatible implementation instructions or migrate the app to fit them. |
| [Impeccable](https://github.com/pbakaus/impeccable) | `df3a4abe7d0840d5f60324812c432e00b66a4e75`; skill 4.8.0; Apache-2.0; active, not archived | Reviewed actual skill. Broad web/native-mobile design workflow overlaps the selected resource; setup launches/downloads a separate binary. Avoid that extra toolchain for this focused WPF refinement. |

## Waypoint design rules

- Users run a local game, manage servers, test source/mod changes and protect characters. This is a task tool, not a marketing surface.
- Retain navy surfaces, mint primary actions, Segoe UI, full checkout paths, dynamic repository name, seven-character commit badges with full identity on hover, and existing backup defaults.
- Put all eight destinations in the existing sidebar. Keep source identity and version there. Use the existing page content and runtime command handlers.
- Keep the list and selection detail visible together for mods/backups. Anchor actions before long text. Scroll each independently; disclose explanatory material below instead of pushing work offscreen.
- A displayed enabled choice, built artifact and confirmed loaded mod remain different facts. No new action bypasses the runtime safety checks.
- State-dependent titles, disabled controls and written explanations must reflect actual readiness. No color-only status or simulated success.
- Use WPF routed keyboard commands. Keep native list selection semantics and a visible focus border without changing control geometry. Keep unchanged row containers through polling.
- Use semantic dynamic brushes, SystemColors in Windows High Contrast, layout rounding, vector/font glyphs and PerMonitorV2 awareness. Dark is the established normal theme; no separate light theme is introduced.
- Continue to review actual rendered screens, keyboard access, error and empty states. Simulated scale/layout checks do not prove a real multi-monitor DPI transition or screen-reader usability.

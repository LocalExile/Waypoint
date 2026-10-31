{{PRODUCT}}
{{SUBTITLE}}

This is a Windows x64 portable preview. No installer is required.

1. Extract the entire ZIP to a folder you can keep, such as Documents\{{PRODUCT}}.
2. Open that folder and double-click {{EXECUTABLE}}.
3. Select your LocalExile checkout, or use Find LocalExile to look for it.
4. In Setup, follow each missing requirement. Copy your complete supported
   client into the selected checkout's Client folder, then choose Prepare
   local client. Install extraction dependency if it is missing.
5. For a new installation, keep the game and servers stopped, open Setup,
   scroll to First local account and choose Create local account.
6. Press Play, sign in inside the game and create/select a character there.
   Builds manages branches, PRs and hot reload. Backups manages save snapshots
   and the automatic-backup preference. Mods manages local mods and shows
   their last confirmed runtime status.
7. In Settings > Language, choose English or Русский. The interface changes
   immediately and remembers your choice. Logs and technical identifiers
   keep their original text. English is the default.

Use Ctrl+1 through Ctrl+9 to choose a page, Ctrl+Tab or Ctrl+Shift+Tab to
cycle pages, Ctrl+F to search the current page and F5 to refresh local status.
On Windows 11, hover over the maximize button to open native Snap Layouts.

Keep all files and subfolders together. Moving only the EXE will not work.
The launcher includes its .NET Desktop runtime. Building/running LocalExile
still needs its own prerequisites: .NET 10 SDK, Git, Python, Node.js and a
prepared standalone PoE 3.29.3b client in the standard installation layout.
The optional native game-data reload helper also needs Frida in the selected
checkout's Python environment.

The launcher does not download the game. It does not contain LocalExile,
client archives, accounts, saved characters or signing keys.

Settings, save backups and operation logs live under:
  %LOCALAPPDATA%\EmulatorWorkspaceLauncher
This identity stays stable across product-name changes and portable upgrades.
Game files and saves remain in your selected LocalExile installation.
On first close, choose Minimize to tray or Exit and whether a full exit should
stop this checkout's services. Tray mode and stopping on exit default off.
Remember my choices saves both; both can be changed in Settings > Window behavior.
Hiding always keeps services running. Click the icon beside the clock (possibly
under the hidden-icons arrow) to return, or right-click it for Open and Exit.
Opening the EXE again also restores the hidden workspace. The normal minimize
button continues to use the taskbar. With stopping on exit enabled, an open
game prompts Cancel or Exit and leave running. Services stop gracefully; a
failure keeps Waypoint open. No game or server is force-killed on exit.

Branch/PR switching requires the game closed. Current characters are kept by
default and automatic backups are on. Starting fresh replaces accounts and
characters in the selected save database. You can restore a prior snapshot
from Backups; restoration does not switch source or restore client keys.

If something fails, use Open failed stage log or View operation output in
the status area. Logs also provides service output and a Diagnostic report
preview. Review that preview before sharing it on the repository's Issues
page; do not share passwords, keys, game files or save databases.

To upgrade, extract a new release into its own folder and run its EXE.
Existing settings/backups are retained. Do not extract over a running copy.

release-manifest.json records the source commit, build mode and file hashes.
RELEASE-NOTES.md describes this version's features, compatibility and limits.
LICENSE.txt contains Waypoint's MIT license. Bundled runtime license/notice
files are in licenses and retain their own terms. No game files are licensed
or distributed by Waypoint.
This preview has no installer, signing, automatic update or telemetry service.

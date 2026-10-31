# Waypoint

**Workspace manager for [LocalExile](https://github.com/LocalExile/LocalExile)**

A Windows desktop launcher for a LocalExile server checkout: set it up, play, start and stop the servers and the game
client, switch branches and pull requests, manage mods, save backups and accounts, watch the services and their
performance, read every log and send the Gateway's chat commands with suggestions.

Waypoint brings together two launchers: the interface, performance tools and console of JetStream's LocalExile
Launcher, and the setup, mod, backup, account, appearance and language tools of the first Waypoint.

It is a separate tool: it references no server project and drives a checkout only through what the server offers outside
tools (`scripts/dev/live.ps1`, the Gateway's `commands`/`values` modes, the dev host's protocol, the client control bridge,
the logs) and through its own adapter in `scripts/`. Every start goes through `live.ps1`, so the server's rules apply
unchanged. It does not download Path of Exile, bundle LocalExile or offer a public server.

## Install

Requirements: Windows 10/11 x64, the .NET 10 Desktop Runtime, and your own standalone PoE **3.29.3b** client. The setup guide
offers two ways to get LocalExile:

- **The ready-to-play release** (recommended). Nothing else to install: Waypoint downloads the release, checks its SHA-256, and
  runs its `LocalExile.exe` to prepare your client and create your account.
- **The source code**, for working on LocalExile, which also needs the tools below. The **Setup** section walks through what
  the checkout still needs; the server's
  [GettingStarted](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md) is the full guide.

| A source checkout's requirement | Used for |
| --- | --- |
| A [LocalExile](https://github.com/LocalExile/LocalExile) source checkout | The server Waypoint manages: the folder with `GameServer.slnx` |
| [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | Building and running LocalExile |
| [Git](https://git-scm.com/downloads/win) | Branches and pull requests |
| [Python 3.9+](https://www.python.org/downloads/windows/) | Data extraction, consistent save backups, the account tools |
| [Node.js](https://nodejs.org/en/download) | The bundle-decompression dependency |
| Your own standalone PoE **3.29.3b** client | A separate copy in the checkout's `Client` folder; Steam layout and newer clients are unsupported |

Frida (`pip install frida`) is optional: it is needed for walk maps, the console and live mod control, not for ordinary play.

1. Download `Waypoint.exe` from the latest release on this repository's **Releases** page.
2. Put it anywhere and start it.
3. On its first start the **setup guide** opens. It gets LocalExile to a folder you choose or finds the copy you have, finds
   your game client, copies and prepares it. You can skip it, and you can stop it at any point: what is finished stays
   finished, and the guide continues from there later (**Play → Finish setup**, or **Setup → Run setup guide** on a checkout).

The exe is not code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time: **More info** →
**Run anyway**.

Settings of an earlier Waypoint or LocalExile Launcher are taken over on the first start, and existing save backups stay
where they are. Neither old file is changed.

## Simple and Advanced modes

On a release install some sections do less, because a release has no source, Frida or per-service control: **Console** copies
commands for you to paste into the game's chat, **Mods** enables and disables built mods only, **Control** starts and stops
everything together, and **Tools** needs a source checkout. When a newer LocalExile release is out, a green card offers to install it.
Nothing changes until you accept and the game is closed. Your saves are copied first, and `data\` and `Client\` are kept.
**Settings → Keep LocalExile up to date** turns the prompt off.

A new player starts in **Simple mode**: **Play**, **Mods**, **Backups**, **Accounts**, **Logs**, **Console**, **Tools** and **Settings**. On a source checkout **Play** does
everything. It backs up your saves, gets the newest code at most once a day, builds it and starts the game. If an update doesn't
start, Waypoint goes back to the version you had and starts that. **Go back to the version you had** stays for a week after an
update, and **Settings → Keep LocalExile up to date** turns updates off. An update is the latest code, not a release; a bug
inside the game is what the way back is for.

A copy with anything of your own (another branch, a fork as `origin`, changes, your own commits) is never updated by itself.

**Settings → Developer → Developer Mode** adds a **Simple | Advanced** switch at the bottom of the sidebar: **Advanced** gives every section below, with
full control over branches and pull requests. Someone coming from an earlier launcher starts there.

## Sections

| Section | What it does |
| --- | --- |
| **Control** | Play, start and stop the servers and each service, with the switches of `live.ps1` and its output |
| **Setup** | What the checkout still needs, each requirement with its next step; the setup guide, which does all of it in one go |
| **Versions** | LocalExile's branches and pull requests and your own, how far you are from LocalExile, with every switch guarded; build, hot reload status, fresh saves |
| **Mods** | The checkout's mods: build, enable, disable, reload, live status |
| **Backups** | Consistent copies of the saves, made automatically before risky steps; restore, delete, saved mod choices |
| **Accounts** | Accounts and characters, read from the saves; register, create a character, duplicate, rename, set a password, delete |
| **Performance**, **Server timings** | Live CPU, memory and runtime counters; what each Gateway operation costs |
| **Logs** | Every server's log, the client's, Waypoint's own, and all errors in one place; a redacted diagnostic report |
| **Console** | The Gateway's chat commands for the character in game, with suggestions and pinned lines |
| **Tools** | The item crafter: any base, rarity, modifiers with their values, sockets and influences; saved items; give it to the character in game |
| **Settings** | Appearance and accessibility, language, what closing the window does, updates, the checkout |

**English and Russian** are in **Settings → Language**, and switch at once. **Settings → Appearance** has 19 color
schemes in light, dark and system modes, custom colors, reading fonts (OpenDyslexic included), text size, color-vision
profiles, stronger contrast and focus, and reduced motion: see [appearance](docs/appearance.md).

## Updates

A release build looks for a newer release when it starts (**Look for updates at start** in Settings turns that off;
**Check for updates** there looks at once). A newer one shows a green card at the bottom of the window: **Update now**
downloads the new exe, checks it against the release's SHA-256, puts it in place of the running one and restarts
Waypoint. The servers and the game keep running. **What's new** opens the release page.

## Build from source

```powershell
git clone <this repository> Waypoint
cd Waypoint
dotnet publish src/Waypoint -o .
.\Waypoint.exe
```

A build from source has no version and never offers updates. Everything else, from the sections to the code layout and
how a release is made, is in [docs/launcher.md](docs/launcher.md); what comes next is in [docs/roadmap.md](docs/roadmap.md).

Waypoint is under the [MIT license](LICENSE). OpenDyslexic is under the [SIL Open Font License](src/Waypoint/Fonts/OFL.txt).

This project is not affiliated with or endorsed by Grinding Gear Games.

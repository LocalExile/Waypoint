# LocalExile Launcher

A Windows desktop launcher for a [LocalExile](https://github.com/LocalExile/LocalExile) server checkout: start, stop
and restart the servers and the game client, watch the services and their performance, read every log, manage dev
accounts and send the Gateway's chat commands with suggestions.

It is a separate tool: it references no server project and drives a checkout only through what the server offers
outside tools (`scripts/dev/live.ps1`, the Gateway's `commands`/`values` modes, the dev host's protocol, the client
control bridge, the logs). Every start goes through `live.ps1`, so the server's rules apply unchanged.

## Install

Requirements: Windows 10/11 x64, the .NET 10 SDK (the server needs it anyway; it brings the Desktop Runtime the
launcher runs on), and a LocalExile server checkout set up as its
[GettingStarted](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md) describes.

1. Download `LocalExileLauncher.exe` from the latest release on this repository's **Releases** page.
2. Put it anywhere, for example in a folder beside your `LocalExile` checkout, and start it.
3. On its first start it asks for the server folder (the one with `GameServer.slnx`); it remembers it, and **Change…**
   on the Control tab picks another one.

The exe is not code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time: **More info** →
**Run anyway**.

## Updates

A release build looks for a newer release when it starts (**at start** on the Control tab turns that off; **Check for
updates** looks at once). A newer one shows a bar at the top: **Update now** downloads the new exe, checks it against
the release's SHA-256, puts it in place of the running one and restarts the launcher. The servers and the game keep
running. **What's new** opens the release page.

## Build from source

```powershell
git clone <this repository> LocalExile-Launcher
cd LocalExile-Launcher
dotnet publish src/GameServer.Launcher -o .
.\LocalExileLauncher.exe
```

A build from source has no version and never offers updates. Everything else, from the tabs to the code layout and
how a release is made, is in [docs/launcher.md](docs/launcher.md).

This project is not affiliated with or endorsed by Grinding Gear Games.

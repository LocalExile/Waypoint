# LocalExile Launcher

A Windows desktop launcher for a [LocalExile](https://github.com/LocalExile/LocalExile) server checkout: start, stop
and restart the servers and the game client, watch the services and their performance, read every log, manage dev
accounts and send the Gateway's chat commands with suggestions.

It is a separate tool: it references no server project and drives a checkout only through what the server offers
outside tools (`scripts/dev/live.ps1`, the Gateway's `commands`/`values` modes, the dev host's protocol, the client
control bridge, the logs). Every start goes through `live.ps1`, so the server's rules apply unchanged.

## Quick start

Requirements: Windows 10/11 x64, the .NET 10 SDK, and a LocalExile server checkout set up as its
[GettingStarted](https://github.com/LocalExile/LocalExile/blob/main/Agents/GettingStarted.md) describes.

```powershell
git clone <this repository> LocalExile-Launcher    # beside your LocalExile checkout
cd LocalExile-Launcher
dotnet publish src/GameServer.Launcher -o .
.\LocalExileLauncher.exe
```

On its first start the launcher asks for the server folder (the one with `GameServer.slnx`); it remembers it, and
**Change…** on the Control tab picks another one.

Everything else, from the tabs to the code layout, is in [docs/launcher.md](docs/launcher.md).

This project is not affiliated with or endorsed by Grinding Gear Games.

# Third-party notices

Waypoint is under the [MIT license](LICENSE). A released `Waypoint.exe` and `Waypoint-x86_64.AppImage` also carry the works
below, each under its own license. They are separate works: none of them changes Waypoint's license, and Waypoint's does not change theirs.

## DepotDownloader

- **What:** [DepotDownloader](https://github.com/SteamRE/DepotDownloader) 3.4.0, the program Waypoint runs to fetch a game
  client from Steam ([docs/launcher.md](docs/launcher.md), "The game client from Steam").
- **How it is carried:** the project's own release for Windows x64 (`DepotDownloader-windows-x64.zip`, SHA-256
  `41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767`), unchanged, inside `Waypoint.exe`, and its release for
  Linux x64 (`DepotDownloader-linux-x64.zip`, SHA-256 `a999dec66b4850fc961bd50366696d23c2d0fad7b18790e6a5647b2f19097a53`),
  unchanged, inside `Waypoint-x86_64.AppImage`. Waypoint unpacks it into its own data folder (`tools\DepotDownloader`), with
  the `LICENSE` file that comes in that zip, and runs it as a program of its own. Waypoint contains none of its code.
- **License:** GNU General Public License, version 2. Its full text is the `LICENSE` file beside the unpacked program, and
  <https://github.com/SteamRE/DepotDownloader/blob/DepotDownloader_3.4.0/LICENSE>.
- **Source:** the corresponding source is the tag
  [`DepotDownloader_3.4.0`](https://github.com/SteamRE/DepotDownloader/tree/DepotDownloader_3.4.0). Every Waypoint release
  that carries the program has that source attached beside `Waypoint.exe` and the AppImage, as
  `DepotDownloader-3.4.0-source.zip`.
- DepotDownloader is built on SteamKit2 (LGPL-2.1), protobuf-net and QRCoder, whose notices are in its own repository.

## OpenDyslexic

The reading font Waypoint offers, under the [SIL Open Font License](src/Waypoint/Fonts/OFL.txt).

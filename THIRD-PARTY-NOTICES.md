# Third-party notices

Waypoint is under the [MIT license](LICENSE). A released `Waypoint.exe` also carries the works below, each under its own
license. They are separate works: none of them changes Waypoint's license, and Waypoint's does not change theirs.

## DepotDownloader

- **What:** [DepotDownloader](https://github.com/SteamRE/DepotDownloader) 3.4.0, the program Waypoint runs to fetch a game
  client from Steam ([docs/launcher.md](docs/launcher.md), "The game client from Steam").
- **How it is carried:** the project's own release for Windows x64 (`DepotDownloader-windows-x64.zip`, SHA-256
  `41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767`), unchanged, inside `Waypoint.exe`. Waypoint unpacks it
  into its own data folder (`tools\DepotDownloader`), with the `LICENSE` file that comes in that zip, and runs it as a
  program of its own. Waypoint contains none of its code.
- **License:** GNU General Public License, version 2. Its full text is the `LICENSE` file beside the unpacked program, and
  <https://github.com/SteamRE/DepotDownloader/blob/DepotDownloader_3.4.0/LICENSE>.
- **Source:** the corresponding source is the tag
  [`DepotDownloader_3.4.0`](https://github.com/SteamRE/DepotDownloader/tree/DepotDownloader_3.4.0). Every Waypoint release
  that carries the program has that source attached beside `Waypoint.exe`, as `DepotDownloader-3.4.0-source.zip`.
- DepotDownloader is built on SteamKit2 (LGPL-2.1), protobuf-net and QRCoder, whose notices are in its own repository.

## OpenDyslexic

The reading font Waypoint offers, under the [SIL Open Font License](src/Waypoint/Fonts/OFL.txt).

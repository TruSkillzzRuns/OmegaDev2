# OmegaDev2

A Windows desktop companion tool for the 1.52 MHServerEmu fork. It talks to
your locally running server over its WebAPI — spawn and manage phantom hero
squads, browse and deliver items, run server commands, and watch logs, all
from one app.

Built with WinUI 3 / .NET 8. No game assets are included; everything you see
in the app comes from your own server and your own game client installation
at runtime.

## Tools

| Page | What it does |
| --- | --- |
| **Teleport Pad** | Pick any safe-warp region from the live server list and jump to it. |
| **Phantom Heroes** | Spawn AI hero bots that fight alongside a player — specific heroes or random squads, level / level-lock / costume per spawn, live roster with per-phantom costume and gear actions, saved squads. |
| **Squad Builder** | Design squads visually: click heroes into a lineup, set level, lock and costume per slot, save or spawn. |
| **Gear Picker** | Browse every item in the loaded game data with search, categories and a Unique section; build a basket with per-item count and rarity override and deliver it to any online player. |
| **Command Console** | Run any server command from the app. Set a player to execute client commands (`!phantom`, `!region`, ...) as that player. |
| **Log Viewer** | Live tail of the server log file with filtering and error/warning highlighting. |
| **Diagnostics** | At-a-glance server health — errors, warnings, uptime — plus the server URL setting. |
| **Asset Setup** | Two Browse buttons that configure item icons, hero portraits and real item names automatically (see below). |

## Requirements

- Windows 10 (1809+) or Windows 11, x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- A running MHServerEmu fork build that includes the OmegaDev2 WebAPI
  endpoints (`/webapi/items/*`, `/webapi/phantoms/*`, ...)

## Quick start

```
git clone <this repo>
cd OmegaDev2
dotnet build src/OmegaDev2/OmegaDev2.csproj -c Release
```

Run `OmegaDev2.exe` from the build output, start your server, and the pane
header shows `server: online`. The app talks to `http://localhost:8080` by
default — change it on the Diagnostics page if your server runs elsewhere.

## Optional: item icons, hero portraits and item names

Out of the box the app works fully but shows no pictures and uses data leaf
names for items. To enable the visual extras, open the **Asset Setup** page:

1. **Browse** to your server folder (the one with `MHServerEmu.exe`).
2. **Browse** to your game client install folder.
3. If the checklist says the extraction tools are missing: **Browse** to
   your server repo folder and click **Build Tools** — they're built from
   the repo's `tools/` sources and installed automatically.
4. Hit **Apply Setup**, then restart the server.

The page auto-discovers your client's texture folder and locale files,
updates the server's `Config.ini`, copies locale files, and builds the
one-time texture index (a few minutes). The checklist shows exactly what is
and isn't in place at every step.

Textures and strings are read from your local client files on demand and
cached as small PNGs on your machine. Nothing is redistributed.

<details>
<summary>Manual setup (what Apply does under the hood)</summary>

1. Sets `[ClientAssets] CookedPCConsolePath=<your client's CookedPCConsole
   folder>` in the server's `Config.ini`.
2. Copies your client's locale files into the server's `Data\Game\Loco`
   and sets `[GameData] LoadLocaleFiles=true`.
3. Runs `Tools\UpkExtract\UpkExtract.exe indexmeshes <CookedPCConsole path>
   Cache` to write `Cache/texIndex.json` next to the server executable.
</details>

## Notes

- The server's tool endpoints only answer loopback requests — the WebAPI is
  meant for a tool running on the same machine as the server.
- Phantom squads are saved server-side per account, so squads built here
  also work with the `!phantom squad` chat commands, and vice versa.

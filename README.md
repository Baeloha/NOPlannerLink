# NOPlannerLink

A BepInEx 5 mod for **Nuclear Option** that streams the running game to the **NO Mission Planner**
(https://nuclear-planner.vercel.app), so the planner's map follows the real battle:

- your aircraft moves live on the planner map (cyan), with a trail and a "follow me" option;
- **players on your team are green**, friendly AI is blue, and enemies are red;
- tanks, IFVs, SAMs, ships and other units appear at their real positions;
- enemy radars that paint you are drawn as yellow lines to your plane, like on the in-game map;
- missiles coming at you get a red line to your plane, and jamming is shown (JAM badges, purple lines);
- in Detected mode you can pick targets on the planner map and they are selected in your aircraft (enemies your side has detected only);
- the targets you lock in your aircraft are marked with a yellow reticle (primary target pulses);
- when a target is **destroyed** it disappears from the planner map and from route exposure checks;
- it works in 2D and 3D.

The mod only reads the game, with one exception you trigger yourself: selecting targets for your own aircraft from the planner (Detected mode only, the same targets you could click on the in-game map). It opens a WebSocket on `127.0.0.1:8787` (your
own PC only), and the planner in your browser connects to it.

## Install

**With NOMM (Nuclear Option Mod Manager):** search for *NOPlannerLink* and install it.

**By hand:** download `NOPlannerLink.dll` from the latest
[release](../../releases/latest) and put it in
`Nuclear Option/BepInEx/plugins/NOPlannerLink/`. BepInEx 5 must be installed.

Every player who wants the live map installs the mod on their own PC.

## Use

1. Start Nuclear Option. `BepInEx/LogOutput.log` should contain `NOPlannerLink listening on ws://localhost:8787`.
2. Open https://nuclear-planner.vercel.app in Chrome or Edge **on the same PC** (a second monitor works well), and load your mission.
3. Open the **Live** tab and click **Connect to game**. From then on, the planner reconnects by itself.

## Intel: "Detected" vs "Everything"

| Mode | What you see |
|---|---|
| **Detected** (default; "My side" before 1.1) | Your faction's units, neutral units, and enemies your faction has detected, at their last known position. This is the same information as the in-game map. |
| **Everything** | Every unit in the game. |

"Everything" is **host-gated**, so the mod can't be used as a wallhack:

- In **single-player** it's always available.
- As the **host**, set `Intel → AllowEverything = true` in the config (press F1 with BepInEx.ConfigurationManager, or edit `BepInEx/config/NOPlannerLink.cfg`). Players in your game who run the mod may then select it.
- As a **client**, it's available only when the host runs NOPlannerLink with `AllowEverything = true`. The host's mod tells modded clients this over a small network message. Unmodded players are never sent anything.

## Config (`BepInEx/config/NOPlannerLink.cfg`)

| Key | Default | Meaning |
|---|---|---|
| General.Enabled | true | Run the link server |
| General.Port | 8787 | Local WebSocket port |
| General.UpdateRateHz | 4 | Position updates per second |
| Intel.Default | Side | Starting intel mode (Side / All) |
| Intel.AllowEverything | false | Host setting, see above |
| Security.AllowedOrigins | planner + localhost | Web pages allowed to connect. Other websites can't read your game state. |

## Protocol

JSON over WebSocket: `hello`, `snapshot`, `delta` and `event` messages; the planner sends `setIntel` and `resync`.
The full spec is in the planner repository, `docs/live-protocol.md`.

## Build from source

You need the .NET SDK (8+) and a copy of the game's `NuclearOption_Data/Managed` plus `BepInEx/core`. The game DLLs are not in this repository.

```bash
GAME_DIR="/path/to/Nuclear Option" ./build.sh      # → dist/NOPlannerLink.dll (+ sha256), runs the tests
```

- `LinkCore/` holds the protocol, JSON writer and WebSocket server. It has no Unity dependencies and is compiled into the plugin as source.
- `NOPlannerLink/` is the BepInEx plugin: `Plugin.cs` (config, update loop), `GameWorld.cs` (reads `UnitRegistry` and the faction tracking database), `HostGate.cs` (host permission for "Everything").
- `LinkCore.Tests/` contains unit tests and a real WebSocket end-to-end test (`dotnet test LinkCore.Tests`).
- `LinkHarness/` is a fake game for testing the planner without Nuclear Option:
  `dotnet run --project LinkHarness -- path/to/mission.json --faction Boscali [--allow-all]`

## AI assistance

This mod was written with substantial AI assistance (Claude), as declared in the NOMNOM manifest
(`isAiAssisted: "True"`). All source is in this repository; the release DLL is built from it by `build.sh`.

## License

MIT, see [LICENSE](LICENSE). Nuclear Option is a game by Shockfront Studios; this project is not affiliated with them.

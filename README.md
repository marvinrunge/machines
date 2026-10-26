# Machines Remake

A ground-up remake of **Machines: Wired for War** (Charybdis / Acclaim, 1999) in Godot 4 with C#.
The original game rules are imported from the GPLv3 data files of the original source release.

Design doc: [Machines Remake — Game Design Document](https://claude.ai/artifact/HaZzuN2abszMB1ytkbdQww)

## Status

- **M0 · Foundation: complete.** Every one of the 362 original data files has an importer and loads.
- **M1 · Vertical slice: complete and play-tested.** Red against Blue's original AI on `m_desert`: locate,
  mine, haul, bank, build, produce, research and fight with the original numbers, and drive any machine in
  first person.
- **M2 · Full roster and all maps: complete; v0.1.0 is the first test release.**
  - A skirmish menu with all 32 skirmish and multiplayer maps, under their original names, with planet
    previews and descriptions from your game copy. It offers the original settings: players, colour, fixed or
    random starts, starting BMUs, mineral resources, technology level, and annihilation, pod or timed victory.
    Timed games go to the original score.
  - Area weapons with the original radii: vortex, bee bomb, gorilla punch, ion cannon and nuclear missile.
    Pods arm their ion cannon once it is researched.
  - Support work: healing, the orb of treachery, repair, capture, deconstruction, recycling, garrisons and
    self-destruct.
  - The original terrain on every skirmish planet, loaded from your game copy by the classic exporter
    (without it, a heightfield with stand-in rock). Sun, sky and fog come from each planet's `.env` and
    follow its day.
- **Polish round before M3: playable, waiting for its play-test.**
  - Sound from your game copy: weapons, hits, explosions, blasts and engine loops by machine type, as the
    original chose them, and the voice reports of your own machines (selected, moving, built, destroyed ...).
  - Animation: machines move their parts as exported from the original engine. Spider legs play their
    walking, turning and resting cycles, wheels roll, track treads scroll, and upper bodies turn to their
    target.
  - Transport by APC: machines board by volume within the APC's capacity, ride along and deploy at a point;
    passengers die with their APC, and an APC near destruction lets them out. Buildings let in only machines
    small enough for their doors.
  - The AI builds as the original did beyond its script: a prioritised list with mines on every found site
    first and its destroyed REBUILD buildings again.
  - Race colours on buildings and machines: textures take their material's colour, and the original's
    `logos.dat` textures switch to the owner's race.
- **M3 · Multiplayer: playable over the network, waiting for its play-test.**
  - Deterministic lockstep: every client runs the same simulation from the same seed and exchanges only
    commands, one turn per tick with four ticks of input delay. The simulation uses its own trigonometry
    (`DetMath`) so every machine computes the same floats, and clients report world checksums the relay
    compares; on a mismatch every player sees the tick the games diverged at.
  - A relay (`server/Machines.Relay`) with rooms. **Host** under **Multiplayer…** runs it inside the game;
    it also runs on its own. Lobby: open a room on the map and settings of the skirmish menu, join, pick a
    colour, get ready; the host starts, and free slots can play the AI.
  - Replays: every match, single player too, is recorded as its commands to `replays/`; **Watch a replay…**
    plays one back to the identical checksum.
  - Not yet: rejoining after a dropped connection, and an AI that takes over a dropped player's machines.
- 187 tests. Every map runs AI-only for three minutes, and every map whose scripts give orders gets moving.

## Layout

| Folder | Holds |
| --- | --- |
| `import/Machines.Import` | Readers for every original data format: stats, research, factories, scenarios, AI scripts, menus, strings, planets, sounds |
| `sim/Machines.Sim` | The simulation core, plain C# with no Godot types: world, pathfinding on the original obstacles, economy, construction, production, research, combat, the original AI scripts |
| `net/Machines.Net` | Multiplayer messages and framing, the lockstep session and replays |
| `server/Machines.Relay` | The multiplayer relay: rooms, turn forwarding, checksum comparison (`--port`, default 7777) |
| `tests/Machines.Tests` | xUnit tests that pin the importers and simulation to the 1999 numbers |
| `game/` | The Godot 4 client: terrain, units, RTS camera, selection and orders, build menus, HUD, minimap, first person |
| `tools/classic-export` | Exports the original models from your own copy of the game (see its README) |
| `data/original` | Original design data, copied unchanged (see `data/README.md`) |
| `classic/` | Local-only (git-ignored): models and planet terrain exported from your game copy |
| `references/` | Local-only (git-ignored): OpenMachines mod, fan Blender models, screenshots, manual scans |

## Requirements

- .NET 10 SDK
- Godot 4.7.2, **.NET edition** (the standard edition cannot run C# scripts)
- Optional: your own copy of the game and the classic exporter, for the original models and textures.
  Without them the client shows simple race-coloured shapes.

## Build, test, play

```bash
dotnet test Machines.sln
```

Open `game/project.godot` in the Godot .NET editor and press Play. Godot builds `Machines.sln` from the
repository root (`dotnet/project/solution_directory`). The game opens on the skirmish menu: pick a map
(double click starts it) and settings, then Start.

For automated checks, `MACHINES_MAP=<scenario>,<planet>` skips the menu, and `MACHINES_SCREENSHOT=<file.png>`
saves a screenshot and quits (see `Game.cs` and `SkirmishMenu.cs` for the other `MACHINES_*` switches).

| Control | Does |
| --- | --- |
| Left click / drag | Select (Shift adds) |
| Right click | Move; attack an enemy; build an unfinished site, repair a damaged building or capture an enemy one (constructors); heal a damaged machine (administrators with superchargers); haul from a mine (transporters); enter a lab (technicians), a garrison or one of your APCs; locate (locators) |
| Buttons (machines selected) | Recycle at the nearest smelter or pod; self-destruct (or cancel it); deconstruct a building (constructors); deploy an APC's passengers at a point |
| Buttons (pod or nuclear silo selected) | Fire the ion cannon or launch the missile once charged, then left click the target |
| Ctrl + right click | Attack-move |
| Build buttons (constructors selected) | Place a building: R rotates, right click cancels, Shift keeps placing |
| Factory / lab selected | Buttons to build machines or start research |
| W A S D, edges, Q / E, wheel | Pan, rotate, zoom |
| F | Drive the selected machine in first person: W/S drive, A/D or mouse turn, left mouse fires |
| Ctrl+S, P, Esc | Stop, pause (not online), clear selection |
| F10 | Back to the skirmish menu (online: leaves the match; the replay is saved) |

## Multiplayer

One player presses **Multiplayer…** and **Host**: the game runs the relay on port 7777 and shows the
addresses the others connect to. The others type that address and **Connect**. Over the internet, the host
forwards TCP 7777 to their computer, or everyone joins a VPN such as Tailscale. A dedicated relay runs with

```bash
dotnet run --project server/Machines.Relay -- --port 7777
```

For automated checks, `MACHINES_MP=host,<address>` (or `serve` to host the relay in the game, `join` to join
the first room) drives the lobby, and `MACHINES_CHECKSUM_AT=<tick>` prints the world checksum at that tick.
`MACHINES_REPLAY=<file.mreplay>` watches a replay.

## Releases

GitHub Actions (`.github/workflows/build.yml`) runs the tests on every push and pull request, builds the
classic exporter against the original engine, and exports a Windows build. Pushing a tag such as `v0.1.0`
publishes that build as a pre-release (notes in `tools/release/NOTES.md`).

The zip holds the game, `data/original` and the exporter with its DLLs, but nothing from the original game.
Players who own it press **Import from your game copy…** in the menu; the import writes `classic/` next
to `Machines.exe`, as `tools/classic-export/run.sh` does in the repository.

A local build needs the Godot 4.7.2 .NET export templates:

```bash
tools/release/build-windows.sh <Godot console exe> 0.1.0 <folder made by tools/classic-export/package.sh>
```

## Licence

GPLv3, see `LICENSE.md`. This matches the licence of the original source and data release.
No original art, sound or video is included; the game's name and trademarks belong to their owners.

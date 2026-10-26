# Machines Remake

A ground-up remake of **Machines: Wired for War** (Charybdis / Acclaim, 1999) in Godot 4 with C#.
The original game rules are imported from the GPLv3 data files of the original source release.

Design doc: [Machines Remake — Game Design Document](https://claude.ai/artifact/HaZzuN2abszMB1ytkbdQww)

## Status

- **M0 · Foundation: complete.** Every one of the 362 original data files has an importer and loads.
- **M1 · Vertical slice: complete and play-tested.** Red against Blue's original AI on `m_desert`: locate,
  mine, haul, bank, build, produce, research and fight with the original numbers, and drive any machine in
  first person.
- **M2 · Full roster and all maps: playable, waiting for its play-test.**
  - A skirmish menu with all 32 skirmish and multiplayer maps, under their original names, with planet
    previews and descriptions from your game copy. It offers the original settings: players, colour, fixed or
    random starts, starting BMUs, mineral resources, technology level, and annihilation, pod or timed victory.
    Timed games go to the original score.
  - Area weapons with the original radii: vortex, bee bomb, gorilla punch, ion cannon and nuclear missile.
    Pods arm their ion cannon once it is researched.
  - Support work: healing, the orb of treachery, repair, capture, deconstruction, recycling, garrisons and
    self-destruct.
  - Ground heights from the planet tiles, and sun, sky and fog from each planet's `.env`, following its
    day.
  - 170 tests. Every map runs AI-only for three minutes, and every map whose scripts give orders gets moving.

## Layout

| Folder | Holds |
| --- | --- |
| `import/Machines.Import` | Readers for every original data format: stats, research, factories, scenarios, AI scripts, menus, strings, planets, sounds |
| `sim/Machines.Sim` | The simulation core, plain C# with no Godot types: world, pathfinding on the original obstacles, economy, construction, production, research, combat, the original AI scripts |
| `tests/Machines.Tests` | xUnit tests that pin the importers and simulation to the 1999 numbers |
| `game/` | The Godot 4 client: terrain, units, RTS camera, selection and orders, build menus, HUD, minimap, first person |
| `tools/classic-export` | Exports the original models from your own copy of the game (see its README) |
| `data/original` | Original design data, copied unchanged (see `data/README.md`) |
| `classic/` | Local-only (git-ignored): models exported from your game copy |
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
| Right click | Move; attack an enemy; build an unfinished site, repair a damaged building or capture an enemy one (constructors); heal a damaged machine (administrators with superchargers); haul from a mine (transporters); enter a lab (technicians) or a garrison; locate (locators) |
| Buttons (machines selected) | Recycle at the nearest smelter or pod; self-destruct (or cancel it); deconstruct a building (constructors) |
| Buttons (pod or nuclear silo selected) | Fire the ion cannon or launch the missile once charged, then left click the target |
| Ctrl + right click | Attack-move |
| Build buttons (constructors selected) | Place a building: R rotates, right click cancels, Shift keeps placing |
| Factory / lab selected | Buttons to build machines or start research |
| W A S D, edges, Q / E, wheel | Pan, rotate, zoom |
| F | Drive the selected machine in first person: W/S drive, A/D or mouse turn, left mouse fires |
| Ctrl+S, P, Esc | Stop, pause, clear selection |
| F10 | Back to the skirmish menu |

## Licence

GPLv3, see `LICENSE.md`. This matches the licence of the original source and data release.
No original art, sound or video is included; the game's name and trademarks belong to their owners.

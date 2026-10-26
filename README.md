# Machines Remake

A ground-up remake of **Machines: Wired for War** (Charybdis / Acclaim, 1999) in Godot 4 with C#.
The original game rules are imported from the GPLv3 data files of the original source release.

Design doc: [Machines Remake — Game Design Document](https://claude.ai/artifact/HaZzuN2abszMB1ytkbdQww)

## Status

**M0 · Foundation is complete.** Every one of the 362 original data files has an importer and loads
(`DataCoverageTests`), and 99 tests pin the importers and the armour rule to the 1999 values. Next is
**M1 · Vertical slice**: a playable 1v1 skirmish on the desert planet.

## Layout

| Folder | Holds |
| --- | --- |
| `import/Machines.Import` | Readers for every original data format: stats, research, factories, scenarios, AI scripts, menus, strings, planets, sounds |
| `sim/Machines.Sim` | The simulation core, plain C# with no Godot types. Today: the original armour/damage rule |
| `tests/Machines.Tests` | xUnit tests that pin the importer and simulation to the 1999 numbers |
| `game/` | The Godot 4 project. Its start scene loads `parmdata.dat` and shows a summary |
| `data/original` | Original design data, copied unchanged (see `data/README.md`) |
| `references/` | Local-only reference material (git-ignored): OpenMachines mod, fan Blender models, screenshots, manual scans |

## Requirements

- .NET 10 SDK
- Godot 4.7.2, **.NET edition** (the standard edition cannot run C# scripts)

## Build and test

```bash
dotnet test Machines.sln
```

To run the game, open `game/project.godot` in the Godot .NET editor and press Play.
Godot builds `Machines.sln` from the repository root (`dotnet/project/solution_directory`).

## Licence

GPLv3, see `LICENSE.md`. This matches the licence of the original source and data release.
No original art, sound or video is included; the game's name and trademarks belong to their owners.

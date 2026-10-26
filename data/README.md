# Original game data

`original/` holds the design data of Machines: Wired for War (1999), copied unchanged from the
GPLv3 source release (`nightdivestudio/machines`, commit `2c857564ac9145fa8913ed57e533a5069faf7662`).
The release was made with the permission of Night Dive Studios, who own the game. These files are GPLv3;
see `../LICENSE.md`.

| Copied from the release | To |
| --- | --- |
| `data/` (all text data, no shaders) | `original/` |
| `models/planet/` | `original/models/planet/` |
| `sounds/*.dat`, `sounds/vemail/vemail.dat` | `original/sounds/` |

No original art, sound or video is included. Those are not part of the source release: the planet tiles,
meshes, sounds and videos these files name only exist in a purchased copy of the game.

Every file here has an importer in `import/Machines.Import`, and `DataCoverageTests` checks that all
362 of them load.

| Files | Contents | Importer |
| --- | --- | --- |
| `parmdata.dat` | Stats of every machine, building and weapon, evasion strategies, general data | `Parm.ParmDataParser` |
| `global.rst` | Research tree | `Research.ResearchTreeParser` |
| `global.cst` | Construction order for constructors | `Research.ConstructionOrderParser` |
| `factory.bld` | What each factory type and level can build | `Research.FactoryBuildListParser` |
| `*.scn` | Scenarios: starting units, mineral sites, artefacts, triggers | `Scenarios.ScenarioParser` |
| `*.spl` | AI race scripts: production lists and squads | `Scenarios.AiScriptParser` |
| `*.rsi` | Research available at the start of a scenario | `Research.ScenarioResearchParser` |
| `sceninfo.dat` | Campaign, skirmish and multiplayer menus | `Scenarios.ScenarioCatalogueParser` |
| `machstrg.xml` | UI strings and in-game names | `Text.StringTableParser` |
| `models/planet/*/*.psf` | Planet surface: tile grid, tile rotations and heights | `Planets.SurfaceParser` |
| `models/planet/*/*.csp` | Pathfinding: domains, portals, obstacles | `Planets.ConfigSpaceParser` |
| `models/planet/*/*.env` | Sky, sun and moon orbits, colour tables, fog | `Planets.EnvironmentParser` |
| `sounds/snddef*.dat`, `sounds/Sd2d*.dat` | Sound effects for 3D and 2D sound | `Audio.SoundDefinitionParser` |
| `sounds/vemail/vemail.dat` | Voice-mail reports | `Audio.VoiceMailParser` |
| `zenith.dat`, `stats.ini`, `simstats.ini` | Camera limits, update priorities, timing | `Parm.SettingsParser` |
| `logos.dat` | Textures recoloured per race | `Parm.FileListParser` |

## Known quirks in the original data

The importers keep these as they are; the tests list them so new ones stand out.

Research and factories:

- `m_1o2.rsi` line 103 enables `CONSTRUCTOR 2 SUB BUILDER`, but the Builder only exists at level 3.
  The original lookup asserts on it. The scenario loader must skip it.
- `tut03.rsi` line 9 makes the Dozer available but unresearched, and its research cost is 0,
  so researching it finishes at once.
- `global.rst`: the original applies `FACTORY_COST` only when the amount is above zero, so the four
  negative `RELATIVE` adjustments (medic and surgeon administrators) have no effect.
- `fortseig.rsi` lines 46–47 start with `Med tech` and are ignored, as in the original.

Scenarios:

- The original reads numbers with C's `atoi`/`atof`, which never fail. So `0T` (`m_contrl.scn` 116)
  reads as 0. In `multidog.scn` lines 34 and 40, `MEDIUM`/`HIGH` land in the height slot of a `SITE` line that
  lacks its height, so they read as 0 and the sites count for every resource setting. The importer copies this
  behaviour and lists each case in `Scenario.Warnings`.
- Building flags only count when spelt exactly (`REBUILD`, `NOT_BUILT`). The 13 misspellings
  (`REBUILT`, `REBUILD2`, `REBUILDY`) silently mean "no flag" and are listed in `UnknownWords`.
- `deepspac.scn` writes `SET_SCENARIO_FLAG NAME x` instead of `ON x`; the original never checks that word.
- `tut02.scn` and `tut03.scn` close the RED block twice, so the assembly points after it never load.
- `guns.scn` line 7 puts a mineral site at y = 1075, but the `guns` planet is 600 × 1000 m. That ore can
  never be reached.

AI scripts:

- The `N_MINES`, `N_CONSTRUCTORS`, `N_GEO_LOCATORS` and `N_RESOURCE_CARRIERS` lines of every production list
  are ignored by the original (that code is commented out); only `NUMBER` lines count.
- `control.spl` lines 15, 21, 27: `AI PATROL ... DO 4` — the original reads `DO 4` as one more patrol point,
  (0, 4), so those Gorillas walk to the map corner.
- `m_1o2R.spl` line 108 and `m_greenR.spl` line 148 start a `SQUAD` without closing the previous one. The
  original merges the two squads, which shifts the number of every later squad.
- Misspelt or misplaced AI words (`D0`, `DO1`, `DO4`, `RANGE` for `RANGE_POD`, options on `AI CONSTRUCT`) are
  skipped, as in the original, and listed in `AiScript.Warnings`.
- `deswar.spl` line 73 orders a Demon Warlord (`COMMANDER 5 L_PULSE_CANNON_R_VIRUS`), which has no research item.
  The original asserts on this; `deswar.scn`, its only user, is not in the game menus.

Text and sound:

- `machstrg.xml` declares UTF-8 but holds single bytes 0x80–0xFF, which are glyphs in the game's own font
  (0x81 stands before every BMU amount, so it is most likely the BMU sign). The importer reads the file
  byte for byte and keeps them as U+0080–U+00FF.
- `Sd2d64.dat` line 106: `SID_CONTROL` is marked `3D` in the 2D sound set.

Do not edit these files. Balance changes for the remake go in separate override files,
so the original values stay available as "classic rules".

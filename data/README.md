# Original game data

`original/` holds the design data of Machines: Wired for War (1999), copied unchanged from the
GPLv3 source release (`nightdivestudio/machines`, commit `2c857564ac9145fa8913ed57e533a5069faf7662`,
folder `data/`). The release was made with the permission of Night Dive Studios, who own the game.
These files are GPLv3; see `../LICENSE.md`.

No original art, sound or video is included. Those are not part of the source release.

| Files | Contents | Importer |
| --- | --- | --- |
| `parmdata.dat` | Stats of every machine, building and weapon, evasion strategies, general data | `Machines.Import.Parm.ParmDataParser` |
| `global.rst` | Research tree | `Machines.Import.Research.ResearchTreeParser` |
| `global.cst` | Construction order for constructors | `Machines.Import.Research.ConstructionOrderParser` |
| `*.scn` | Scenarios: starting units, mineral sites, triggers | not yet |
| `*.spl` | AI race scripts: production lists and squads | not yet |
| `*.rsi` | Research available at the start of a scenario | `Machines.Import.Research.ScenarioResearchParser` |
| `sceninfo.dat` | Campaign, skirmish and multiplayer menus | not yet |
| `machstrg.xml` | UI strings and in-game names | not yet |

## Known quirks in the original data

The importers keep these as they are; the tests list them so new ones stand out.

- `m_1o2.rsi` line 103 enables `CONSTRUCTOR 2 SUB BUILDER`, but the Builder only exists at level 3.
  The original lookup asserts on it. The scenario loader must skip it.
- `tut03.rsi` line 9 makes the Dozer available but unresearched, and its research cost is 0,
  so researching it finishes at once.
- `global.rst`: the original applies `FACTORY_COST` only when the amount is above zero, so the four
  negative `RELATIVE` adjustments (medic and surgeon administrators) have no effect.
- `fortseig.rsi` lines 46–47 start with `Med tech` and are ignored, as in the original.

Do not edit these files. Balance changes for the remake go in separate override files,
so the original values stay available as "classic rules".

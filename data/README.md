# Original game data

`original/` holds the design data of Machines: Wired for War (1999), copied unchanged from the
GPLv3 source release (`nightdivestudio/machines`, commit `2c857564ac9145fa8913ed57e533a5069faf7662`,
folder `data/`). The release was made with the permission of Night Dive Studios, who own the game.
These files are GPLv3; see `../LICENSE.md`.

No original art, sound or video is included. Those are not part of the source release.

| Files | Contents | Importer |
| --- | --- | --- |
| `parmdata.dat` | Stats of every machine, building and weapon, evasion strategies, general data | `Machines.Import.Parm.ParmDataParser` |
| `global.rst` | Research tree | not yet |
| `global.cst` | Construction order for constructors | not yet |
| `*.scn` | Scenarios: starting units, mineral sites, triggers | not yet |
| `*.spl` | AI race scripts: production lists and squads | not yet |
| `*.rsi` | Research available at the start of a scenario | not yet |
| `sceninfo.dat` | Campaign, skirmish and multiplayer menus | not yet |
| `machstrg.xml` | UI strings and in-game names | not yet |

Do not edit these files. Balance changes for the remake go in separate override files,
so the original values stay available as "classic rules".

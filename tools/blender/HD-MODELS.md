# HD models: what is rebuilt in Blender and what is left

The HD look takes each model from the first of these that has it (`ClassicAssets.ModelPath`):

1. `classic/built/models/<colour>/`: rebuilt in Blender from the overhauled parts (harness, style and blueprints in
   `renders/hd-plan/probe`: `STYLE.md`, `tools/mach.py`, `bake.py`, `assemble.py`, `export_game.py`).
2. `classic/meshy/models/<colour>/`: the Meshy remakes fitted into the original rigs (`tools/blender/meshy_rig.py`).
3. The overhauled model, then the original.

The model page (main menu, Models) says which one the HD look shows. Both folders are local only, like the rest of
`classic/`. `ls classic/built/models/red` lists what is built; tick a line here when a machine lands there.

**Built: 1 of 65 models (1 of 80 files).** The files name the original's models with their weapons; a line is
done when every file on it is built.

## Machines

| Done | Model | Original files | Weapons | Notes |
|---|---|---|---|---|
| [x] | Gorilla 5 | `AGGRESSOR_5_NINJA_…` | multi-launcher 7 ×2, punch | built 2026-11-07, about 6,040 triangles; open choices in `STYLE.md` (elbow as the original's, hands, wrist, pods silver) |
| [ ] | Grunt 1 | `AGGRESSOR_1_GRUNT_…` (2) | bolter, electric charge | |
| [ ] | Grunt 2 | `AGGRESSOR_2_GRUNT_…` (3) | bolter, electric charge, flame thrower 2 | |
| [ ] | Grunt 3 | `AGGRESSOR_3_GRUNT_…` | heavy bolter 1 ×2 | |
| [ ] | Reaper 2 | `AGGRESSOR_2_ASSASSIN_…` (2) | auto cannon, plasma rifle | |
| [ ] | Reaper 3 | `AGGRESSOR_3_ASSASSIN_…` (2) | auto cannon ×2, plasma rifle ×2 | |
| [ ] | Knight 3 | `AGGRESSOR_3_KNIGHT_…` (2) | heavy bolter 2 ×2, plasma cannon 1 ×2 | |
| [ ] | Knight 4 | `AGGRESSOR_4_KNIGHT_…` (2) | multi-launcher 3 ×2, plasma cannon 2 ×2 | |
| [ ] | Knight 5 | `AGGRESSOR_5_KNIGHT_…` (2) | multi-launcher 4 ×3, plasma cannon 2 ×3 | |
| [ ] | Ballista 3 | `AGGRESSOR_3_BALLISTA_…` (2) | multi-launcher 1, vortex | |
| [ ] | Ballista 4 | `AGGRESSOR_4_BALLISTA_…` | multi-launcher 2 ×2 | |
| [ ] | Boss 1 | `ADMINISTRATOR_1_BOSS_…` (2) | wasp light sting, wasp metal sting | |
| [ ] | Boss 2 | `ADMINISTRATOR_2_BOSS_…` | bee bomb | |
| [ ] | Overseer 2 | `ADMINISTRATOR_2_OVERSEER_…` (2) | pulse rifle ×2, pulse rifle and supercharge | |
| [ ] | Overseer 3 | `ADMINISTRATOR_3_OVERSEER_…` (2) | pulse rifle ×2, pulse rifle and supercharge | |
| [ ] | Commander 4 | `ADMINISTRATOR_4_COMMANDER_…` (3) | multi-launcher 5 ×2, with supercharge, with treachery | |
| [ ] | Commander 5 | `ADMINISTRATOR_5_COMMANDER_…` (2) | pulse cannon ×2, pulse cannon and supercharge | |
| [ ] | Dozer 1 | `CONSTRUCTOR_1_DOZER` | | |
| [ ] | Builder 3 | `CONSTRUCTOR_3_BUILDER` | | |
| [ ] | Behemoth 4 | `CONSTRUCTOR_4_BEHEMOTH` | | |
| [ ] | Behemoth 5 | `CONSTRUCTOR_5_BEHEMOTH` | | |
| [ ] | Lab Tech 2 | `TECHNICIAN_2_LAB_TECH` | | |
| [ ] | Tech Boy 3 | `TECHNICIAN_3_TECH_BOY` | | |
| [ ] | Brain Box 5 | `TECHNICIAN_5_BRAIN_BOX` | | |
| [ ] | Geo locator 1 | `GEO_LOCATOR_1` | | |
| [ ] | Geo locator 2 | `GEO_LOCATOR_2` | | |
| [ ] | Geo locator 3 | `GEO_LOCATOR_3` | | |
| [ ] | Spy locator 3 | `SPY_LOCATOR_3` | | |
| [ ] | Spy locator 5 | `SPY_LOCATOR_5` | | |
| [ ] | Resource carrier 1 | `RESOURCE_CARRIER_1` | | |
| [ ] | Resource carrier 2 | `RESOURCE_CARRIER_2` | | |
| [ ] | Resource carrier 3 | `RESOURCE_CARRIER_3` | | |
| [ ] | Resource carrier 4 | `RESOURCE_CARRIER_4` | | |
| [ ] | Resource carrier 5 | `RESOURCE_CARRIER_5` | | |
| [ ] | APC 2 | `APC_2` | | |
| [ ] | APC 4 | `APC_4` | | |
| [ ] | APC 5 | `APC_5` | | |

## Buildings and emplacements

| Done | Model | Original files | Weapons | Notes |
|---|---|---|---|---|
| [ ] | Pod | `POD_1_T_ION_ORBITAL_CANNON` | ion cannon | |
| [ ] | Smelter 1 | `SMELTER_1` | | |
| [ ] | Smelter 3 | `SMELTER_3` | | |
| [ ] | Mine 1 | `MINE_1` | | |
| [ ] | Mine 3 | `MINE_3` | | |
| [ ] | Mine 5 | `MINE_5` | | |
| [ ] | Civilian factory 1 | `FACTORY_1_CIVILIAN` | | |
| [ ] | Civilian factory 3 | `FACTORY_3_CIVILIAN` | | |
| [ ] | Civilian factory 5 | `FACTORY_5_CIVILIAN` | | |
| [ ] | Military factory 1 | `FACTORY_1_MILITARY` | | |
| [ ] | Military factory 3 | `FACTORY_3_MILITARY` | | |
| [ ] | Military factory 4 | `FACTORY_4_MILITARY` | | |
| [ ] | Military factory 5 | `FACTORY_5_MILITARY` | | |
| [ ] | Civilian lab 1 | `HARDWARE_LAB_1_CIVILIAN` | | |
| [ ] | Civilian lab 3 | `HARDWARE_LAB_3_CIVILIAN` | | |
| [ ] | Military lab 1 | `HARDWARE_LAB_1_MILITARY` | | |
| [ ] | Military lab 3 | `HARDWARE_LAB_3_MILITARY` | | |
| [ ] | Garrison 1 | `GARRISON_1` | | |
| [ ] | Beacon 1 | `BEACON_1` | | |
| [ ] | Beacon 3 | `BEACON_3` | | |
| [ ] | Turret 1 | `MISSILE_EMPLACEMENT_1_TURRET_…` | flame thrower 1 | |
| [ ] | Turret 2 | `MISSILE_EMPLACEMENT_2_TURRET_…` | auto cannon ×2 | |
| [ ] | Turret 3 | `MISSILE_EMPLACEMENT_3_TURRET_…` | pulse rifle ×2 | |
| [ ] | Sentry 3 | `MISSILE_EMPLACEMENT_3_SENTRY_…` | multi-launcher 5 ×2 | |
| [ ] | Sentry 4 | `MISSILE_EMPLACEMENT_4_SENTRY_…` | multi-launcher 6 ×2 | |
| [ ] | Launcher 4 | `MISSILE_EMPLACEMENT_4_LAUNCHER_…` | large missile ×2 | |
| [ ] | ICBM 5 | `MISSILE_EMPLACEMENT_5_ICBM_…` | nuclear missile | silo dome opens (format 7) |
| [ ] | Ore holograph | `ORE_HOLOGRAPH_1` | | no Meshy remake either; HD shows it overhauled |

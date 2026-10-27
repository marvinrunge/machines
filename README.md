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
  - Dropping out: a player who leaves or loses the connection hands their race to the original AI from the
    tick of their first missing turn. Reconnecting under the same name and pressing **Join** on the running
    match rebuilds it from the relay's log, catches up in fast-forward and takes the race back from the AI.
    Replays record who left and came back.
  - Performance gate: four players with 200 armed machines each fighting on m_green run at 1.7 ms per tick
    on average (p99 12 ms; the budget is 50 ms), measured with `tools/sim-bench`. Path searches share work
    for group orders, know which areas are cut off, and enemies are found through a spatial grid; a machine
    on an attack-move whose target falls takes on the next enemy at once instead of planning its way on.
- **Remake graphics (phase A):** AgX tone mapping, glow, ambient occlusion, soft shadows that follow the
  camera, 16× anisotropic texture filtering and slightly glossy machines; **Remake graphics** in the menu
  switches back to the classic look. The control panel follows the original's layout with its icons.
- **Remake graphics (phase B):** glowing bolts that fly at the weapon's speed with muzzle flashes and sparks;
  explosions of fire, smoke and debris with a flash of light; scorch marks that fade after a minute; dust
  behind moving machines; area weapons glow; selection rings lie on the ground.
- **Remake graphics (phase C):** the moon lights the night as in the original (it was pitch black), with
  stars and moon discs in the sky; buildings light their surroundings and machines switch on headlights
  after dark; water ripples, glints and mirrors its surroundings, waterfalls flow.
- **Remake graphics (phase D, HD textures):** the import ends by enlarging every original texture four
  times with Real-ESRGAN on your GPU (fetched by `tools/texture-upscale/fetch.sh`; Lanczos without it) and
  deriving normal and roughness maps from them, so panel lines, rivets, cracks and grain catch the light.
  They stay in `classic/hd` on your machine. Already imported? **Import again** makes them.
- **Weapons at work and closed buildings:** guns recoil with every round by the original's distances and
  times, and the autocannon turns its barrels on by one per round. Buildings no longer show their interiors
  from outside: as in the original, an interior appears only to first person looking out from inside it.
  The import format is now 6: **Import again** after updating.
- **Remake graphics (phase E, light and air):** the ground casts shadows, so cliffs shade the slopes below
  them; while the sun stands high the ambient light is darker and the sun stronger, so shade stands apart
  from sunlight (at dawn and dusk the ambient returns to the original's); light bounces between nearby
  surfaces (SSIL); a thin volumetric haze in the planet's fog colour gives light shafts and lamp haloes
  (airless planets have none); plasma and pulse rounds light the ground as they fly.
- **Towers and battle damage:** turrets, sentries and launchers turn their heads to their targets at the
  original's MAX_ROTATION, and machines fighting on their own turn their upper bodies to what they shoot.
  As in the original, damaged machines smoke black from 65 % damage and burn from 90 %, buildings smoke from
  40 % and burn from 50 %; missiles trail vapour and burst in fireballs, and bolters, autocannons,
  flamethrowers and electric chargers puff out smoke as they fire. Import again for the towers.
- **Support effects from the original's own meshes:** healers reach their patients with a beam of two
  helices turning against each other, and the patient glows with green crosses; the electric charger
  strikes with spinning lightning while halos run up and down its victim; the orb of treachery opens as a
  ring in the shooter's colour; machines a flamethrower hits burn for 15 seconds; transporters flash with
  rings of light as they load and unload ore. Import again for them.
- **Area weapons as the original drew them:** the ion cannon's beam widens from the sky for six seconds, draws
  in to a thread and leaves a ring of burning cracks; the nuke flashes, its shock wave runs out 220 m and its
  mushroom cloud rises over 16 seconds while cracks tear outwards; the vortex grows into a swirling sphere and
  collapses black. Import again for them.
- **The earth shakes** as in the original (W4dCameraShake): the nuke and the Gorilla's punch shake the view
  hardest, buildings blowing up by their size, weakening with distance in the strategy view.
- **Ground detail (remake look):** the terrain keeps the original's pictures and colours, and textures from
  [Poly Haven](https://polyhaven.com) (CC0) add the grain, pebbles, cracks, relief and roughness of sand,
  soil, grass, rock, mud, snow or metal plates close up; steep faces turn to rock. Each tile's layer is read
  from its original texture's name or colour. Fetch them once with `tools/terrain-textures/fetch.sh`
  (Powered by Poly Haven); without them the ground stays as before. Tufts of grass sway on grass and soil,
  pebbles and stones lie on rock, sand, gravel, mud and snow, each in the colours of the tile beneath; never
  on steep slopes, water, roads or metal, and buildings clear the ground they stand on. Tracks and wheels leave
  marks behind them that fade after half a minute.
- **Painted ground (remake look):** on the natural planets (desert, green, hills, forts, moons, Gracko, ice)
  the ground no longer shows the original's tiles with their hard edges and dark rectangles. A material map over
  the whole planet is read from the old pictures (sand, sandstone, beach, gravel, grass, mossy rock, regolith, red
  soil, dark rock, snow, ice) and painted with Poly Haven materials blended by their height maps, tinted to the
  original's colours; cliffs take their family's rock seen from the side. Each material comes in three looks laid
  in patches by chance and by the ground around: packed sand at the foot of the rock, wet sand and mud by the
  water, drier grass on the slopes, scorched ground by the lava. Water, lava and everything built keep
  their pictures. The same `fetch.sh` downloads them.
- **Photographed skies (remake look):** where the original's sky is a day sky (desert, green, hills, the
  day-and-night forts, the industrial worlds under a blue sky), photographed skies from Poly Haven (CC0) take its
  place, one for each height of the sun from night to noon, blended as it moves and tinted to the original sky's
  colours at that hour. The photographed sun is taken out; a glow lies round the planet's own. The clouds drift,
  stars come out at night, and the sky lends the scene reflections and some of its ambient light. Alien skies
  (moons, Gracko, the dark forts, ice) keep their painted pictures. The same `fetch.sh` downloads them (about
  55 MB). The air's haze now thins with the planet's view distance, so planets that fade to black far off no
  longer look like night by day.
- **Weather (remake look, only for the eye):** sand drifting over the desert, dust devils and sandstorms; snowfall,
  snow drifts and blizzards on the ice; rain showers on the green planets, hills and forts that darken and wet
  the ground, and mist in the hollows when the sun is low; ash and embers over Gracko and the red moons; shooting
  stars over the airless moons. Light weather comes and goes, a storm every ten to fifteen minutes, all drawn from
  the match's seed so every player and replay sees the same. Storms close in the fog and dim the sun; the grass
  leans with the wind; wind and rain sound (made from noise, not recorded). The dust machines kick up takes the
  planet's colour.
- **The planets' artefacts:** the trees, palms, rocks, ruins and other scenery the scenarios set on the
  planets (ARTEFACTS) stand where the original put them, from each planet's own models (`pdata/*.arb`). They
  stand in the way as in the original: machines go round the active ones (their boundaries, turned as they
  stand) and cannot build on them, while scenery (DUMB) blocks nothing. They cannot be destroyed yet.
- **Models in motion:** the machines' glowing eyes and turn-to-face sprites, glowing parts such as the
  plasma weapons' green, rotors and turbines that spin, hips that bob and the bee bomber's energy ball, all
  as the original moved them. Flyers and hover machines tip as they speed up, brake and turn; real
  shadows replace the flat shadow plates under flyers. In the remake look, destroyed machines burst into
  their parts, which fly, bounce and sink away. Buildings play their working animations as the original
  switched them on: factories while they produce, mines while they fill, smelters as ore arrives, beacons
  and pods all the time. Terrain tiles take the colours of their own vertices, so the dark squares at rock
  corners are gone. Groups ordered somewhere spread out around the target as in the original instead of
  piling onto one point. The import format is now 5: start the import again after updating.
- **Music:** the original soundtrack from your own MP3s (**Music folder…** in the menu, with a volume
  slider), played as the original played its CD: the intro and then the menu tune, each map's own track
  when the match begins and then the in-game tracks at random, a victory or defeat tune at the end.
- **Closer to the original:** its own mouse cursors, smoothly upscaled (Scale2x); the sun and moons on their
  real orbits, round the sky, with the planets' own pictures of them; barrels and pipes that turn to face
  the camera; turning ore holograms over found mineral sites; new machines come out on the factory pad,
  and a factory's assembly point (B) gathers them; machines walk into labs and garrisons through the door,
  which slides open; the AI sends hurt machines into garrisons. Buildings turn with Space while placing.
  Plasma and pulse bolts, missiles and stings fly as the original's own models and do their damage where
  they strike, so slow ones can be dodged; in first person the trigger fires where the crosshair points.
  Rounds leave from the weapons' own muzzles and fly straight at what they aim at, up to flyers too. First
  person has the original crosshair with the target's health and armour bars and the compass, and the
  weapon panels (Tab picks the weapons; the Gorilla never picks its punch alone). The Gorilla's punch
  tears the ground open in burning cracks fanning out ahead of it, and the bee bomb throws up rings of blue
  fire, one after another and ever wider, under its spreading shock wave. Glowing parts (the machines'
  eyes) glow in their texture's pattern. Each planet's own sky dome hangs over it, tinted by the time of
  day, with the stars showing through at night. Clicks make the original's sounds, and a refused order its
  bad click. Buildings under construction stand as a pulsing green hologram while the
  constructors' arms work and their jets throw beams and sparks. Factory and lab queues take any number
  of entries, and a click cancels one.
  Painted night skies can be added in `game/assets/sky` (see its README for the prompts).
- **Fog of war** as the original drew it: your machines uncover the planet by their scanner range; looking
  at unknown ground darkens the view with static down to black, where nothing can be picked and the world
  is silent. A level 1 beacon's map shows only what you have uncovered; a level 3 beacon lifts the fog.
  It is your view only: the AI never had it, in the original either.
- 195 tests. Every map runs AI-only for three minutes, and every map whose scripts give orders gets moving.

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
| `tools/sim-bench` | The performance gate: four players with 200 machines each, timed tick by tick (`dotnet run -c Release --project tools/sim-bench`) |
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

For automated checks, `MACHINES_MAP=<scenario>,<planet>` skips the menu, `MACHINES_SCREENSHOT=<file.png>`
saves a screenshot and quits (without fog of war unless `MACHINES_FOG=1`), and `MACHINES_SCREENSHOT_PLAY=punchA,3` plays an animation 3 s in (see `Game.cs` and
`SkirmishMenu.cs` for the other `MACHINES_*` switches).

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

A **test build** of the Machines: Wired for War remake, for Windows 10/11 (64-bit). Website: https://marvinrunge.github.io/machines/

**Download** the `setup.exe` below and run it, or the `windows.zip`, unzip it anywhere and run `Machines.exe`. `README.txt` lists the controls.

**New in 0.7.8:**
- **Games no longer break or hang behind the loading screen.** About one start in three could go wrong while the game loaded: errors piled up from then on, and in the worst case a later load, such as going back to a checkpoint, never finished. Loading takes as long as before.
- **Online: 0.7.7 and 0.7.8 play together.** Online play is unchanged, so a player on 0.7.7 can still play with one on 0.7.8.

**From 0.7.7:**
- **Third person in Survival and Battle.** Press **F** or turn the **mouse wheel** to switch between the faceplate and a camera behind and above your hero. Third person is the default; **Hero view** and **Camera distance** are in the options. The mouse turns the view and the guns, the keys drive the hull, and the shots land where the crosshair points. The camera pulls in at buildings and hills and never sinks into the ground. Under the crosshair, a small sign shows where the hull points and drives, and a red cross shows when a wall stands between your gun and what you aim at.
- **Guns swing within their arc, as in the original.** A hero without a turning top no longer turns its whole hull for every small aim. Within each weapon's arc from the original's data (10 degrees for most weapons, 30 for the Bolter) the guns angle on their mounts and the hull stays put. Brackets beside the crosshair mark the arc's edges. Aim past them, and the hull turns on the spot.
- **Survival is faster and busier.** From wave 6 on, waves without a boss come sooner and bring more machines, each of them weaker. On Normal the first shot falls after about 25 seconds instead of 40, a wave lasts about 80 seconds instead of 125, and 5 to 7 machines come instead of 2 to 4. How far you get is the same as before. Waves 1 to 5 and the boss waves are unchanged.
- **The skirmish computers play better.** They fill their squads, find their deposits and always build a mine. Each picks its own rival instead of all of them going for the nearest race. On **Hard** they build the best level of each fighter they have researched and research half again as fast. After 40 minutes about 70 % of their fighters are level 3 or higher, where about 20 % were before. The campaign is unchanged.
- **Machines are hit where their models are.** Shots no longer strike the air in a square box round a machine. They hit the machine's own shape, fitted to its model, so a round over a low machine's back flies past, and a sight on a tall machine's head hits there. Recordings and saves from earlier versions play as they were played.
- **Online: 0.7.7 plays only with 0.7.7.** The public relay now speaks the new version, so update before you play together.

**From 0.7.6:**
- **HUD icons wear your colour.** In skirmish with random starts, a player dealt another colour's start saw the icons of the selected machines and the squad's commander in that start's colour, often blue. They now show the player's own colour.
- **The production queue stays at the foot of the panel**, one row with arrows to scroll it, as in the original, instead of pushing the build menu down.
- **Machines inside buildings can be selected again:** steer the ground camera into a lab or another building with an interior, and the view shows the inside and clicks pick the machines in there.
- **Reapers and other legged machines no longer "breathe" on the spot:** they play their resting motion once when they stop, as the original does.
- **A richer look:** each kind of planet has its own colour grade, the eye adapts to the light, edges are smoothed over time (FSR 2), canyons lie in deeper shade, and indoor maps are darker, lit by headlights and lamps. Explosions make the air shimmer, sparks and debris bounce, machines rock on their springs and press the grass down, clouds cast drifting shadows, sand blows off ridges, and the overhauled models carry the planet's dust.
- **Online play works again** with the public relay (0.7.0 to 0.7.6 play together).

**From 0.7.5:**
- **Importing from CD 1 and 2 works again.** The import stopped with "Failed loading SDL3 library."; the importer now brings the library it was missing.
- **The game has its own icon**, and the start goes from the splash straight into the loading screen, the same picture without a jump (once the original has been imported). A full-screen game is full screen from the first frame.
- **Water in the Overhauled look:** lakes and rivers are clear, dark water under drifting waves that mirrors the sky, with foam along the banks. Waterfalls run downhill and throw up spray where they land.
- A punch shakes the view more calmly, and shots strike the machines and buildings themselves instead of the air around them.

**From 0.7.4:** the **Overhauled** look shows the painted ground (photographed textures) in place of the sharpened original tiles, which looked flat and blurry. A look chosen before keeps its ground as saved; pick **Overhauled** again in the options to switch. Energy bolts no longer show a black dot at their head.

**From 0.7.3:** Survival and Battle stay in first person while a run or a battle goes on; there is no view from above any more. **Esc** opens the menu over your hero. The end of a run or battle, skirmish and the campaign leave first person as before.

**From 0.7.2:** the unfinished HD models are left out of this build. A look chosen with them before now shows **Overhauled**: the original models with sharper textures.

**From 0.7.1: import from the CDs**
- **Put CD 1 in the drive** and the first start finds it: press **Import**. A disc image of it (`.iso`) in Downloads, Desktop or Documents is found as well, or pick one with **Disc image**. The game no longer has to be installed; the import unpacks it from the CD next to the remake.
- **The campaign's films** come along when CD 2 is in a drive or its image lies beside CD 1's; later, **From CD** in the options adds them.
- An installed copy still works as before (**Folder** picks it by hand).

**Online, 0.7.7 and 0.7.8 play together, but not with earlier versions.**

**From 0.7.0: online play**
- **A public relay.** Under **Multiplayer** the relay is already filled in: type a name, press **Connect**, join a room or open one. Nobody has to open a port. The relay sleeps while nobody plays, so the first connection after a break takes a few seconds.
- **Battle** (German *Schlacht*): two teams of Wired heroes, each defending its own pod, as **2 vs 2, 5 vs 5 or 10 vs 10**. Bots play the free seats. A room chooses the map, the tier, the lives and the victory: wipe out the other team, take its pod, or both.
- **Steady online matches:** a player whose line stutters is marked after 3 seconds and handed to a bot after 30, and gets the hero back on return. A player who drifts out of step is rebuilt from the relay within about 15 seconds. Players can join a running battle late, into a bot's seat.
- **Wired's new machines:** constructor, spy and locator lines with their own abilities (towers, mines, scans) and weapon batteries, built from the original's own meshes.
- **Smoother starts:** the loading screen warms up what a match needs, so far less stutters the first time it appears, and it shows a loading bar.
- The menus switch screens on the board instead of through the original's films, the shaded sides of planets are lighter, and the HUD size can be set in the options.

Replays and saves from versions before 0.7.0 do not load.

**Original models, sounds and films**
This build contains no art, sound, terrain or films from the original game; those belong to Night Dive Studios. If you own the game, the first start reads them from its CD, a disc image of it or the installed game (or **Import** in the options does) and keeps them next to the game. Without them, units are simple shapes.

**Imported with an earlier version?** If the options say so, press **Import again** once.

**Problems?** [Open an issue](https://github.com/marvinrunge/machines/issues) with what you did, what you expected and what happened, and the newest log from `%APPDATA%\Godot\app_userdata\Machines Remake\logs`.

Known gaps: Survival co-op online, accounts and matchmaking are still to come. The overhauled models' rounded edges and baked shading, and the new trees and rocks, are made with Blender and are not in this build; the Overhauled look shows the original models with sharpened textures, and the original's trees and rocks. The HD look with newly made models comes back once they are finished.

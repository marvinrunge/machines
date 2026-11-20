A **test build** of the Machines: Wired for War remake, for Windows 10/11 (64-bit). Website: https://marvinrunge.github.io/machines/

**Download** the `setup.exe` below and run it, or the `windows.zip`, unzip it anywhere and run `Machines.exe`. `README.txt` lists the controls.

**New in 0.7.5:**
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

**0.7.0 to 0.7.5 play together online.**

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

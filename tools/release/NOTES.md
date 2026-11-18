A **test build** of the Machines: Wired for War remake, for Windows 10/11 (64-bit). Website: https://marvinrunge.github.io/machines/

**Download** the `setup.exe` below and run it, or the `windows.zip`, unzip it anywhere and run `Machines.exe`. `README.txt` lists the controls.

**New in 0.7.0: online play**
- **A public relay.** Under **Multiplayer** the relay is already filled in: type a name, press **Connect**, join a room or open one. Nobody has to open a port. The relay sleeps while nobody plays, so the first connection after a break takes a few seconds.
- **Battle** (German *Schlacht*): two teams of Wired heroes, each defending its own pod, as **2 vs 2, 5 vs 5 or 10 vs 10**. Bots play the free seats. A room chooses the map, the tier, the lives and the victory: wipe out the other team, take its pod, or both.
- **Steady online matches:** a player whose line stutters is marked after 3 seconds and handed to a bot after 30, and gets the hero back on return. A player who drifts out of step is rebuilt from the relay within about 15 seconds. Players can join a running battle late, into a bot's seat.
- **Wired's new machines:** constructor, spy and locator lines with their own abilities (towers, mines, scans) and weapon batteries, built from the original's own meshes.
- **Smoother starts:** the loading screen warms up what a match needs, so far less stutters the first time it appears, and it shows a loading bar.
- The menus switch screens on the board instead of through the original's films, the shaded sides of planets are lighter, and the HUD size can be set in the options.

**Everyone online needs 0.7.0.** Replays and saves from earlier versions do not load.

**Original models, sounds and films**
This build contains no art, sound, terrain or films from the original game; those belong to Night Dive Studios. If you own the game (GOG or Steam), the first start finds your copy (or **Import** in the options reads it from a folder you pick) and keeps them next to the game. Without them, units are simple shapes.

**Imported with an earlier version?** If the options say so, press **Import again** once.

**Problems?** [Open an issue](https://github.com/marvinrunge/machines/issues) with what you did, what you expected and what happened, and the newest log from `%APPDATA%\Godot\app_userdata\Machines Remake\logs`.

Known gaps: Survival co-op online, accounts and matchmaking are still to come. The overhauled models' rounded edges and baked shading, and the new trees and rocks, are made with Blender and are not in this build; the Overhauled look shows the original models with sharpened textures, and the original's trees and rocks. The HD look with newly made models is still in the making.

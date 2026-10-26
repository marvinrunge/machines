MACHINES REMAKE {{VERSION}} - TEST BUILD
=======================================

A fan remake of Machines: Wired for War (1999), built with Godot. This is an early test build:
skirmish against the original AI on all 32 skirmish and multiplayer maps, alone or with friends.

START
  Unzip anywhere and run Machines.exe. Windows 10 or 11, 64-bit.
  "Remake graphics" in the menu switches between the remake's look and the classic one.

ORIGINAL MODELS AND TERRAIN
  This build contains no art, sound or terrain from the original game; those belong to Night Dive
  Studios. Without them, units are coloured shapes and the ground is simple stand-in terrain.

  If you own Machines: Wired for War (for example from GOG or Steam), press
  "Import from your game copy..." in the menu and pick the folder the game is installed in (the one
  with models.bin). The import takes about a minute; it reads the models and terrain from your copy
  with the original engine code and stores them in the "classic" folder next to Machines.exe.

MULTIPLAYER
  One player hosts: pick the map and settings in the skirmish menu, press "Multiplayer...", then
  "Host". Windows may ask whether Machines may use the network; allow it. The status line shows the
  address the others connect to.
  The others press "Multiplayer...", type that address (for example 192.168.1.20:7777) and "Connect",
  then pick the room and "Join". Everyone picks a colour and ticks Ready; the host starts the match.
  On the same network that is all. Over the internet the host forwards TCP port 7777 in the router to
  their computer and gives the others their public address; a VPN such as Tailscale or ZeroTier works too.
  Every player needs the same version, and the imported models of their own game copy for the full look.
  If someone drops out, the AI plays on for them. To come back, connect again with the same name, pick
  the running match and press "Join": the game catches up and hands your race back.
  Each match is saved to the "replays" folder; "Watch a replay..." in the menu plays one back.

CONTROLS
  Left click / drag      select (Shift adds)
  Right click            move, attack, build, repair, capture, heal, haul, enter labs and garrisons
  Ctrl + right click     attack-move
  W A S D, Q / E, wheel  pan, rotate, zoom
  F                      drive the selected machine in first person (W/S, A/D, left mouse fires)
  P                      pause (not online)
  F10                    back to the skirmish menu (online: leaves the match)

FEEDBACK
  Note what you did, what you expected and what happened; a screenshot helps.

LICENCE
  GPLv3, see LICENSE.md. Source code: https://github.com/marvinrunge/machines

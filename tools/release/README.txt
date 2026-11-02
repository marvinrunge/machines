MACHINES REMAKE {{VERSION}} - TEST BUILD
=======================================

A fan remake of Machines: Wired for War (1999), built with Godot. This is a test build: the original's
campaign, and skirmish against the original AI on all 32 skirmish and multiplayer maps, alone or with
friends.

START
  Unzip anywhere and run Machines.exe. Windows 10 or 11, 64-bit.
  The first start asks three things, each of which can be left for later and changed in the options:
  your game copy to import from, the look (Classic or Overhauled) and the folder with the soundtrack.
  The language (English or Deutsch) is chosen there and in the options too.

ORIGINAL MODELS, SOUNDS AND FILMS
  This build contains no art, sound, terrain or films from the original game; those belong to Night
  Dive Studios. Without them, units are coloured shapes and the ground is simple stand-in terrain.

  If you own Machines: Wired for War (for example from GOG or Steam), pick the folder the game is
  installed in (the one with models.bin) at the first start, or press "Import" in the options. The
  import takes a few minutes; it reads the models and terrain from your copy with the original engine
  code, sharpens the textures on your graphics card and stores them in the "classic" folder next to
  Machines.exe. Sounds, texts and the campaign's films come straight from your copy.

MUSIC
  The remake plays the original soundtrack from MP3s of your own (Track 00 - Intro.mp3 and the others).
  Pick their folder at the first start or under Soundtrack in the options.

MULTIPLAYER
  One player hosts: press "Multiplayer", then "Host". Windows may ask whether Machines may use the
  network; allow it. With "Open the port on the router" ticked, the game asks the router to let the
  others in (UPnP) and shows the address to give them; "Copy address" copies it. If the router will not,
  the page says why: then forward TCP port 7777 in the router by hand, play over IPv6, or everyone joins
  a VPN such as Tailscale or ZeroTier.
  The others press "Multiplayer", type that address (for example 203.0.113.20:7777) and "Connect",
  then pick the room and "Join". Everyone picks a colour and gets ready; the host starts the match.
  Free slots can play the AI, easy, normal or hard. Every player needs the same version.
  If someone drops out, the AI plays on for them. To come back, connect again with the same name, pick
  the running match and press "Join": the game catches up and hands your race back.
  Each match is saved to the "replays" folder; "Replays" in the menu plays one back.

CONTROLS
  Left click / drag      select (Shift adds)
  Right click            move, attack, build, repair, capture, heal, haul, enter labs and garrisons
  Ctrl + right click     attack-move
  W A S D, Q / E, wheel  pan, rotate, zoom
  F                      drive the selected machine in first person: W/S drive, Q/E turn, A/D step
                         sideways or turn, the mouse looks and the machine follows, left mouse fires
  P                      pause (not online)
  F10 or Esc             the menus: continue, briefing, save, load, options, hot keys, leave
  These are the first keys: "Change keys" in the options moves them.

FEEDBACK
  Note what you did, what you expected and what happened; a screenshot helps.

LICENCE
  GPLv3, see LICENSE.md. Source code: https://github.com/marvinrunge/machines

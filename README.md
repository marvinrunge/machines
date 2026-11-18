# Machines Remake

A fan remake of **Machines: Wired for War** (Charybdis / Acclaim, 1999), rebuilt from the ground up in Godot 4 and C#.
The game rules come from the GPLv3 data files of the original source release; the art, sounds and films come from
your own copy of the game.

**[Website](https://marvinrunge.github.io/machines/)** ·
**[Download](https://github.com/marvinrunge/machines/releases)** ·
**[Report a problem](https://github.com/marvinrunge/machines/issues)**

> This is a test build. Expect rough edges, and tell us about them.

## What's in it

- **The campaign** and **skirmish** against the original AI on all 32 skirmish and multiplayer maps.
- **Wired**, a survival mode of the remake's own: build a hero in the hangar and drive it in first person to keep your
  pod alive against endless waves.
- **Battle**, two teams of heroes defending their pods: 2 vs 2, 5 vs 5 or 10 vs 10, with bots in free seats.
- **Online play** over the public relay, no port forwarding needed, or on your own network.
- Two looks: **Classic**, as in 1999, and **Overhauled**, with sharper textures on painted ground, new lighting, weather and effects.

## Install

You need Windows 10 or 11 (64-bit) and your own *Machines: Wired for War*: its CDs, disc images of them (`.iso`) or
the game installed from them (it is not sold anywhere today).

1. **Download** `Machines-Remake-<version>-setup.exe` from the
   [latest release](https://github.com/marvinrunge/machines/releases).
2. **Run it.** It installs for you alone, without administrator rights, and adds a start menu entry. If Windows warns
   about an unknown publisher, choose **More info**, then **Run anyway**.
3. **Start the game and import.** Put CD 1 in the drive (or keep its `.iso` in Downloads, Desktop or Documents), or
   have the game installed: the first start finds it, press **Import**. Found nothing? Pick the disc image with
   **Disc image** or the installed game's folder with **Folder**. With CD 2 in the drive or its image beside CD 1's,
   the campaign's films come along; later, **From CD** in the options adds them. The import takes a few minutes.
4. **Music** (optional): pick a folder with the soundtrack as MP3s (`Track 00 - Intro.mp3` and the others), for
   example ripped from the CD's audio tracks.

Prefer no installer? Unzip `Machines-Remake-<version>-windows.zip` anywhere and run `Machines.exe`.

Updating keeps your import, saves and replays. Uninstall it in Windows' list of apps.

Without a copy of the game you can still start the remake, but machines are simple shapes and the ground is stand-in
terrain.

## Play online

1. Open **Multiplayer** and type a name. The **Relay** field already holds the public relay.
2. Press **Connect**, then join a room or open one.
3. Everyone picks a seat and gets ready, and the host starts.

The relay sleeps while nobody plays, so the first connection after a break can take a few seconds. Everyone needs the
same version.

To play on your own network instead, one player presses **Host** and the others connect to the address it shows.

## Controls

| Key | Does |
| --- | --- |
| Left click / drag | Select |
| Right click | Move, attack, build, repair, capture, haul |
| W A S D, Q / E, wheel | Pan, rotate, zoom |
| F | Drive the selected machine in first person |
| Esc or F10 | Menu: save, load, options, leave |

**Change keys** in the options moves them all. The hot keys page in the in-game menu lists every command, and
`README.txt` next to the game has the full list.

## Problems?

[Open an issue](https://github.com/marvinrunge/machines/issues) with what you did, what you expected and what happened.
A screenshot helps, and so does the log from `%APPDATA%\Godot\app_userdata\Machines Remake\logs`.

## Building from source

You need the .NET 10 SDK and Godot 4.7.2, .NET edition.

```bash
dotnet test Machines.sln
```

Then open `game/project.godot` in Godot and press Play. Everything else (the code layout, test switches, the relay,
releases) is in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Licence

GPLv3, see [LICENSE.md](LICENSE.md), like the original source release. This build contains no art, sound, terrain or
films from the original game; those belong to Night Dive Studios. This is a fan project, not affiliated with them. The
game's name and trademarks belong to their owners.

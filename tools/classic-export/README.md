# Classic model exporter

Exports the original 1999 unit and building models from **your own copy** of Machines: Wired for War,
so the remake can show them. The output stays on your machine: `classic/` is git-ignored and must never
be committed or shared.

It links the original engine from the GPLv3 source release, loads the game's `models.bin` exactly as
the game did, builds every machine and building for each race, and writes their visible geometry as
JSON (`classic/models/<race>/<model id>.json`). Textures are not copied: the Godot client reads the
BMP files straight from your game folder (`classic/game_path.txt`).

## Requirements

- MSYS2 with the MinGW64 toolchain:
  `pacman -S mingw-w64-x86_64-{gcc,cmake,ninja,SDL2,SDL2_image,openal,alure,glew,glm,freetype} mingw-w64-ucrt-x86_64-enet`
- The original engine source (`nightdivestudio/machines`)
- A purchased copy of the game (folder with `models.bin` and `models/texture4`)

## Build and run

From an MSYS2 shell (or `C:\msys64\usr\bin\bash.exe -lc "..."`):

```bash
tools/classic-export/build.sh /c/Projekte/nightdivestudio/machines
cp /ucrt64/bin/libenet-7.dll tools/classic-export/build/
tools/classic-export/run.sh /c/Users/<you>/Downloads/Machines
```

`run.sh` writes the model list (`model-list.sh`: every research-tree machine and construction plus all
building levels, 79 models), runs the exporter for the four races (316 files, a few seconds), and
records the game folder for the client.

## How it works

- Start-up follows the original planet editor: render library, sound (machines attach their engine
  sound when built), display, scene manager, texture search path, then `MachPhysPreload::persistentPreload`.
- The game's sound definitions are inside its encrypted archive, so `data/original/sounds/snddef64.dat`
  from the source release is used instead. The archive itself is not opened.
- Machines and buildings keep their parts as composite *links*, a subtree separate from ordinary
  children; each part's transform is composed up its parent chain.
- Geometry comes from `RenMesh`'s private vertex and triangle data; the exporter is compiled with
  `-fno-access-control` rather than changing the engine.
- Engineers and Brainboxes only exist at software ("brain") levels 3 and 5 (`machphys/levels.cpp`).
- Coordinates stay in the original's z-up model space; the client converts them.

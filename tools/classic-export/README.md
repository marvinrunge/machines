# Classic model exporter

Exports the original 1999 unit and building models from **your own copy** of Machines: Wired for War,
so the remake can show them. The output stays on your machine: `classic/` is git-ignored and must never
be committed or shared.

It links the original engine from the GPLv3 source release and loads the game's own files exactly as the
game did:

- **Models:** from `models.bin`, every machine and building for each race, written as JSON
  (`classic/models/<race>/<model id>.json`). Machines also carry a `rig`: the machine and each of its links
  as a tree of nodes with their rest transforms and their own geometry, the link that turns the upper body,
  and how the locomotion moves them. Spider legs bring their resting, walking, turning, start and stop
  cycles, sampled 20 times a second from the original composite plans. Wheels bring their nodes and radius,
  and tracks their tread nodes and texture repeats per metre.
- **Terrain:** each planet's surface from `pdata/<planet>.psb`, the engine's persisted planet with all its
  terrain tiles. The exporter writes the tiles as one model (`classic/planets/<planet>/terrain.json`) and the
  engine's `terrainHeight` sampled every 4 m (`heights.bin`: int32 columns, int32 rows, float32 spacing,
  then float32 heights row by row).

Textures are not copied: the Godot client reads the BMP files straight from your game folder
(`classic/game_path.txt`).

## Requirements

- MSYS2 with the MinGW64 toolchain:
  `pacman -S mingw-w64-x86_64-{gcc,cmake,ninja,SDL2,SDL2_image,openal,glew,glm,freetype} mingw-w64-ucrt-x86_64-enet`
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
building levels, 79 models), runs the exporter for the four races (316 files, a few seconds), then once
per planet with `--planet` (45 planets, under a minute), and records the game folder for the client.
A single planet:

```bash
classic_export.exe --planet <game folder> data/original/models/planet/<p>/<p>.psf classic/planets/<p> data/original/sounds/snddef64.dat
```

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
- The game folder has no `.psf` or `.env` files (they are in the encrypted `mach1.met`, which the source
  release cannot read and the exporter leaves alone). The surface is given the `.psf` path in
  `data/original`, so it finds the `.env` beside it, and still reads `pdata/<planet>.psb`, the sky meshes
  and the textures from the game folder, its working directory.
- `MachPhysPersistence::instance()` must run first: it registers the classes the `.psb` stream holds.
- The game copy has no `.psb` for `2o1` and `desert`, so those two keep the stand-in terrain; neither is a
  skirmish planet.

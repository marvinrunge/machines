# HD textures

The remake look shows the original textures enlarged four times, with normal and roughness maps. They are made
from **your own copy** of the game, as the last step of the import (`game/scripts/HdTextures.cs`), and written to
`classic/hd` (git-ignored, never shared).

## Real-ESRGAN

The enlarging is done by [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN)'s portable Windows build
(BSD-3-Clause, see `LICENSE-Real-ESRGAN.txt`), which runs on the GPU through Vulkan. Fetch it once:

```bash
tools/texture-upscale/fetch.sh
```

It lands in `tools/texture-upscale/bin` (git-ignored); release builds copy it with its licence. Without it, or
without a GPU it can use, the textures are enlarged with Lanczos filtering instead.

- Planet tiles go through `realesr-animevideov3`, which keeps the grain of sand and rock; machines and buildings
  through `realesrgan-x4plus-anime`, which draws their painted panels crisply. `realesrgan-x4plus` smoothed the
  ground into plastic.
- Steam's, Epic's and Twitch's Vulkan overlay layers make this 2022 build fail to find the GPU
  (`vkEnumeratePhysicalDevices failed`); the game starts it with `VK_LOADER_LAYERS_DISABLE=~implicit~`.
- Textures are padded with their own far edges before enlarging and cut back afterwards, so tiles and tread
  textures repeat without seams. Magenta cut-outs are filled with their neighbours' colour first and their
  coverage is enlarged separately, so no pink fringe appears.

## The maps

Fine brightness detail (the brightness minus its blur over two original pixels) is read as height: panel lines,
rivets and cracks sink in, while the large light and shade painted into the textures stays flat. The normal
map follows its slope (OpenGL convention, green up); darker and recessed spots are rougher. Skies, suns, moons
and water get no maps.

## Running it alone

From the repository, with the models already imported:

```bash
MACHINES_HD=/c/Users/<you>/Downloads/Machines godot --path game
```

It makes the textures and quits; `classic/hd.log` has the upscaler's output.

# HD films

The original's films (Smacker, `flics` in the game copy) are 8-bit pictures of 632×272 at most, at 10 to 25
frames a second. In the looks other than the classic one, `SmackerView` plays HD versions instead when they have
been made from **your own copy** of the game. They are written to `classic/films` (git-ignored, never shared) by
this tool:

```bash
tools/texture-upscale/fetch.sh   # Real-ESRGAN, if not fetched yet
tools/film-upscale/fetch.sh      # RIFE and FFmpeg
dotnet run -c Release --project tools/film-upscale                    # every film
dotnet run -c Release --project tools/film-upscale -- animatic gui/tutba1b   # some
```

`--game <folder>` names the game copy; without it, the one in `classic/game_path.txt` is used. Films already made
are kept, so the tool can be stopped and started again.

## Steps

1. Each frame is decoded (`SmackerDecoder`) and enlarged by Real-ESRGAN's video model `realesr-animevideov3`: films
   up to 320 pixels wide (briefing and menu pictures) four times, the 632×272 cut-scenes three times
   (1896×816), the 640×480 ones twice. It keeps the original's look while the 256-colour dithering and Smacker's
   4×4 blocks go; `realesrgan-x4plus` invented textures that are not there (wet rock on the briefings' planets).
2. [RIFE](https://github.com/nihui/rife-ncnn-vulkan) v4.6 (MIT) draws the frames in between, to 50-60 a second:
   twice as many for the 25-frame films, three times for 20, four for 15, six for 10. Every original frame stays
   as it was.
3. [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) (GPL, essentials build) writes the frames with the first sound
   track as Ogg Theora (quality 8) and Vorbis, which Godot plays itself. Silent tracks are left out.

The GPU steps take about 0.1 s a frame on an RTX 3060 laptop; the films are encoded on three processor threads
while the GPU goes on with the next one. Both programs are started with `VK_LOADER_LAYERS_DISABLE=~implicit~`, as
Steam's, Epic's and Twitch's Vulkan overlays keep them from finding the GPU.

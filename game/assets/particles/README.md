# Particle textures

Single particle pictures for the remake's effects (weather, dust, smoke), from Kenney's
[Particle Pack](https://kenney.nl/assets/particle-pack) 1.1, CC0 (see `License-Kenney.txt`).
Credit is not required; it is given here and in the README anyway. Scaled down from 512 to 256
pixels; their alpha holds the shape, their colour is white or grey and is tinted in the game.

| File | Used for |
|---|---|
| `smoke_04`, `smoke_07`, `smoke_08` | sandstorm and blizzard clouds, dust behind machines, dust devils |
| `dirt_01`, `dirt_02`, `dirt_03` | blown sand and grit, ash |
| `trace_01`, `trace_06` | rain streaks, shooting stars |
| `circle_02` | rain splashes |
| `circle_05`, `light_02` | snowflakes, mist puffs |
| `spark_05`, `star_01` | embers |
| `scorch_02`, `twirl_02` | spare (dust devils, wind gusts) |

Animated flipbooks are rendered in Blender (`tools/blender/flipbooks.py`, 4 by 4 frames) and go in
`flipbooks/` beside these:

| File | Used for |
|---|---|
| `billow` | storm walls; smoke from explosions, missile hits and the nuke's cap; the nuke's dust wave |
| `devil` | dust devils |
| `splash` | rain splashes |
| `fireball` | explosions, missile hits, the flamethrower's stream (its burning half only), the nuke's mushroom (fire cooling into smoke, coloured as rendered) |
| `flame` | damage fires, burning machines, where the flamethrower reaches the ground, the fire walls of punch, bee bomb, ion cannon and nuke (twice as high as wide; greyed in the game for the bee bomb's blue) |

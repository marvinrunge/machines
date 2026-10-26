# Night skies

Painted night skies for the remake look. The game lays one over the sky at night, and all day on
planets whose sky stays dark (moons, space). The sun, moons and planets are drawn separately from
the original game, so the pictures should hold none.

## Format

- Equirectangular 360° panorama, 2:1, at least 4096 × 2048 (8192 × 4096 is better), PNG or JPG.
- The horizon runs across the middle. The **lower half is plain black**; the ground covers it.
- Black background: the picture is added onto the sky's own colours, so black means "no change".
- The left and right edges must meet seamlessly.
- No sun, moons, planets, landscape, horizon silhouettes, text, watermark or frame.

Tools with a real 360° mode do this best (Blockade Labs Skybox AI, for example). With other image
generators, add `equirectangular 360 panorama, 2:1` and check the seam and the top (the zenith is
stretched across the whole upper edge).

## Files

The game looks for, in order: `<planet>.png`, then `<theme>.png`, then `stars.png`; `.jpg` and `.webp`
work too. Without any of them it draws a plain starfield.

| File | Planets |
| --- | --- |
| `stars.png` | every planet without a picture of its own |
| `desert.png` | m_desert, desspy, daynight |
| `green.png` | m_green, mgreendn, m_dales, mdalesdn, finger, hilly, talonth8 |
| `ice.png` | ice, icedn, micey, miceydn, resfac |
| `lunar.png` | m_moon, mmoony, 1o1, 1o2, 1o3, CommSat, dogbar, multidog, seekndes, tut |
| `volcanic.png` | gracko, mgrack |
| `fortress.png` | fortmult, fortmdn, fortsdn, fortseig, seige, 5o1 |
| `industrial.png` | home, joust, midian, control, m_contrl, kidnap, sat, roller |
| `space.png` | space, suicide, guns, deepspac, 2o1 |

## Prompts

Each prompt starts with the same base, then a line of its own.

**Base:**

> 360 degree equirectangular panorama of a futuristic night sky, 2:1, seamless left and right edges,
> horizon exactly across the middle, lower half pure black, pure black background between the stars,
> thousands of tiny sharp stars in many sizes, subtle and elegant, cinematic science fiction, no sun,
> no moon, no planets, no landscape, no ground, no text, no watermark

**stars**: a calm band of the galaxy arching across the sky, faint blue and violet nebula wisps, a
few bright blue-white stars, a thin faint ring arc of an orbital megastructure low on one side

**desert**: warm night, a dusty orange and amber nebula drifting low over the horizon, a sparse band
of satellite lights crossing the sky in a dotted line, stars slightly softened by fine dust

**green**: a clear temperate night, a soft green and teal aurora rippling along part of the horizon,
a bright galaxy band above it, a few slow-moving lights of orbital stations

**ice**: a crisp, cold night, very sharp bright stars, pale cyan and white aurora curtains, a faint
cold-blue nebula near the zenith, a delicate halo of ice crystals around the brightest stars

**lunar**: an airless black sky, extremely dense stars, the bright core of the galaxy as a brilliant
band with dark dust lanes, small distant galaxies, no haze at all

**volcanic**: a smoky night with an ash-red tint near the horizon, a deep crimson and orange nebula
like embers, stars dimmed and reddened low down, clear above

**fortress**: a stormy war-torn night, a steel-blue and grey nebula torn into streaks, a distant
fleet of tiny lights in formation, faint flashes of far-off battle among the stars

**industrial**: an orbital industrial night, a few thin light trails of shuttle traffic lanes
crossing the sky, a faint ring of orbital factories glinting, stars slightly dimmed by smog low down

**space**: deep space, vivid magenta, blue and gold nebula clouds, glittering star clusters, a
distant spiral galaxy, the richest and most colourful of the set

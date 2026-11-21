# The prologue's deck, scaled 2x (stage 6)

The player's note after stage 5: the deck is far too small. The doors are narrower than a Grunt, and the escape pods in the
pod bay are tiny next to the Grunts. The deck should be made bigger as a whole, with more way to drive; only the small duct
ways may stay as they are. As a guide for the size, the player asked to compare a Grunt with the game's Pod on a normal
skirmish map.

The deck `data/planets/prolog/prolog.deck` is now **630 x 420 m** (it was 270 x 180). It was made from the stage 5 deck
(commit 65a7b84) by `tools/deck/scale.mjs` with the plan `tools/deck/prolog-x2.plan`:

```
git show 65a7b84:data/planets/prolog/prolog.deck > old.deck
node tools/deck/scale.mjs tools/deck/prolog-x2.plan old.deck data/planets/prolog/prolog.deck
node tools/deck/deck.mjs        # greybox .csp, scenario .scn, prolog.png and prolog-reach.txt from the new deck
```

`data/planets/prolog/prolog.psf` now has 21 x 14 tiles of 30 m (it had 9 x 6).

## 1. Measurement: a Grunt next to a Pod

Measured on fortmult (a skirmish map) in the Overhauled look. The drawn bounds are the models' meshes as the game draws
them (`ActorView` bounds). The sim radius is `Actor.Radius`, and a building's footprint is `Construction.Footprint`. Units
are game metres, the same as deck metres. Picture: `scratchpad/prologue/stage6-shots/deck/grunt-vs-pod.png`, showing the
two Grunts (left), the Locator (small, in front) and the Pod.

| What | drawn: across x deep x tall | sim |
|---|---|---|
| Pod (POD_1_T_ION_ORBITAL_CANNON) | 47.5 x 47.8 x 43.5 m above ground (dome with landing pads; the ion mast on top) | footprint 28.7 x 28.6 m, radius 14.35 |
| Garrison (for the doors of buildings) | 36.9 x 35.3 x 11.6 m | footprint 35 x 31 m |
| Grunt 1 (R_BOLTER, the hostile Grunts) | 6.5 x 5.3 x 5.5 m | radius 1.5 |
| Grunt 2 (L_BOLTER) | 4.5 x 6.3 x 5.5 m | radius 1.6 |
| Grunt 3 (LR_HEAVY_BOLTER1_X2) | 5.7 x 7.7 x 6.1 m | radius 2.06 |
| Locator (GEO_LOCATOR 1) | 5.5 x 4.8 x 5.4 m | radius 1.5 |
| Reaper 2 (ASSASSIN 2) | 11.1 x 8.4 x 11.4 m | radius 2.4 |
| Reaper 3 (ASSASSIN 3) | 10.0 x 7.2 x 8.1 m | radius 2.4 |
| Thresher (ASSASSIN 4) | 15.0 x 11.6 x 13.2 m | radius 2.88 |
| Commander (COMMANDER 5) | 5.7 x 8.4 x 9.3 m | radius 2.0 |

How the stage 5 deck compared with this:

- The Grunts' doors were 4 m wide, but a Grunt is drawn 4.5 to 6.5 m wide. The Reapers' blast doors were 6 to 8 m, but a
  Reaper is 10 to 11 m wide. The Thresher's ring corridors were 8 m, but the Thresher is 15 m wide. The bays were 7 m tall,
  while a Grunt stands 5.5 to 6 m tall.
- The escape pods in the pod bay are the game's Pod shown at **7 m** across (`Game.DeckShow`: `PodWidth = 7`, scale 0.154;
  `abwurf.cut`: `scale=0.154`). That is 1.1 to 1.5 Grunt widths. In the game the Pod's footprint is 6.4 widths of a level-2
  Grunt (28.7 / 4.5 m) and its drawn width about 10 (47.5 / 4.5 m), so the bay's pods were about a seventh of the game's
  size. The pod outside (the Abwurf film's landing) is drawn at its own size, about 45 m, hence stage 5's "45 m against 7 m".

## 2. The factor: 2

At 2x the deck fits the machines the way the game's buildings fit them:

- A Grunt's door becomes 8 m, so a 4.5 to 6.5 m Grunt has 0.75 to 1.75 m to spare on each side.
- The Reapers' blast doors become 12 to 16 m, wide enough for a 10 to 11 m Reaper. The Thresher's ring becomes 16 m, for a
  15 m Thresher.
- Room heights double: 14 m bays, 16 to 20 m corridors and halls, 18 m maintenance decks. Grunts stand about 6 m tall,
  Reapers up to 11.4 m and the Thresher 13.2 m.

3x would give a Grunt a 12 m door, two Grunt widths, and the Reapers 18 to 24 m doors. It would also make the grid 9 times
as large (810 x 540 before padding) and every way 3 times as long, well past the 1.5 to 2 times the play time asked for.
The one room that 2x leaves too small is the pod bay, and it gets its own stretch (below).

## 3. Mapping old points to new ones

For an old point (x, y):

```
x' = 2x + 16 [x >= 49] + 24 [x >= 141] + 48 [x >= 250]
y' = 2y + 16 [y >= 44] + 16 [y >= 70]
z' = 2z        (heights of cameras and rails)
```

A zone's far edge (x1, y1) moves with a stretch only if it lies beyond it (`x1 > at`, not `>=`). Angles do not change. To
convert a point, run `node tools/deck/scale.mjs tools/deck/prolog-x2.plan --map 235,40 180,106`, which prints
`235,40 -> 510,80` and `180,106 -> 400,244`.

The five stretches (`stretch` in the plan). Each one inserts copies of the cell column or row just west or south of the old
line, so the order of the sections and the story beats stay as they were:

| old line | new cells | what gets longer |
|---|---|---|
| x = 49 | +16 | the Buchtengang R0, from the bays to deck A |
| y = 44 | +16 | the links WL and EL, the service passage S and duct V1a; the pod bay R16 gets deeper |
| y = 70 | +16 | deck B (R6) gets deeper; the Notgang R15 gets longer |
| x = 141 | +24 | the spine R10, the parallel corridors P1 and P2, the cable corridor K and duct V2 |
| x = 250 | +48 | the pod bay R16 gets wider, for three of the game's pods side by side |

The grid is padded with wall to 630 x 420, which is 21 x 14 tiles of 30 m.

### Rules besides the plain 2x

- **Ducts** (`v`, their `^` lips and the vent door V) keep their 4 m cross-section; only their length grows. After the
  scaling, each duct keeps the middle 4 of its 8 cells across (offset 2 from its scaled edge), and the cells beside it
  become wall. Zones in a duct (V1a, V1b, V1c, V2, dust_fall) and markers in one move onto that band. The vent door V
  keeps its 2 m depth on R9's side, and the other 2 m of its slot become duct.
- **Every other door** doubles with its wall: walls become 4 m thick, so doors are 4 m deep.
- **Cover `c`** doubles with the cells. The kit's crates and consoles fit themselves to a cover block, so they stay their
  real size and there are more of them. Props (crate_large, crate_small) keep their size.
- **The computer core** (prop `core`) is drawn twice as large in `tools/deck/deck.mjs`: 8 m round and 28 m tall (it was
  4 m and 14 m).

### The plan's own edits (new coordinates)

- **R9 cover** for the fight in the core chamber (160-224 x 232-296, core at 192,264):
  - four 4 x 4 m pillars of wall from floor to ceiling, at 172-176 / 208-212 x 240-244 / 284-288;
  - console banks `c` 160-166 x 258-262 (west wall) and 218-224 x 274-278 (east wall, beside vent V), with markers
    `console_r9_w` and `console_r9_e` tagged `terminal`, so the kit dresses them as consoles;
  - crate stacks `c` 168-171 x 270-273, 213-216 x 254-257, 178-181 x 282-285 and 203-206 x 282-285;
  - six loose crates (props `crate_r9_1` to `crate_r9_6`, crate_large, ids 305-310): two by I (176,236 and 208,236), two by
    J (176,291 and 208,291), and two in the NE corner behind the east console bank (218,284 and 218,289); in front of M
    (216,247 and 216,252, as first placed) they left the Reapers no way into P2.

  Every way round the core and through I, J, K and M stays at least 9 m wide, so the Reapers still get round. The NE
  corner behind the east console bank is too narrow for Reapers, which makes it a Grunt and Locator hiding spot by the vent.
- **Pods** at their real size: `pod_1` 464,54, `pod_2` 516,54 and `pod_3` 568,54. They are 52 m apart, 8 m of drawn pod
  stands clear of each wall, and pod_2 sits under the Notgang's mouth. `pod_3_hatch` is at 568,74 (facing 270) and
  `pod_zone` at 544,64-592,96.
- **cam_abwurf_1** moved to 520,110 at 48 m height, yaw 311, pitch -25: from the catwalk, looking down at pod 3.

## 4. Doors

Gaps a class passes on the new deck, as `tools/deck/deck.mjs` checks them: the Locator 3 m when crawling, Grunts 5 m,
Reapers and the Commander 9 m, the Thresher 13 m. These are twice the plan's 3 / 5 / 7 m, less one cell, which keeps every
old gating; see section 7.

| door | kind, start | old -> new width | new cells | who passes |
|---|---|---|---|---|
| A bay7_bay6 | door, locked | 4 -> 8 m | 36,48-40,56 | Locator, Grunts |
| B bay7_bay8 | door, locked | 4 -> 8 m | 68,48-72,56 | Locator, Grunts |
| C bay7_corridor | door, locked | 4 -> 8 m | 48,64-56,68 | Locator, Grunts |
| D corridor_deckA | door, closed | 4 -> 8 m | 116,72-120,80 | Locator, Grunts |
| E service_passage | door, locked | 4 -> 8 m | 232,80-240,84 | Locator, Grunts |
| F lager_westlink | door, locked | 4 -> 8 m | 136,112-140,120 | Locator, Grunts |
| H deckB_vorraum | blast, locked | 6 -> 12 m | 184,208-196,212 | + Reapers, Commander |
| I vorraum_core | blast, closed | 6 -> 12 m | 184,228-196,232 | + Reapers, Commander |
| J core_spine | blast, closed | 8 -> 16 m | 184,296-200,300 | + Reapers, Commander, Thresher |
| K core_cable | door, closed | 4 -> 8 m | 224,232-228,240 | Locator, Grunts (keeps the Reapers out of K) |
| M core_p2 | blast, closed | 7 -> 14 m | 224,244-228,258 | + Reapers, Commander, Thresher |
| L spine_p1 | blast, open | 7 -> 14 m | 256,296-270,300 | + Reapers, Commander, Thresher |
| V core_vent | vent, closed | 4 -> 4 m (2 deep) | 224,266-226,270 | Locator, crawling |
| Q d5 | door, locked | 4 -> 8 m | 380,232-384,240 | Locator, Grunts |
| O g2 | gate, closed | 7 -> 14 m | 380,244-384,258 | + Reapers, Commander, Thresher |
| G g1 | gate, closed | 7 -> 14 m | 380,276-384,290 | + Reapers, Commander, Thresher |
| R kreuzung_werkstatt | door, closed | 4 -> 8 m | 416,240-420,248 | Locator, Grunts |
| T werkstatt_gang | door, closed | 4 -> 8 m | 480,224-484,232 | Locator, Grunts |
| U werkstatt_repair | door, closed | 4 -> 8 m | 480,240-484,248 | Locator, Grunts |

The doors that keep big machines out still do: K, S (8 m), the bays, the Lager and R13 to R16 keep out Reapers and the
Commander, and the ducts keep out everyone but the Locator. The Thresher stays on its ring (R4, WL, R6, EL). The grates `g`
between P1, V2, P2 and K still block movement and let shots and sight through. The doors the Reapers storm (I, J, M, the
gates O and G, H) still let them through. `docs/story/deck/prolog-reach.txt` shows that every check passes with every door
open. The rooms each class reaches are the same as in stage 5.

## 5. Rooms

| room | new x0 y0 x1 y1 | size | height | was |
|---|---|---|---|---|
| R2 Bucht 6 | 8 40 36 64 | 28 x 24 m | 14 | 14 x 12, 7 |
| R1 Bucht 7 | 40 40 68 64 | 28 x 24 m | 14 | 14 x 12, 7 |
| R3 Bucht 8 | 72 40 96 64 | 24 x 24 m | 14 | 12 x 12, 7 |
| R0 Buchtengang | 8 68 116 80 | 108 x 12 m | 12 | 46 x 6, 6 |
| R4 Wartungsdeck A | 120 24 264 80 | 144 x 56 m | 18 | 72 x 28, 9 |
| WL / EL links | 120 / 248, 80-144 | 16 x 64 m | 18 | 8 x 24, 9 |
| R5 Lager | 140 108 176 132 | 36 x 24 m | 12 | 18 x 12, 6 |
| R7 Schaltraum | 200 108 224 128 | 24 x 20 m | 12 | 12 x 10, 6 |
| S service passage | 232 80 240 144 | 8 x 64 m | 10 | 4 x 24, 5 |
| R6 Wartungsdeck B | 120 144 264 208 | 144 x 64 m | 18 | 72 x 24, 9 |
| R8 Vorraum | 176 212 208 228 | 32 x 16 m | 16 | 16 x 8, 8 |
| R9 Rechnerkern | 160 232 224 296 | 64 x 64 m | **40** | 32 x 32, 16 |
| R10 Hauptgang | 128 300 432 320 | 304 x 20 m | 20 | 140 x 10, 10 |
| R10a Kaserne-Lift | 432 300 448 320 | 16 x 20 m | 20 | 8 x 10, 10 |
| P1 / P1s | 232 276 380 290 / 256 290 270 296 | 148 x 14 / 14 x 6 m | 16 | 62 x 7, 8 |
| P2 | 228 244 380 258 | 152 x 14 m | 16 | 64 x 7, 8 |
| K Kabelgang | 228 232 380 240 | 152 x 8 m | 8 | 64 x 4, 4 |
| R12 Kreuzung | 384 224 416 292 | 32 x 68 m | 16 | 16 x 34, 8 |
| R13 Werkstatt | 420 224 480 264 | 60 x 40 m | 14 | 30 x 20, 7 |
| R13a / R14 | 484 224 512 232 / 484 240 508 256 | 28 x 8 / 24 x 16 m | 10 | 14 x 4 / 12 x 8, 5 |
| R15 Notgang / R15a | 512 128 528 256 / 512 120 528 128 | 16 x 128 / 16 x 8 m | 16 | 8 x 56 / 8 x 4, 8 |
| R16 Pod-Bucht | 432 24 600 120 | 168 x 96 m | **56** | 60 x 40, 20 |
| V1a / V1b / V1c / V2 ducts | 158 80 162 108 / 176 118 200 122 / 214 128 218 144 / 228 266 384 270 | 4 m across | 3 | 4 m across |

Heights are twice the old ones, except R9 at 2.5 times (the core chamber must feel huge: the Reapers' first shots fly
across 64 m) and R16 at 56 m, room for a 43.5 m pod and the crane over it.

Way lengths (every door open, the shortest way from beat to beat): the Locator's whole route spawn_l7 -> hack_s ->
hack_h -> hack_core -> switch_g1 -> hack_d5 -> pickup_bolter -> repair_station -> pod_3_hatch goes from 497 m to 1111 m
(x2.24). A Grunt's route goes from 539 m to 1193 m (x2.21). Hacks, films, fights and waits do not grow, so the play time
should grow by roughly 1.6 to 2 times.

## 6. The escape pod's size

**One escape pod for the bay, the Abwurf film and outside: the game's Pod at its own size (scale 1).** It is 47.5 m across
as drawn with its landing pads, has a 28.7 m footprint and stands 43.5 m tall. That is the game's true Pod-to-Grunt
proportion, which the player asked for. It also ends stage 5's mismatch, because the landing outside already shows the Pod
at its own size (about 45 m). The bay is built for it: R16 is 168 x 96 x 56 m, the pods stand 52 m apart, and a pod's drawn
edge is 8 m from the walls and 6 m from the south wall.

The deck only holds the pods' markers. The next step must make the rest match:

- `Game.DeckShow`: `PodWidth = 7` and the clamp `Min(PodWidth / size, 1)` must go, so pods are drawn at scale 1.
- `abwurf.cut`: change `scale=0.154` to 1 (two `prop pod` lines) and the heights of its sparks, light and fall
  (`pod_3@9`, `above=14`, `pod_3@-40`).
- `DeckLayout`: `pod_cradle`'s ring is 10 m across and must hold a 47.5 m pod (scale it to about 50 m).
  `FindCrashFires` treats a pod within 10 m of the hatch as the launched one; the hatch is now 20 m from pod 3, so use
  about 30 m. Fires sit 4 m north of each broken pod; use about 26 m.
- The pods are not obstacles in the sim, so a machine could drive through one. If that matters, give each pod a 29 m
  footprint, as an artefact in the scenario or as cells.

## 7. What the next step must port

The deck, the greybox `.csp`, the scenario `.scn`, `prolog.psf`, `prolog.png` and the reach report are new. Nothing that
reads them has been changed yet. The deck tests fail as expected: 9 of DeckNavTests and DeckLayoutTests, all on old
coordinates, widths or heights. The parse and the nav map test pass, and the nav map is in
`scratchpad/prologue/stage6-shots/deck/nav-map.png`.

- **The sim's size classes on this deck.** `Mobility.Room(size)` is `size + 0.5` (1.5 / 2.5 / 3.5 m, gaps 3 / 5 / 7). On the
  2x deck it must be `2 size + 0.5` (2.5 / 4.5 / 6.5 m, gaps 5 / 9 / 13), as `tools/deck/deck.mjs` checks it. A crawling
  machine in duct and lip cells keeps 1.5 m, because the ducts are still 4 m. With the old rooms every class fits
  everywhere, so K, S and the bays would no longer keep Reapers out. The stage 5 nav map shows green (Thresher) in every
  room. The new rule could be a deck setting such as a gap scale, or a fixed rule for decks; that is the next step's call.
  `RoomSquared4` and the tests must follow.
- **Every coordinate outside the deck** (use `--map`):
  - `sim/Machines.Sim/Story/Prologue.cs`, for example the `Fall` points "235,40", "249,38" and "252,38", the offsets, and
    the distances and radii it checks;
  - StoryDirector, StoryDriver (routes, checkpoints such as cp4 at 180,106 -> 400,244);
  - `Cinematic.Prolog.cs`, `erwischt.cut` and `abwurf.cut`;
  - the sim-bench prologue mode and the story tests (DeckNavTests, DeckLayoutTests and the rest of
    `Machines.Tests.Story`);
  - camera, minimap and radar bounds;
  - sight and lamp ranges, and fire and search distances, which are game metres and may need doubling with the rooms.
- **DeckLayout constants**:
  - `DoorHeight 4`, `GateHeight 7`, `BlastHeight 7` and `WideBlastHeight 8` should become about 8 / 14 / 14 / 16, matching
    the door leaves deck.mjs now writes;
  - `DefaultHeight 8` should become 16;
  - `LampSpacing 8` and `WallLampSpacing 12` should become 16 and 24;
  - `ShadowedRoom 12` should become about 30, since now almost every room is 12 m tall or more (the lights test fails on
    this);
  - the clamp's hoist stretch `room / 7` should become `room / 14`, and the fittings' spacings should double;
  - `FrameLintel` stays: the frame is fitted to each slot.
- **Barricades.** The barricade markers kept their old count, three a door, now 4 m apart, for doors of 12 m (I), 16 m (J)
  and 8 m (K), and crate_large is still 3 m wide. The core fight step should add markers or crates; R9 has six loose crates
  for it (ids 305-310).
- **Performance.** The fine nav grid has 264,600 cells, 5.4 times as many (48,600 before). Check sim-bench `story` and
  `prologue` after the port.
- Old prologue replays and saved checkpoints are void (accepted).

## 8. The port (stage 6, second step)

What reads the deck now goes by the 2x deck:

- **Size classes.** The deck says its scale on its DECK line (`DECK prolog 630 420 2`; `Deck.GapScale`, 1 when left out).
  `NavGrid` measures the room on land with `Mobility.Room(size, scale)` = scale x size + 0.5 (2.5 / 4.5 / 6.5 m here), and
  a crawler's room with the plain classes (1.5 m), as the ducts kept their cross-section. Every other deck, planet and
  test deck is scale 1 and unchanged. The scale is in the deck's fingerprint only when it is not 1.
- **Grate windows into ducts.** `tools/deck/scale.mjs` now lets a grate beside a duct reach the duct's kept band; the first
  2x deck had 2 m of wall between the V2 duct and the grate windows of P1 and P2, so nobody saw or shot through them.
- **Coordinates** moved with the mapping above: `Prologue.cs` (starts, Reapers at the core, Grunts at the cable duct and
  D5, gate places, the Flank and patrol points, blasts and falls), the driver's Notgang point, `erwischt.cut` (places;
  heights in the room doubled, heights on machines kept), the story tests. The cp3 Grunts stand at 219 / 213, 236 (208,236
  is crate_r9_2). crate_r9_5 and crate_r9_6 moved from door M's mouth (they shut the Reapers out of P2) to 218,284 and
  218,289.
- **Distances** (machines kept their size, so what belongs to a machine stayed, what belongs to a room grew):
  the director's door reach 4.5 -> 8 m (doors are 4 m deep), the gates' push reach 10 -> 14 m, the data link's call
  reach 80 -> 120 m, a Grunt left behind at a duct 10 -> 16 m, the falls' lane offset 2.5 -> 5 m, the blasts' reach and
  size doubled, the scare's trigger 9 -> 16 m, the Thresher at the false-alarm stack 30 -> 60 m, the pod's refusal 6 ->
  10 m. **Fire reach stays 40 m** (STORY.md's fair rule): the Reapers and the trio kept their size and speed, so 40 m is
  still the same number of body lengths and the same seconds of driving out of it; the bigger rooms give the trio more
  ways out of a line of fire rather than fewer, which is what the player asked for in the core. The 1.2 s lock, the
  Thresher's searchlight (40 m), hearing (30 m a point) and touch (6 m) stay for the same reason.
- **View:** door, gate and blast heights 8 / 14 / 14 / 16 m (a blast door counts as wide from 16 m), rooms without a height
  16 m, ceiling lamps every 16 m hanging at most 12 m up, wall lamps every 24 m reaching 14 m, a shadowed middle lamp in
  rooms from 30 m (R9, R16), lamps fading from 72 m; pipes, pillars, the speaker and cables spaced for the bigger rooms;
  the crane only in the pod bay; consoles and racks find their cover within 4 m. The tracker reaches 60 m, sounds 140 m
  (voices 150 m), the hunted / uneasy music 20 / 50 m, the reverb by the doubled sizes, the beacons' beams 24 m. On a deck
  the first-person view is drawn back from a wall it noses into.
- **Pods** at scale 1 in the bay (`Game.DeckShow`) and in `abwurf.cut` (its look, light, sparks and fall heights for a
  43.5 m pod), the cradle ring 5x, fires 26 m north of a broken pod, the launched one found within 30 m of the hatch.
- **The dark start** (sections 1 and 2): the ambient light at 45 % and the emergency lamps at 45 % (`PlanetLook.Gloom`,
  `DeckView.SetGloom`), lifting over about 3 s once the trio is through to the core. **The trio's lamps** are damaged
  (`ActorView.LampDamaged`, `LampFlicker`): 40 % of a sound lamp, with an unsteady hum and every 3 to 10 s a dip, a stutter
  or now and then a cut-out of up to a second, each lamp its own rhythm. The view's alone: the sim's lamp, the signature
  and the hunters' sight do not flicker.

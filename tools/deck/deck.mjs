#!/usr/bin/env node
// Reads a ship deck (data/planets/<p>/<p>.deck, 1 m a character; the format is described at the top of the file and in
// import/Machines.Import/Planets/DeckParser.cs) and writes what is made from it:
//   data/planets/<p>/<p>.csp    the greybox: the deck's walls as obstacles on 4 m cells, which TerrainBuilder draws as
//                               prisms; the sim walks the deck itself on its fine 1 m grid (NavGrid(Deck)) and ignores it
//   data/maps/<p>.scn           the scenario: races, and the doors (closed ones) and props of the deck as artefacts
//   docs/story/deck/<p>.png     a picture of the deck: cells, doors by state, rooms, markers
//   docs/story/deck/<p>-reach.txt  which rooms each mobility class reaches (plan §1.2), with doors as they start and all open
// Usage: node tools/deck/deck.mjs [deck file]   (default data/planets/prolog/prolog.deck)
// Every output is written from the deck alone, so the deck is the one file to edit. tools/deck/scale.mjs imports parseDeck.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, basename, join, resolve } from 'node:path';
import { deflateSync } from 'node:zlib';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

// ---- reading ---------------------------------------------------------------------------------

export function parseDeck(text) {
  const lines = text.split(/\r?\n/);
  const deck = { name: '', width: 0, height: 0, rows: [], doors: [], markers: [], props: [], zones: [], lights: [], cams: [], rails: [] };
  const fail = (i, why) => { throw new Error(`line ${i + 1}: ${why}`); };
  const words = (line) => line.replace(/\/\/.*$/, '').trim().split(/\s+/).filter(Boolean);
  const num = (i, w) => { const v = Number(w); if (!Number.isFinite(v)) fail(i, `'${w}' is no number`); return v; };
  const tags = (ws) => ws;
  for (let i = 0; i < lines.length; i++) {
    const head = words(lines[i]);
    if (head.length === 0) continue;
    const [key] = head;
    if (key === 'DECK') {
      if (head.length !== 4 && head.length !== 5) fail(i, 'expected DECK name width depth [scale]');
      [deck.name, deck.width, deck.height] = [head[1], num(i, head[2]), num(i, head[3])];
      continue;
    }
    if (key === 'GRID') {
      for (i++; i < lines.length && lines[i].trim() !== 'ENDGRID'; i++) {
        const row = lines[i].replace(/\s+$/, '');
        if (row.length !== deck.width) fail(i, `a grid row has ${row.length} characters, not ${deck.width}`);
        deck.rows.push(row);
      }
      if (deck.rows.length !== deck.height) fail(i, `the grid has ${deck.rows.length} rows, not ${deck.height}`);
      continue;
    }
    const section = { DOORS: 'doors', MARKERS: 'markers', PROPS: 'props', ZONES: 'zones', LIGHTS: 'lights', CAMS: 'cams', RAILS: 'rails' }[key];
    if (!section) fail(i, `unexpected '${key}'`);
    for (i++; i < lines.length && words(lines[i])[0] !== `END${key}`; i++) {
      const w = words(lines[i]);
      if (w.length === 0) continue;
      switch (section) {
        case 'doors': if (w.length !== 4 || w[0].length !== 1 || !/[A-Z]/.test(w[0])) fail(i, 'expected letter name kind state');
          deck.doors.push({ letter: w[0], name: w[1], kind: w[2], state: w[3] }); break;
        case 'markers': if (w.length < 4) fail(i, 'expected name x y angle [tags]');
          deck.markers.push({ name: w[0], x: num(i, w[1]), y: num(i, w[2]), angle: num(i, w[3]), tags: tags(w.slice(4)) }); break;
        case 'props': if (w.length !== 6) fail(i, 'expected name type x y angle id');
          deck.props.push({ name: w[0], type: w[1], x: num(i, w[2]), y: num(i, w[3]), angle: num(i, w[4]), id: num(i, w[5]) }); break;
        case 'zones': if (w.length < 5) fail(i, 'expected name x0 y0 x1 y1 [tags]');
          deck.zones.push({ name: w[0], x0: num(i, w[1]), y0: num(i, w[2]), x1: num(i, w[3]), y1: num(i, w[4]), tags: tags(w.slice(5)) }); break;
        case 'lights': if (w.length !== 3) fail(i, 'expected name zone state');
          deck.lights.push({ name: w[0], zone: w[1], state: w[2] }); break;
        case 'cams': if (w.length !== 6) fail(i, 'expected name x y z yaw pitch');
          deck.cams.push({ name: w[0], x: num(i, w[1]), y: num(i, w[2]), z: num(i, w[3]), yaw: num(i, w[4]), pitch: num(i, w[5]) }); break;
        case 'rails': if (w.length < 7 || (w.length - 1) % 3 !== 0) fail(i, 'expected name x y z x y z ...');
          deck.rails.push({ name: w[0], points: w.slice(1).map((v) => num(i, v)) }); break;
      }
    }
  }
  return deck;
}

/** The character at a cell, y north; outside the grid is wall. */
const at = (deck, x, y) => (x < 0 || y < 0 || x >= deck.width || y >= deck.height ? '#' : deck.rows[deck.height - 1 - y][x]);

/** Each door's cells as a rectangle [x0, x1) x [y0, y1). */
function doorRects(deck) {
  const rects = new Map();
  for (let y = 0; y < deck.height; y++)
    for (let x = 0; x < deck.width; x++) {
      const c = at(deck, x, y);
      if (!/[A-Z]/.test(c)) continue;
      const r = rects.get(c) ?? { x0: x, y0: y, x1: x + 1, y1: y + 1, cells: 0 };
      [r.x0, r.y0, r.x1, r.y1] = [Math.min(r.x0, x), Math.min(r.y0, y), Math.max(r.x1, x + 1), Math.max(r.y1, y + 1)];
      r.cells++;
      rects.set(c, r);
    }
  return rects;
}

function check(deck, rects) {
  const problems = [];
  for (const [letter, r] of rects) {
    if (!deck.doors.some((d) => d.letter === letter)) problems.push(`door ${letter} in the grid is not in DOORS`);
    if (r.cells !== (r.x1 - r.x0) * (r.y1 - r.y0)) problems.push(`door ${letter} is not one rectangle`);
  }
  for (const d of deck.doors) if (!rects.has(d.letter)) problems.push(`door ${d.letter} (${d.name}) is not in the grid`);
  for (const m of deck.markers) {
    const c = at(deck, Math.floor(m.x), Math.floor(m.y));
    if (!'.=v^'.includes(c)) problems.push(`marker ${m.name} stands on '${c}'`);
  }
  const zones = new Set(deck.zones.map((z) => z.name));
  for (const l of deck.lights) if (!zones.has(l.zone)) problems.push(`light ${l.name} names no zone ${l.zone}`);
  return problems;
}

// ---- the greybox on the 4 m grid --------------------------------------------------------------

const Coarse = 4;
// Height of what blocks a 4 m cell, by the cells in it: walls stand 8 m, ducts 3, grates 4, cover 2. Door slots are left
// open (the scenario's door artefacts block them), as are floors and catwalks.
const Solid = { '#': 8, v: 3, '^': 3, g: 4, c: 2 };

/** For each 4 m cell, the height that blocks it, or 0: blocked when a quarter or more of its 1 m cells are solid. */
function coarse(deck) {
  const [cw, ch] = [Math.ceil(deck.width / Coarse), Math.ceil(deck.height / Coarse)];
  const grid = Array.from({ length: ch }, () => Array(cw).fill(0));
  for (let cy = 0; cy < ch; cy++)
    for (let cx = 0; cx < cw; cx++) {
      let solid = 0, height = 0;
      for (let y = cy * Coarse; y < cy * Coarse + Coarse; y++)
        for (let x = cx * Coarse; x < cx * Coarse + Coarse; x++) {
          const h = Solid[at(deck, x, y)];
          if (h) { solid++; height = Math.max(height, h); }
        }
      grid[cy][cx] = solid >= (Coarse * Coarse) / 4 ? height : 0;
    }
  return grid;
}

/** The blocked 4 m cells merged into rectangles of one height: runs along x, grown north while the run repeats. */
function rectangles(grid) {
  const used = grid.map((row) => row.map(() => false));
  const out = [];
  for (let y = 0; y < grid.length; y++)
    for (let x = 0; x < grid[0].length; x++) {
      const h = grid[y][x];
      if (!h || used[y][x]) continue;
      let x1 = x;
      while (x1 + 1 < grid[0].length && grid[y][x1 + 1] === h && !used[y][x1 + 1]) x1++;
      let y1 = y;
      const rowFits = (yy) => { for (let xx = x; xx <= x1; xx++) if (grid[yy][xx] !== h || used[yy][xx]) return false; return true; };
      while (y1 + 1 < grid.length && rowFits(y1 + 1)) y1++;
      for (let yy = y; yy <= y1; yy++) for (let xx = x; xx <= x1; xx++) used[yy][xx] = true;
      out.push({ x0: x * Coarse, y0: y * Coarse, x1: (x1 + 1) * Coarse, y1: (y1 + 1) * Coarse, h });
    }
  return out;
}

function csp(deck, rects) {
  // Each rectangle is drawn in by a quarter metre, so that the corners of the free 4 m cells beside it, which lie on its
  // edges, are not inside it (NavGrid blocks a cell whose centre or any corner is inside an obstacle).
  const inset = 0.25;
  const out = [`// ${deck.name}: the greybox of ${deck.name}.deck on the sim's 4 m grid, written by tools/deck/deck.mjs; do not edit.`,
    '// A 4 m cell is blocked where a quarter or more of its 1 m cells are wall, duct, grate or cover (heights 8, 3, 4, 2 m).', ''];
  for (const r of rects) {
    const [x0, y0, x1, y1] = [r.x0 + inset, r.y0 + inset, r.x1 - inset, r.y1 - inset];
    out.push(`OBSTACLE 4 ${r.h}`, `${x0} ${y0}`, `${x1} ${y0}`, `${x1} ${y1}`, `${x0} ${y1}`, '');
  }
  return out.join('\n');
}

// ---- the scenario ---------------------------------------------------------------------------

// The artefact types the deck's doors and props become: footprint (width along the door, depth through the wall), height,
// hit points and armour. Doors are not to be shot open (the director opens them); the core and crates can be destroyed.
// Door leaves are made for any width (door_leaf_<w>, gate_leaf_<w>, w in metres with the point dropped: 35 is 3.5 m):
// a door's two leaves are as tall as the machines that use it need (DoorLeafHeight). Since the deck was scaled 2x
// (tools/deck/scale.mjs, docs/story/deck/scale.md) the Grunts' doors are 8 m wide, the Reapers' blast doors 12-16 m, and
// the computer core is twice the size it was (8 m round, 28 m tall); crates keep their real size.
const Types = {
  door_small: { w: 4, d: 2, h: 4, hp: 30000, armour: 100 },
  vent_cover: { w: 4, d: 2, h: 3, hp: 30000, armour: 100 },
  crate_large: { w: 3, d: 2, h: 2.2, hp: 150, armour: 5 },
  crate_small: { w: 1.5, d: 1.5, h: 1.5, hp: 60, armour: 2 },
  core: { round: 8, h: 28, hp: 2000, armour: 10 },
};

/** How tall a door's leaves stand for a door of this width: a Grunt's door 8 m, a Reaper's blast door or gate 14-16 m. */
const DoorLeafHeight = (width) => (width <= 8 ? 8 : width <= 14 ? 14 : 16);

/** The type of a door leaf of this width, made on first use. */
function leafType(kind, width, leaf) {
  const type = `${kind === 'gate' ? 'gate_leaf_' : 'door_leaf_'}${String(leaf).replace('.', '')}`;
  Types[type] ??= { w: leaf, d: 2, h: DoorLeafHeight(width), hp: 30000, armour: 100 };
  return type;
}

const fmt = (v) => String(Math.round(v * 1000) / 1000);

function boundary(type) {
  const t = Types[type];
  if (t.round) {
    // An octagon around the round core.
    return Array.from({ length: 8 }, (_, k) => {
      const a = (k + 0.5) * Math.PI / 4;
      return [t.round * Math.cos(a) / Math.cos(Math.PI / 8), t.round * Math.sin(a) / Math.cos(Math.PI / 8)];
    });
  }
  const [hw, hd] = [t.w / 2, t.d / 2];
  return [[hw, -hd], [hw, hd], [-hw, hd], [-hw, -hd]];
}

/** The artefacts a closed door is made of: one for a 4 m door or vent, two leaves for a wider one; ids from 100 + 10 a door. */
function doorArtefacts(deck, rects) {
  const out = [];
  deck.doors.forEach((door, index) => {
    if (door.state === 'open') return;
    const r = rects.get(door.letter);
    const alongX = r.x1 - r.x0 >= r.y1 - r.y0;
    const width = alongX ? r.x1 - r.x0 : r.y1 - r.y0;
    const [cx, cy] = [(r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2];
    const angle = alongX ? 0 : 90;
    const id = 100 + 10 * index;
    if (door.kind === 'vent') { out.push({ type: 'vent_cover', x: cx, y: cy, angle, id, door }); return; }
    if (width <= 4) { out.push({ type: 'door_small', x: cx, y: cy, angle, id, door }); return; }
    const leaf = width / 2;
    const type = leafType(door.kind, width, leaf);
    for (const [k, side] of [[0, -1], [1, 1]]) {
      const off = side * leaf / 2;
      out.push({ type, x: alongX ? cx + off : cx, y: alongX ? cy : cy + off, angle, id: id + k, door });
    }
  });
  return out;
}

function scn(deck, rects) {
  const pieces = [...doorArtefacts(deck, rects), ...deck.props.map((p) => ({ ...p, prop: p }))];
  const spawn = deck.markers.find((m) => m.name === 'spawn_l7') ?? { x: deck.width / 2, y: deck.height / 2 };
  const out = [
    `// The story's ${deck.name}: written by tools/deck/deck.mjs from data/planets/${deck.name}/${deck.name}.deck; edit the deck, not this.`,
    '// RED is the trio (the story\'s heroes), BLUE the ship. No machines: the story\'s director sets everything down.',
    '// Doors closed at the start and the props that move or break are artefacts (ids: doors 100 + 10 a door in DOORS order,',
    '// the second leaf one more; props as the deck gives them).',
    'RACES_DEFAULT', '\tRED PC', '\tBLUE AI', 'ENDRACES_DEFAULT', '',
    'ARTEFACTS', `MODELS models/planet/${deck.name}/${deck.name}.arf`,
  ];
  for (const type of Object.keys(Types)) {
    const placed = pieces.filter((p) => p.type === type);
    if (placed.length === 0) continue;
    const t = Types[type];
    out.push(`ACTIVE ${type} COST 0 HP ${t.hp} ARMOUR ${t.armour} IDS 0 HEIGHT ${t.h} BOUNDARY ${boundary(type).flat().map(fmt).join(' ')}`);
    for (const p of placed) {
      const why = p.door ? `door ${p.door.letter} ${p.door.name} (${p.door.state})` : `prop ${p.prop.name}`;
      out.push(`AT ${fmt(p.x)} ${fmt(p.y)} 0 ${fmt(p.angle)} ID ${p.id}\t// ${why}`);
    }
  }
  out.push('ENDARTEFACTS', '',
    'RACE RED none', `CAMERA_POS ${fmt(spawn.x)} ${fmt(spawn.y)} 1.5 90 0 0 GROUND`, 'NBMU 0', 'NORE 0', 'ENDRACE', '',
    'RACE BLUE none', `CAMERA_POS ${fmt(deck.width / 2)} ${fmt(deck.height / 2)} 100 -90 55 0 ZENITH`, 'NBMU 0', 'NORE 0', 'ENDRACE', '');
  return out.join('\n');
}

// ---- reachability ------------------------------------------------------------------------------

// The mobility classes of plan §1.2: the clearance a machine needs at a cell's centre (distance to the nearest blocked
// cell), and whether it crawls through ducts. Measured on the 1 m cells, as the sim's fine grid does (NavGrid.Room).
// The deck was scaled by GapScale (docs/story/deck/scale.md): every gap is that many times as wide as the plan's, so the
// classes' gaps grow with it. A class that passed a gap of g m on the old deck passes (g x GapScale - 1) m now: Grunts 5,
// Reapers and the Commander 9, the Thresher 13 (their drawn bodies are about 4.5-6.5, 10-11 and 15 m wide). The ducts kept
// their 4 m cross-section, so the crawling Locator keeps its old need of 1.5 m (a 3 m gap).
// The deck says so on its DECK line (DECK prolog 630 420 2), which the sim reads (Deck.GapScale, NavGrid.Room).
export const GapScale = 2;
const need = (gap) => (gap * GapScale - 1) / 2;
const Classes = [
  { name: 'Locator (A, crawls)', need: 1.5, crawl: true, from: 'spawn_l7' },
  { name: 'Grunt (A, wheels)', need: need(3), crawl: false, from: 'spawn_ike' },
  { name: 'Reaper/Commander (B)', need: need(5), crawl: false, from: 'spawn_reaper_1' },
  { name: 'Thresher (C)', need: need(7), crawl: false, from: 'spawn_thresher' },
];

function blockedFor(deck, cls, doorsOpen) {
  const state = new Map(deck.doors.map((d) => [d.letter, d.state]));
  const b = new Uint8Array(deck.width * deck.height);
  for (let y = 0; y < deck.height; y++)
    for (let x = 0; x < deck.width; x++) {
      const c = at(deck, x, y);
      let blocked = '#gc'.includes(c) || (!cls.crawl && 'v^'.includes(c));
      if (/[A-Z]/.test(c)) blocked = !doorsOpen && state.get(c) !== 'open';
      b[y * deck.width + x] = blocked ? 1 : 0;
    }
  return b;
}

/** Each cell's distance from its centre to the nearest blocked cell (or the edge), up to 8 m. */
function clearance(deck, blocked) {
  const { width: w, height: h } = deck;
  const out = new Float32Array(w * h);
  const reach = 8;
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      if (blocked[y * w + x]) continue;
      let best = reach;
      for (let dy = -reach; dy <= reach; dy++)
        for (let dx = -reach; dx <= reach; dx++) {
          const [nx, ny] = [x + dx, y + dy];
          const isBlocked = nx < 0 || ny < 0 || nx >= w || ny >= h || blocked[ny * w + nx];
          if (!isBlocked) continue;
          // From the centre (x + 0.5, y + 0.5) to the nearest point of the square [nx, nx+1] x [ny, ny+1].
          const ex = Math.max(nx - (x + 0.5), 0, x + 0.5 - (nx + 1));
          const ey = Math.max(ny - (y + 0.5), 0, y + 0.5 - (ny + 1));
          best = Math.min(best, Math.hypot(ex, ey));
        }
      out[y * w + x] = best;
    }
  return out;
}

function flood(deck, ok, start) {
  const { width: w, height: h } = deck;
  const seen = new Uint8Array(w * h);
  if (start < 0) return seen;
  const queue = [start];
  seen[start] = 1;
  while (queue.length) {
    const i = queue.pop();
    const [x, y] = [i % w, Math.floor(i / w)];
    for (const [nx, ny] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = ny * w + nx;
      if (!seen[j] && ok(j)) { seen[j] = 1; queue.push(j); }
    }
  }
  return seen;
}

/** The cell nearest a marker (within 3 m) that passes <paramref name="ok"/>, or -1. */
function startCell(deck, marker, ok) {
  let best = -1, bestD = Infinity;
  for (let dy = -3; dy <= 3; dy++)
    for (let dx = -3; dx <= 3; dx++) {
      const [x, y] = [Math.floor(marker.x) + dx, Math.floor(marker.y) + dy];
      if (x < 0 || y < 0 || x >= deck.width || y >= deck.height) continue;
      const j = y * deck.width + x;
      const d = dx * dx + dy * dy;
      if (ok(j) && d < bestD) [best, bestD] = [j, d];
    }
  return best;
}

const rooms = (deck) => deck.zones.filter((z) => z.tags.includes('room'));

function reached(deck, seen) {
  return rooms(deck).filter((z) => {
    for (let y = z.y0; y < z.y1; y++) for (let x = z.x0; x < z.x1; x++) if (seen[y * deck.width + x]) return true;
    return false;
  }).map((z) => z.name);
}

function reachReport(deck, coarseGrid) {
  const lines = [`Reachability on ${deck.name}.deck (tools/deck/deck.mjs). Rooms are the ZONES tagged room; 4-connected cells.`, ''];
  const result = {};
  for (const doorsOpen of [false, true]) {
    lines.push(doorsOpen ? '## Every door open' : '## Doors as they start (only DOORS marked open)');
    for (const cls of Classes) {
      const blocked = blockedFor(deck, cls, doorsOpen);
      const clear = clearance(deck, blocked);
      const ok = (j) => !blocked[j] && clear[j] >= cls.need;
      const from = deck.markers.find((m) => m.name === cls.from);
      const seen = flood(deck, ok, from ? startCell(deck, from, ok) : -1);
      const got = reached(deck, seen);
      result[`${cls.name}|${doorsOpen}`] = got;
      const missed = rooms(deck).map((z) => z.name).filter((n) => !got.includes(n));
      lines.push(`- ${cls.name} from ${cls.from}: reaches ${got.join(' ') || 'nothing'}`, `    not: ${missed.join(' ') || '-'}`);
    }
    lines.push('');
  }
  // The 4 m greybox of today's sim: what the Locator can walk with the doors as they start and with all open.
  const w = coarseGrid[0].length;
  for (const doorsOpen of [false, true]) {
    const closedDoor = new Set(deck.doors.filter((d) => doorsOpen ? false : d.state !== 'open').map((d) => d.letter));
    const free = (cx, cy) => {
      if (coarseGrid[cy]?.[cx] !== 0) return false;
      for (let y = cy * Coarse; y < cy * Coarse + Coarse; y++)
        for (let x = cx * Coarse; x < cx * Coarse + Coarse; x++) if (closedDoor.has(at(deck, x, y))) return false;
      return true;
    };
    const spawn = deck.markers.find((m) => m.name === 'spawn_l7');
    const [sx, sy] = [Math.floor(spawn.x / Coarse), Math.floor(spawn.y / Coarse)];
    const seen = new Set();
    const queue = free(sx, sy) ? [[sx, sy]] : [];
    queue.forEach(([x, y]) => seen.add(`${x},${y}`));
    while (queue.length) {
      const [x, y] = queue.pop();
      for (const [nx, ny] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
        const k = `${nx},${ny}`;
        if (nx >= 0 && ny >= 0 && nx < w && ny < coarseGrid.length && !seen.has(k) && free(nx, ny)) { seen.add(k); queue.push([nx, ny]); }
      }
    }
    const got = rooms(deck).filter((z) => [...seen].some((k) => {
      const [cx, cy] = k.split(',').map(Number);
      const [mx, my] = [cx * Coarse + 2, cy * Coarse + 2];
      return mx >= z.x0 && mx < z.x1 && my >= z.y0 && my < z.y1;
    })).map((z) => z.name);
    result[`greybox|${doorsOpen}`] = got;
    lines.push(`## Greybox (4 m cells of the .csp), Locator from spawn_l7, ${doorsOpen ? 'every door open' : 'doors as they start'}`,
      `- reaches ${got.join(' ') || 'nothing'}`, '');
  }
  // What plan §1.2 asks of the layout, with every door open.
  const all = rooms(deck).map((z) => z.name);
  const ducts = all.filter((n) => /^V/.test(n));
  const get = (cls) => result[`${Classes.find((c) => c.name.startsWith(cls)).name}|true`];
  const checks = [
    ['the Locator reaches every room', all.filter((n) => !get('Locator').includes(n))],
    ['Grunts reach no duct', get('Grunt').filter((n) => ducts.includes(n))],
    ['Grunts reach every room but the ducts and the Schaltraum (R7, ducts only)', all.filter((n) => !ducts.includes(n) && n !== 'R7' && !get('Grunt').includes(n))],
    ['Reapers and the Commander never enter K, S, the ducts, the bays, the Lager, the Schaltraum or R13-R16',
      get('Reaper').filter((n) => ['K', 'S', 'R0', 'R1', 'R2', 'R3', 'R5', 'R7', 'R13', 'R13a', 'R14', 'R15', 'R15a', 'R16', ...ducts].includes(n))],
    ['the Thresher keeps to its ring R4 WL R6 EL', get('Thresher').filter((n) => !['R4', 'WL', 'R6', 'EL'].includes(n))],
  ];
  lines.push('## Checks (every door open)');
  for (const [what, wrong] of checks) lines.push(`${wrong.length === 0 ? 'PASS' : 'FAIL'} ${what}${wrong.length ? `: ${wrong.join(' ')}` : ''}`);
  lines.push('', 'Note: with every door open the Reapers reach the decks through R8 (12 m doors); in the story the blast doors H and I',
    'and the barricades keep them in R9, R10, P1, P2 and R12.');
  return { text: lines.join('\n') + '\n', failed: checks.filter(([, wrong]) => wrong.length > 0).length };
}

// ---- picture ---------------------------------------------------------------------------------

const Colour = {
  '#': [38, 40, 44], '.': [196, 198, 190], v: [228, 140, 40], '^': [170, 90, 20], g: [80, 150, 210], c: [140, 100, 60], '=': [90, 170, 160],
};
const DoorColour = { locked: [210, 40, 40], closed: [235, 200, 40], open: [70, 190, 80] };
const MarkerColour = {
  hero: [0, 200, 255], hostile: [220, 30, 30], reaper: [220, 30, 30], commander: [255, 0, 160], thresher: [255, 80, 0],
  patrol: [255, 140, 0], clamp: [110, 110, 160], hack: [160, 0, 220], barricade: [120, 70, 20], pod: [255, 255, 255],
};

// A 3 x 5 pixel font for the rooms' names.
const Font = {
  A: '010101111101101', B: '110101110101110', C: '011100100100011', D: '110101101101110', E: '111100110100111', F: '111100110100100',
  G: '011100101101011', H: '101101111101101', I: '111010010010111', K: '101101110101101', L: '100100100100111', M: '101111111101101',
  N: '110101101101101', O: '010101101101010', P: '110101110100100', R: '110101110101101', S: '011100010001110', T: '111010010010010',
  V: '101101101101010', W: '101101111111101', a: '000011101101011', s: '000011110011110',
  0: '111101101101111', 1: '010110010010111', 2: '110001010100111', 3: '110001010001110', 4: '101101111001001', 5: '111100110001110',
  6: '011100111101111', 7: '111001010010010', 8: '111101111101111', 9: '111101111001110',
};

function png(deck, rects) {
  const scale = deck.width > 400 ? 2 : 4;
  const [w, h] = [deck.width * scale, deck.height * scale];
  const px = new Uint8Array(w * h * 3);
  const put = (x, y, [r, g, b]) => { if (x < 0 || y < 0 || x >= w || y >= h) return; const i = (y * w + x) * 3; px[i] = r; px[i + 1] = g; px[i + 2] = b; };
  // Image y runs down; the deck's north is up.
  const cell = (x, y, colour) => { for (let j = 0; j < scale; j++) for (let i = 0; i < scale; i++) put(x * scale + i, (deck.height - 1 - y) * scale + j, colour); };
  const state = new Map(deck.doors.map((d) => [d.letter, d.state]));
  for (let y = 0; y < deck.height; y++)
    for (let x = 0; x < deck.width; x++) {
      const c = at(deck, x, y);
      cell(x, y, /[A-Z]/.test(c) ? DoorColour[state.get(c)] ?? [255, 0, 255] : Colour[c] ?? [255, 0, 255]);
    }
  // Room outlines and names.
  for (const z of rooms(deck)) {
    const [x0, x1, y0, y1] = [z.x0 * scale, z.x1 * scale - 1, (deck.height - z.y1) * scale, (deck.height - z.y0) * scale - 1];
    for (let x = x0; x <= x1; x++) { put(x, y0, [255, 255, 255]); put(x, y1, [255, 255, 255]); }
    for (let y = y0; y <= y1; y++) { put(x0, y, [255, 255, 255]); put(x1, y, [255, 255, 255]); }
    let tx = x0 + 3;
    for (const ch of z.name) {
      const glyph = Font[ch];
      if (glyph) for (let k = 0; k < 15; k++) if (glyph[k] === '1') for (let s = 0; s < 4; s++) put(tx + (k % 3) * 2 + (s & 1), y0 + 3 + Math.floor(k / 3) * 2 + (s >> 1), [20, 20, 120]);
      tx += 8;
    }
  }
  // Markers as small squares, coloured by their first known tag.
  for (const m of deck.markers) {
    const colour = MarkerColour[m.tags.find((t) => MarkerColour[t])] ?? [0, 0, 0];
    const [cx, cy] = [Math.round(m.x * scale), Math.round((deck.height - m.y) * scale)];
    for (let j = -3; j <= 3; j++) for (let i = -3; i <= 3; i++) put(cx + i, cy + j, colour);
    const a = m.angle * Math.PI / 180;
    for (let k = 3; k < 9; k++) put(Math.round(cx + Math.cos(a) * k), Math.round(cy - Math.sin(a) * k), colour);
  }
  // PNG: 8-bit RGB, rows with filter 0.
  const raw = Buffer.alloc((w * 3 + 1) * h);
  for (let y = 0; y < h; y++) { raw[y * (w * 3 + 1)] = 0; Buffer.from(px.buffer, y * w * 3, w * 3).copy(raw, y * (w * 3 + 1) + 1); }
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

// ---- main ------------------------------------------------------------------------------------

// Run as a script (not when scale.mjs imports parseDeck).
if (process.argv[1] && import.meta.url.toLowerCase() === pathToFileURL(resolve(process.argv[1])).href.toLowerCase()) main();

function main() {
  const deckPath = resolve(process.argv[2] ?? join(repo, 'data', 'planets', 'prolog', 'prolog.deck'));
  const deck = parseDeck(readFileSync(deckPath, 'utf8'));
  const rects = doorRects(deck);
  const problems = check(deck, rects);
  for (const p of problems) console.error(`deck: ${p}`);
  if (problems.length) process.exit(1);
  const grid = coarse(deck);
  const planetFolder = dirname(deckPath);
  const name = basename(deckPath, '.deck');
  const outputs = [
    [join(planetFolder, `${name}.csp`), csp(deck, rectangles(grid))],
    [join(repo, 'data', 'maps', `${name}.scn`), scn(deck, rects)],
  ];
  const report = reachReport(deck, grid);
  mkdirSync(join(repo, 'docs', 'story', 'deck'), { recursive: true });
  outputs.push([join(repo, 'docs', 'story', 'deck', `${name}-reach.txt`), report.text]);
  for (const [file, text] of outputs) writeFileSync(file, text);
  writeFileSync(join(repo, 'docs', 'story', 'deck', `${name}.png`), png(deck, rects));
  console.log(report.text);
  console.log(`wrote ${outputs.map(([f]) => f).join(', ')} and ${name}.png`);
  if (report.failed) process.exitCode = 2;
}

#!/usr/bin/env node
// Scales a ship deck (data/planets/<p>/<p>.deck) as a whole by a whole factor F, by a plan file such as
// tools/deck/prolog-x2.plan, and writes the new deck. docs/story/deck/scale.md tells why the prologue's deck was scaled.
//
//   node tools/deck/scale.mjs <plan> <old deck> <new deck>      scale; prints the door table, rooms and way lengths
//   node tools/deck/scale.mjs <plan> --map x,y [x,y ...]         where old deck points lie on the new deck
//
// The prologue's deck was made from the stage 5 deck (commit 65a7b84):
//   git show 65a7b84:data/planets/prolog/prolog.deck > old.deck
//   node tools/deck/scale.mjs tools/deck/prolog-x2.plan old.deck data/planets/prolog/prolog.deck
//   node tools/deck/deck.mjs          (the greybox, scenario, picture and reach report from the new deck)
//
// What the scale does (F = 2 for the words below):
// - Every cell becomes F x F cells; every x, y in MARKERS, PROPS, ZONES, CAMS and RAILS is multiplied by F, and so is z in
//   CAMS and RAILS (the rooms grow as tall); marker angles stay.
// - Crawl ducts (v, their ^ lips and the vent doors in them) keep their cross-section: a 4 m duct stays 4 m across, only
//   longer; the cells it leaves beside it become wall. A vent door keeps its depth on the side of the room it opens to, the
//   rest of its slot becomes duct. Zones and markers in a duct move onto what is left of it.
// - Every other door scales with its wall: a 2 m door becomes 4 m.
// - Stretches (plan: stretch x|y <old coordinate> <cells>) then put extra way in: that many copies of the cells just west
//   (or south) of the old coordinate are put in there, and everything east (or north) moves on. Choose lines that cross
//   only wall, floor and ducts along their length (no door, grate or cover is copied).
// - ZONES height= grows F times, but ducts keep theirs; the plan can set a room's height outright.
// - The plan's own edits come last, in the new deck's coordinates: fill (cells), marker, zone, prop and cam (each moves or
//   replaces the one of that name, or adds it).
// The mapping of an old point (x, y) is x' = F x + the cells of every x stretch at or west of x, y' likewise; a zone's
// far edge only moves with stretches it lies beyond.
import { readFileSync, writeFileSync } from 'node:fs';
import { parseDeck } from './deck.mjs';

const fail = (why) => { console.error(`scale: ${why}`); process.exit(1); };

// ---- the plan ----------------------------------------------------------------------------------

function readPlan(path) {
  const plan = { factor: 0, stretches: [], pad: null, heights: new Map(), fills: [], markers: [], zones: [], props: [], cams: [], header: [] };
  for (const [i, raw] of readFileSync(path, 'utf8').split(/\r?\n/).entries()) {
    const line = raw.replace(/\/\/.*$/, '').trim();
    if (!line) continue;
    const w = line.split(/\s+/);
    const n = (k) => { const v = Number(w[k]); if (!Number.isFinite(v)) fail(`${path}:${i + 1}: '${w[k]}' is no number`); return v; };
    switch (w[0]) {
      case 'factor': plan.factor = n(1); break;
      case 'stretch': plan.stretches.push({ axis: w[1], at: n(2), by: n(3), why: raw.split('//')[1]?.trim() ?? '' }); break;
      case 'pad': plan.pad = [n(1), n(2)]; break;
      case 'height': plan.heights.set(w[1], n(2)); break;
      case 'fill': plan.fills.push({ c: w[1], x0: n(2), y0: n(3), x1: n(4), y1: n(5) }); break;
      case 'marker': plan.markers.push({ name: w[1], x: n(2), y: n(3), angle: w[4] !== undefined ? n(4) : null, tags: w.slice(5) }); break;
      case 'zone': plan.zones.push({ name: w[1], x0: n(2), y0: n(3), x1: n(4), y1: n(5), tags: w.slice(6) }); break;
      case 'prop': plan.props.push({ name: w[1], type: w[2], x: n(3), y: n(4), angle: n(5), id: n(6) }); break;
      case 'cam': plan.cams.push({ name: w[1], x: n(2), y: n(3), z: n(4), yaw: n(5), pitch: n(6) }); break;
      case 'header': plan.header.push(raw.replace(/^\s*header\s?/, '')); break;
      default: fail(`${path}:${i + 1}: unknown '${w[0]}'`);
    }
  }
  if (!Number.isInteger(plan.factor) || plan.factor < 1) fail('the plan needs a whole factor');
  return plan;
}

/** Old coordinate to new along one axis: a point (or a zone's near edge) moves with every stretch at or before it. */
function mapper(plan, axis) {
  const list = plan.stretches.filter((s) => s.axis === axis);
  return {
    point: (v) => plan.factor * v + list.filter((s) => v >= s.at).reduce((a, s) => a + s.by, 0),
    far: (v) => plan.factor * v + list.filter((s) => v > s.at).reduce((a, s) => a + s.by, 0),
  };
}

// ---- the grid --------------------------------------------------------------------------------

const isDuct = (c) => c === 'v' || c === '^';

/** The ducts of the old deck: straight runs of duct cells and the vent doors in them, with their bounds and axis. */
function ducts(deck) {
  const vents = new Set(deck.doors.filter((d) => d.kind === 'vent').map((d) => d.letter));
  const { width: w, height: h } = deck;
  const at = (x, y) => deck.rows[h - 1 - y][x];
  const ductish = (x, y) => x >= 0 && y >= 0 && x < w && y < h && (isDuct(at(x, y)) || vents.has(at(x, y)));
  const seen = new Uint8Array(w * h);
  const out = [];
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      if (seen[y * w + x] || !ductish(x, y)) continue;
      const cells = [];
      const stack = [[x, y]];
      seen[y * w + x] = 1;
      while (stack.length) {
        const [cx, cy] = stack.pop();
        cells.push([cx, cy]);
        for (const [nx, ny] of [[cx + 1, cy], [cx - 1, cy], [cx, cy + 1], [cx, cy - 1]])
          if (ductish(nx, ny) && !seen[ny * w + nx]) { seen[ny * w + nx] = 1; stack.push([nx, ny]); }
      }
      const b = { x0: Infinity, y0: Infinity, x1: -Infinity, y1: -Infinity };
      for (const [cx, cy] of cells) [b.x0, b.y0, b.x1, b.y1] = [Math.min(b.x0, cx), Math.min(b.y0, cy), Math.max(b.x1, cx + 1), Math.max(b.y1, cy + 1)];
      if (cells.length !== (b.x1 - b.x0) * (b.y1 - b.y0)) fail(`the duct at ${x},${y} is not straight (not one rectangle)`);
      const alongX = b.x1 - b.x0 > b.y1 - b.y0;
      const ventCells = cells.filter(([cx, cy]) => vents.has(at(cx, cy)));
      out.push({ ...b, alongX, vents: ventCells, letters: [...new Set(ventCells.map(([cx, cy]) => at(cx, cy)))] });
    }
  return out;
}

/** The cross-section band a duct keeps on the scaled grid (before stretches), along the axis across it: [b0, b1). */
function band(duct, F) {
  const [c0, c1] = duct.alongX ? [duct.y0, duct.y1] : [duct.x0, duct.x1];
  const k = c1 - c0;
  const b0 = F * c0 + Math.floor((F * k - k) / 2);
  return [b0, b0 + k];
}

function scaleGrid(deck, plan, ductList) {
  const F = plan.factor;
  const { width: w, height: h } = deck;
  const at = (x, y) => deck.rows[h - 1 - y][x];
  // grid[y][x] with y north, as arrays of characters.
  let W = w * F, H = h * F;
  let grid = Array.from({ length: H }, (_, y) => Array.from({ length: W }, (_, x) => at(Math.floor(x / F), Math.floor(y / F))));
  // Ducts keep their cross-section; vent doors their depth on the room's side.
  for (const d of ductList) {
    const [b0, b1] = band(d, F);
    for (let y = d.y0 * F; y < d.y1 * F; y++)
      for (let x = d.x0 * F; x < d.x1 * F; x++) {
        const across = d.alongX ? y : x;
        if (across >= b0 && across < b1) continue;
        // Beside the band it is wall, but where a grate window looked into the duct the grate reaches the band, so the
        // window still looks in (sight and shots pass, nobody does).
        const low = across < b0;
        const [ox, oy] = d.alongX ? [Math.floor(x / F), low ? d.y0 - 1 : d.y1] : [low ? d.x0 - 1 : d.x1, Math.floor(y / F)];
        grid[y][x] = ox >= 0 && oy >= 0 && ox < w && oy < h && at(ox, oy) === 'g' ? 'g' : '#';
      }
    for (const letter of d.letters) {
      const cells = d.vents.filter(([cx, cy]) => at(cx, cy) === letter);
      const s0 = Math.min(...cells.map(([cx, cy]) => (d.alongX ? cx : cy)));
      const s1 = Math.max(...cells.map(([cx, cy]) => (d.alongX ? cx : cy))) + 1;
      const depth = s1 - s0;
      // The room is on the side whose neighbour beyond the slot is not a duct.
      const [px, py] = d.alongX ? [s0 - 1, cells[0][1]] : [cells[0][0], s0 - 1];
      const roomBefore = !isDuct(at(px, py));
      const [k0, k1] = roomBefore ? [F * s0, F * s0 + depth] : [F * s1 - depth, F * s1];
      for (let y = d.y0 * F; y < d.y1 * F; y++)
        for (let x = d.x0 * F; x < d.x1 * F; x++) {
          if (grid[y][x] !== letter) continue;
          const along = d.alongX ? x : y;
          if (along < k0 || along >= k1) grid[y][x] = 'v';
        }
    }
  }
  // Stretches, the last first so that the earlier ones' places stay put.
  const order = [...plan.stretches].sort((a, b) => b.at - a.at);
  for (const s of order) {
    const pos = F * s.at;
    if (s.axis === 'x') {
      const copied = grid.map((row) => row[pos - 1]);
      const bad = copied.filter((c) => !'#.v^='.includes(c));
      if (bad.length) fail(`stretch x ${s.at} would copy '${[...new Set(bad)].join('')}'`);
      grid = grid.map((row, y) => [...row.slice(0, pos), ...Array(s.by).fill(copied[y]), ...row.slice(pos)]);
      W += s.by;
    } else {
      const copied = grid[pos - 1];
      const bad = copied.filter((c) => !'#.v^='.includes(c));
      if (bad.length) fail(`stretch y ${s.at} would copy '${[...new Set(bad)].join('')}'`);
      grid = [...grid.slice(0, pos), ...Array.from({ length: s.by }, () => [...copied]), ...grid.slice(pos)];
      H += s.by;
    }
  }
  if (plan.pad) {
    const [PW, PH] = plan.pad;
    if (PW < W || PH < H) fail(`pad ${PW} x ${PH} is smaller than the grid ${W} x ${H}`);
    grid = grid.map((row) => [...row, ...Array(PW - W).fill('#')]);
    while (grid.length < PH) grid.push(Array(PW).fill('#'));
    [W, H] = [PW, PH];
  }
  for (const f of plan.fills)
    for (let y = f.y0; y < f.y1; y++) for (let x = f.x0; x < f.x1; x++) grid[y][x] = f.c;
  return { grid, W, H };
}

// ---- the sections ------------------------------------------------------------------------------

const fmt = (v) => String(Math.round(v * 1000) / 1000);

function inside(d, x0, y0, x1, y1) { return x0 >= d.x0 && y0 >= d.y0 && x1 <= d.x1 && y1 <= d.y1; }

function transform(deck, plan, ductList) {
  const F = plan.factor;
  const [mx, my] = [mapper(plan, 'x'), mapper(plan, 'y')];
  // A point or zone in a duct sits on the kept band (before stretches; the band's offset moves with the duct).
  const ductOf = (x0, y0, x1, y1) => ductList.find((d) => inside(d, x0, y0, x1, y1));
  const markers = deck.markers.map((m) => {
    let [x, y] = [mx.point(m.x), my.point(m.y)];
    const d = ductOf(m.x, m.y, m.x, m.y);
    if (d) {
      // On the middle of the band the duct keeps (stretches move a duct as a whole: they never cut across one).
      const [b0, b1] = band(d, F);
      if (d.alongX) y = (b0 + b1) / 2 + my.point(d.y0) - F * d.y0; else x = (b0 + b1) / 2 + mx.point(d.x0) - F * d.x0;
    }
    return { ...m, x, y };
  });
  const props = deck.props.map((p) => ({ ...p, x: mx.point(p.x), y: my.point(p.y) }));
  const zones = deck.zones.map((z) => {
    const d = ductOf(z.x0, z.y0, z.x1, z.y1);
    let [x0, y0, x1, y1] = [mx.point(z.x0), my.point(z.y0), mx.far(z.x1), my.far(z.y1)];
    if (d) {
      const [b0, b1] = band(d, F);
      // Bands lie between stretch lines (ducts are not cut across by a stretch), so the stretch offset of the duct's
      // near edge applies to the whole band.
      if (d.alongX) { const off = my.point(d.y0) - F * d.y0; [y0, y1] = [b0 + off, b1 + off]; }
      else { const off = mx.point(d.x0) - F * d.x0; [x0, x1] = [b0 + off, b1 + off]; }
    }
    const tags = z.tags.map((t) => {
      const m = /^height=(.*)$/.exec(t);
      if (!m) return t;
      const h = plan.heights.get(z.name) ?? (d ? Number(m[1]) : Number(m[1]) * F);
      return `height=${fmt(h)}`;
    });
    return { ...z, x0, y0, x1, y1, tags };
  });
  const cams = deck.cams.map((c) => ({ ...c, x: mx.point(c.x), y: my.point(c.y), z: c.z * F }));
  const rails = deck.rails.map((r) => ({ ...r, points: r.points.map((v, i) => (i % 3 === 0 ? mx.point(v) : i % 3 === 1 ? my.point(v) : v * F)) }));
  // The plan's own edits.
  for (const e of plan.markers) {
    const m = markers.find((k) => k.name === e.name);
    if (m) Object.assign(m, { x: e.x, y: e.y, ...(e.angle !== null ? { angle: e.angle } : {}), ...(e.tags.length ? { tags: e.tags } : {}) });
    else markers.push({ name: e.name, x: e.x, y: e.y, angle: e.angle ?? 0, tags: e.tags });
  }
  for (const e of plan.zones) {
    const z = zones.find((k) => k.name === e.name);
    if (z) Object.assign(z, { x0: e.x0, y0: e.y0, x1: e.x1, y1: e.y1, ...(e.tags.length ? { tags: e.tags } : {}) });
    else zones.push({ ...e });
  }
  for (const e of plan.props) {
    const p = props.find((k) => k.name === e.name);
    if (p) Object.assign(p, e); else props.push({ ...e });
  }
  for (const e of plan.cams) {
    const c = cams.find((k) => k.name === e.name);
    if (c) Object.assign(c, e); else cams.push({ ...e });
  }
  return { markers, props, zones, cams, rails };
}

// ---- writing ---------------------------------------------------------------------------------

function write(oldText, deck, plan, grid, W, H, s) {
  const lines = oldText.split(/\r?\n/);
  const head = [];
  // The old header comments, with the grid's size brought up to date, and the plan's notes.
  for (const line of lines) {
    if (!line.startsWith('//')) break;
    head.push(line.replace(/\(y = \d+\)/, `(y = ${H - 1})`));
  }
  const out = [...head, ...plan.header.map((l) => `// ${l}`.trimEnd()), '', `DECK ${deck.name} ${W} ${H} ${plan.factor}`, 'GRID'];
  for (let y = H - 1; y >= 0; y--) out.push(grid[y].join(''));
  out.push('ENDGRID', '', 'DOORS', ...deck.doors.map((d) => `${d.letter} ${d.name} ${d.kind} ${d.state}`), 'ENDDOORS', '');
  out.push('MARKERS', ...s.markers.map((m) => [m.name, fmt(m.x), fmt(m.y), fmt(m.angle), ...m.tags].join(' ')), 'ENDMARKERS', '');
  out.push('PROPS', ...s.props.map((p) => [p.name, p.type, fmt(p.x), fmt(p.y), fmt(p.angle), p.id].join(' ')), 'ENDPROPS', '');
  out.push('ZONES', ...s.zones.map((z) => [z.name, fmt(z.x0), fmt(z.y0), fmt(z.x1), fmt(z.y1), ...z.tags].join(' ')), 'ENDZONES', '');
  out.push('LIGHTS', ...deck.lights.map((l) => `${l.name} ${l.zone} ${l.state}`), 'ENDLIGHTS', '');
  out.push('CAMS', ...s.cams.map((c) => [c.name, fmt(c.x), fmt(c.y), fmt(c.z), fmt(c.yaw), fmt(c.pitch)].join(' ')), 'ENDCAMS', '');
  out.push('RAILS', ...s.rails.map((r) => [r.name, ...r.points.map(fmt)].join(' ')), 'ENDRAILS', '');
  return out.join('\n');
}

// ---- the report --------------------------------------------------------------------------------

function doorRects(d) {
  const rects = new Map();
  for (let y = 0; y < d.height; y++)
    for (let x = 0; x < d.width; x++) {
      const c = d.rows[d.height - 1 - y][x];
      if (!/[A-Z]/.test(c)) continue;
      const r = rects.get(c) ?? { x0: x, y0: y, x1: x + 1, y1: y + 1 };
      [r.x0, r.y0, r.x1, r.y1] = [Math.min(r.x0, x), Math.min(r.y0, y), Math.max(r.x1, x + 1), Math.max(r.y1, y + 1)];
      rects.set(c, r);
    }
  return rects;
}

/** Who passes a way this wide: the classes of tools/deck/deck.mjs on the new deck (gaps of 3 crawling, 5, 9, 13 m). */
function passes(width, vent) {
  if (vent) return width >= 3 ? 'Locator (crawling)' : 'nobody';
  const who = [];
  if (width >= 3) who.push('Locator');
  if (width >= 5) who.push('Grunts');
  if (width >= 9) who.push('Reapers, Commander');
  if (width >= 13) who.push('Thresher');
  return who.join(', ') || 'nobody';
}

/** The shortest way (4-connected cells, every door open) between two markers for a machine needing this clearance. */
function way(d, from, to, need, crawl) {
  const { width: w, height: h } = d;
  const blocked = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) blocked[y * w + x] = (crawl ? '#gc' : '#gcv^').includes(d.rows[h - 1 - y][x]) ? 1 : 0;
  // Clearance by a breadth-first distance transform (chessboard steps are close enough for a length).
  const clear = new Float32Array(w * h).fill(Infinity);
  const q = [];
  for (let i = 0; i < w * h; i++) if (blocked[i]) { clear[i] = 0; q.push(i); }
  for (let k = 0; k < q.length; k++) {
    const i = q[k]; const [x, y] = [i % w, Math.floor(i / w)];
    for (const [nx, ny] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = ny * w + nx;
      if (clear[j] > clear[i] + 1) { clear[j] = clear[i] + 1; q.push(j); }
    }
  }
  const ok = (j) => !blocked[j] && clear[j] - 0.5 >= need;
  const start = Math.floor(from.y) * w + Math.floor(from.x), goal = Math.floor(to.y) * w + Math.floor(to.x);
  const dist = new Int32Array(w * h).fill(-1);
  const near = (i) => { // the nearest cell that is ok
    let best = -1, bd = Infinity; const [x0, y0] = [i % w, Math.floor(i / w)];
    for (let dy = -8; dy <= 8; dy++) for (let dx = -8; dx <= 8; dx++) { const j = (y0 + dy) * w + x0 + dx; if (j >= 0 && j < w * h && ok(j) && dx * dx + dy * dy < bd) [best, bd] = [j, dx * dx + dy * dy]; }
    return best;
  };
  const s = near(start), g = near(goal);
  if (s < 0 || g < 0) return null;
  dist[s] = 0; const queue = [s];
  for (let k = 0; k < queue.length; k++) {
    const i = queue[k]; if (i === g) return dist[i];
    const [x, y] = [i % w, Math.floor(i / w)];
    for (const [nx, ny] of [[x + 1, y], [x - 1, y], [x, y + 1], [x, y - 1]]) {
      if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
      const j = ny * w + nx;
      if (dist[j] < 0 && ok(j)) { dist[j] = dist[i] + 1; queue.push(j); }
    }
  }
  return null;
}

function report(oldDeck, newDeck, plan) {
  const out = [];
  const [oldRects, newRects] = [doorRects(oldDeck), doorRects(newDeck)];
  out.push(`grid ${oldDeck.width} x ${oldDeck.height} -> ${newDeck.width} x ${newDeck.height}`, '', 'doors (width across the way, depth through the wall):');
  for (const door of newDeck.doors) {
    const [o, n] = [oldRects.get(door.letter), newRects.get(door.letter)];
    // Across the way: the longer side, but a vent's slot can be as deep as it is wide; then the old door says which side.
    const across = (r, alongX) => (alongX ? r.x1 - r.x0 : r.y1 - r.y0);
    const oldAlongX = o.x1 - o.x0 >= o.y1 - o.y0;
    const [ow, nw] = [across(o, oldAlongX), across(n, oldAlongX)];
    out.push(`  ${door.letter} ${door.name.padEnd(20)} ${door.kind.padEnd(5)} ${door.state.padEnd(6)} ${String(ow).padStart(2)} m -> ${String(nw).padStart(2)} m  at ${n.x0},${n.y0}-${n.x1},${n.y1}  passes: ${passes(nw, door.kind === 'vent')}`);
  }
  out.push('', 'rooms (x0 y0 x1 y1, size, height):');
  for (const z of newDeck.zones.filter((k) => k.tags.includes('room'))) {
    const o = oldDeck.zones.find((k) => k.name === z.name);
    const h = (k) => k?.tags.find((t) => t.startsWith('height='))?.slice(7) ?? '-';
    out.push(`  ${z.name.padEnd(5)} ${[z.x0, z.y0, z.x1, z.y1].join(' ').padEnd(18)} ${`${z.x1 - z.x0} x ${z.y1 - z.y0} m`.padEnd(12)} h ${h(z)} (was ${o ? `${o.x1 - o.x0} x ${o.y1 - o.y0} m, h ${h(o)}` : 'new'})`);
  }
  // The story's way from beat to beat (every door open, shortest 4-connected way): the Locator crawls, a Grunt drives.
  const routes = [
    ['Locator', 1.5, 1.5, true, ['spawn_l7', 'hack_s', 'hack_h', 'hack_core', 'switch_g1', 'hack_d5', 'pickup_bolter', 'repair_station', 'pod_3_hatch']],
    ['Grunt', 1.5, 2.5, false, ['spawn_ike', 'patrol_thresher_1', 'false_alarm_stack', 'crate_pile', 'hack_core', 'switch_g1', 'hack_d5', 'repair_station', 'pod_3_hatch']],
  ];
  for (const [who, oldNeed, newNeed, crawl, stops] of routes) {
    out.push('', `way lengths for the ${who} (every door open, shortest 4-connected way; old -> new):`);
    let [to, tn] = [0, 0];
    for (let k = 0; k + 1 < stops.length; k++) {
      const [a, b] = [stops[k], stops[k + 1]];
      const m = (d, n) => d.markers.find((q) => q.name === n);
      const o = way(oldDeck, m(oldDeck, a), m(oldDeck, b), oldNeed, crawl), n = way(newDeck, m(newDeck, a), m(newDeck, b), newNeed, crawl);
      to += o ?? 0; tn += n ?? 0;
      out.push(`  ${`${a} -> ${b}`.padEnd(36)} ${String(o ?? '-').padStart(4)} m -> ${String(n ?? '-').padStart(4)} m${o && n ? `  x${(n / o).toFixed(2)}` : ''}`);
    }
    out.push(`  ${'whole way'.padEnd(36)} ${String(to).padStart(4)} m -> ${String(tn).padStart(4)} m  x${(tn / to).toFixed(2)}`);
  }
  return out.join('\n');
}

// ---- main ------------------------------------------------------------------------------------

const [planPath, ...rest] = process.argv.slice(2);
if (!planPath) fail('usage: node tools/deck/scale.mjs <plan> <old deck> <new deck> | <plan> --map x,y ...');
const plan = readPlan(planPath);
if (rest[0] === '--map') {
  const [mx, my] = [mapper(plan, 'x'), mapper(plan, 'y')];
  for (const p of rest.slice(1)) {
    const [x, y] = p.split(',').map(Number);
    console.log(`${p} -> ${fmt(mx.point(x))},${fmt(my.point(y))}`);
  }
} else {
  const [oldPath, newPath] = rest;
  if (!oldPath || !newPath) fail('usage: node tools/deck/scale.mjs <plan> <old deck> <new deck>');
  const oldText = readFileSync(oldPath, 'utf8');
  const deck = parseDeck(oldText);
  const ductList = ducts(deck);
  const { grid, W, H } = scaleGrid(deck, plan, ductList);
  const sections = transform(deck, plan, ductList);
  const text = write(oldText, deck, plan, grid, W, H, sections);
  writeFileSync(newPath, text + '\n');
  console.log(report(deck, parseDeck(text), plan));
  console.log(`\nwrote ${newPath}`);
}

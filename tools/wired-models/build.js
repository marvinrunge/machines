// Wired's own machines, put together from the original's exported models (classic/models/<colour>/<id>.json, see
// tools/classic-export) and their textures: scaled, parts thickened, moved or copied from other machines, plates
// added that take a plain patch of a texture the machine already wears. Writes classic/wired/models/<colour>/<id>.json
// and classic/wired/models.txt, which the game finds next to the original's (ClassicAssets.Wired, ModelsPage).
// Wired is the remake's own addition; none of these machines is in the original.
//
//   node tools/wired-models/build.js [recipe ...]     (none: all; "check": rebuild an original unchanged and compare)
//
// Coordinates are the export's: metres, x forward, y left, z up. A node's transform is 12 numbers, its x, y and z
// axes and its origin, in its parent's space; animation samples are the same per frame.
"use strict";
const fs = require("fs");
const path = require("path");
const paint = require("./paint");

const ROOT = path.resolve(__dirname, "..", "..");
const SOURCE = path.join(ROOT, "classic", "models");
const TARGET = path.join(ROOT, "classic", "wired");
const COLOURS = ["red", "blue", "green", "yellow"];

// ---- matrices --------------------------------------------------------------------------------------------------

const IDENTITY = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
const mul = (a, b) => {
    const r = new Array(12);
    for (let c = 0; c < 4; c++)
        for (let i = 0; i < 3; i++)
            r[c * 3 + i] = a[i] * b[c * 3] + a[3 + i] * b[c * 3 + 1] + a[6 + i] * b[c * 3 + 2] + (c === 3 ? a[9 + i] : 0);
    return r;
};
const point = (m, p) => [0, 1, 2].map(i => m[i] * p[0] + m[3 + i] * p[1] + m[6 + i] * p[2] + m[9 + i]);
const vector = (m, v) => [0, 1, 2].map(i => m[i] * v[0] + m[3 + i] * v[1] + m[6 + i] * v[2]);
const normalise = v => { const l = Math.hypot(...v) || 1; return v.map(x => x / l); };
/** A transform turned about z by `deg` and placed at `at`. */
const turnZ = (deg, at = [0, 0, 0]) => {
    const a = deg * Math.PI / 180, c = Math.cos(a), s = Math.sin(a);
    return [c, s, 0, -s, c, 0, 0, 0, 1, ...at];
};
/** A transform tilted about y by `deg` (nose down for positive) and placed at `at`. */
const tiltY = (deg, at = [0, 0, 0]) => {
    const a = deg * Math.PI / 180, c = Math.cos(a), s = Math.sin(a);
    return [c, 0, -s, 0, 1, 0, s, 0, c, ...at];
};

// ---- a model ---------------------------------------------------------------------------------------------------

function load(colour, id) {
    const file = path.join(SOURCE, colour, id + ".json");
    return JSON.parse(fs.readFileSync(file, "utf8"));
}

const nodes = (m, name) => {
    const found = m.rig.nodes.map((n, i) => [n, i]).filter(([n]) => (Array.isArray(name) ? name : [name]).includes(n.name));
    if (!found.length)
        throw new Error(`no node ${name} in ${m.id}`);
    return found;
};
const node = (m, name) => nodes(m, name)[0][0];
const children = (m, index) => m.rig.nodes.map((n, i) => [n, i]).filter(([n, i]) => n.parent === index && i !== index);

function world(m, index) {
    const n = m.rig.nodes[index];
    return n.parent < 0 || n.parent === index ? n.transform : mul(world(m, n.parent), n.transform);
}

/**
 * Every motion of the model: its animations' tracks, and its idle and working motions (a machine's bob, a fan's turn, a
 * building's work), whose samples set a node's transform outright every frame in the game, its rest left aside.
 */
const motions = m => [...Object.values(m.rig.animations ?? {}).flatMap(clip => clip.tracks), ...(m.rig.idle ?? []), ...(m.rig.working ?? [])];

/**
 * Every position of the model, its rest translations and its motions' translations, times `s`. The motions of the nodes
 * named in `aloft` grow about the node's rest instead: a glider's idle motion holds its hip at the height every glider of
 * the original flies at, 20 m over the ground, and a bigger glider flies no higher; only its bob grows. Unscaled, the idle
 * motion drew a machine at its original's height: the Phantom's (Spy 5's) 2 m down in the ground.
 */
function scale(m, s, aloft = []) {
    const about = new Map(aloft.flatMap(name => nodes(m, name)).map(([n, index]) => [index, n.transform.slice(9)]));
    for (const n of m.rig.nodes) {
        n.transform = n.transform.map((v, k) => (k >= 9 ? v * s : v));
        scaleShapes(n, [s, s, s], [0, 0, 0]);
    }
    for (const track of motions(m)) {
        const rest = about.get(track.node) ?? [0, 0, 0];
        track.samples = track.samples.map(t => t.map((v, k) => (k >= 9 ? rest[k - 9] + (v - rest[k - 9]) * s : v)));
    }
    if (m.rig.eye)
        m.rig.eye = m.rig.eye.map(v => v * s);
    if (m.rig.tracks)
        m.rig.tracks.repeatsPerMeter /= s;
    if (m.rig.wheels)
        m.rig.wheels.radius *= s;
    m.scale = (m.scale ?? 1) * s;
}

/** A node's own meshes, glows and spinners stretched by `f` (x, y, z of the node) about `about` (default its middle). */
function stretch(m, name, f, about) {
    for (const [n] of nodes(m, name))
        scaleShapes(n, f, about ?? middle(n));
}

function scaleShapes(n, f, about) {
    const at = p => p.map((v, i) => about[i] + (v - about[i]) * f[i]);
    for (const part of n.parts) {
        for (let k = 0; k < part.positions.length; k += 3) {
            const p = at(part.positions.slice(k, k + 3));
            part.positions.splice(k, 3, ...p);
            const q = normalise([0, 1, 2].map(i => part.normals[k + i] / f[i]));
            part.normals.splice(k, 3, ...q);
        }
    }
    for (const sprite of n.sprites ?? []) {
        sprite.centre = at(sprite.centre);
        sprite.width *= Math.max(...f);
        sprite.height *= Math.max(...f);
    }
    for (const spinner of n.spinners ?? []) {
        spinner.base = at(spinner.base);
        // its corners are flat, in its own plane across the axis
        spinner.corners = spinner.corners.map(v => v * Math.max(...f));
    }
}

function middle(n) {
    const lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    for (const part of n.parts)
        for (let k = 0; k < part.positions.length; k += 3)
            for (let i = 0; i < 3; i++) {
                lo[i] = Math.min(lo[i], part.positions[k + i]);
                hi[i] = Math.max(hi[i], part.positions[k + i]);
            }
    return lo[0] === Infinity ? [0, 0, 0] : lo.map((v, i) => (v + hi[i]) / 2);
}

/** A node moved by `by` in its parent's space, at rest and in every motion. */
function move(m, name, by) {
    for (const [, index] of nodes(m, name))
        moveAt(m, index, by);
}

function moveAt(m, index, by) {
    const n = m.rig.nodes[index];
    n.transform = n.transform.map((v, k) => (k >= 9 ? v + by[k - 9] : v));
    for (const track of motions(m).filter(t => t.node === index))
        track.samples = track.samples.map(t => t.map((v, k) => (k >= 9 ? v + by[k - 9] : v)));
}

/** A node placed where its rest origin times `f` would be: spreads mounts and tracks outwards with a stretched hull. */
function spread(m, name, f) {
    for (const [n, index] of nodes(m, name))
        moveAt(m, index, n.transform.slice(9).map((v, i) => v * (f[i] - 1)));
}

/** A copy of a node of `from` and everything under it, hung on `parent` of `m` at `transform`; returns the new index. */
function graft(m, from, name, parent, transform = IDENTITY) {
    const [[, rootIndex]] = nodes(from, name);
    const map = new Map();
    const copy = (index, under) => {
        const n = JSON.parse(JSON.stringify(from.rig.nodes[index]));
        n.parent = under;
        if (index === rootIndex)
            n.transform = transform;
        m.rig.nodes.push(n);
        map.set(index, m.rig.nodes.length - 1);
        for (const [, child] of children(from, index))
            copy(child, m.rig.nodes.length - 1);
    };
    copy(rootIndex, nodes(m, parent).at(-1)[1]);
    return map;
}

/** The materials of a part of the model wearing `texture`, for a new part to look the same. */
function material(m, texture) {
    if (texture.includes("/")) {
        const any = m.rig.nodes.flatMap(n => n.parts).find(p => p.texture) ?? {};
        return { texture, diffuse: [1, 1, 1, 1], ambient: any.ambient ?? [1, 1, 1], emissive: [0, 0, 0] };
    }
    for (const n of m.rig.nodes)
        for (const part of n.parts)
            if (part.texture.endsWith("/" + texture))
                return { texture: part.texture, diffuse: part.diffuse, ambient: part.ambient, emissive: part.emissive };
    const any = m.rig.nodes.flatMap(n => n.parts)[0];
    return { texture: "models/texture4/" + texture, diffuse: [1, 1, 1, 1], ambient: any.ambient, emissive: [0, 0, 0] };
}

/**
 * A solid of eight corners (bottom four then top four, each going round the same way), every face taking the patch
 * `uv` = [u0, v0, u1, v1] of `texture`, added to node `name`.
 */
function solid(m, name, corners, texture, uv) {
    if (m.space === name)
        corners = corners.map(c => toNode(m, name, c));
    const faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
    const centre = [0, 1, 2].map(i => corners.reduce((s, c) => s + c[i], 0) / 8);
    const part = { ...material(m, texture), positions: [], normals: [], uvs: [], indices: [] };
    const sub = (a, b) => a.map((v, i) => v - b[i]);
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const [u0, v0, u1, v1] = uv;
    for (const f of faces) {
        let q = f.map(i => corners[i]);
        let n = normalise(cross(sub(q[1], q[0]), sub(q[2], q[0])));
        const faceMiddle = [0, 1, 2].map(i => q.reduce((s, c) => s + c[i], 0) / 4);
        if (n.reduce((s, v, i) => s + v * (faceMiddle[i] - centre[i]), 0) < 0) {
            q = [...q].reverse();
            n = n.map(v => -v);
        }
        const uvq = [[u0, v1], [u1, v1], [u1, v0], [u0, v0]];
        for (const t of [[0, 1, 2], [0, 2, 3]])
            for (const k of t) {
                part.indices.push(part.positions.length / 3);
                part.positions.push(...q[k]);
                part.normals.push(...n);
                part.uvs.push(...uvq[k]);
            }
    }
    node(m, name).parts.push(part);
}

/**
 * Faces with texture coordinates of their own, as one part wearing `texture` on node `name`: each face is
 * [corners, uvs], four corners going round (two may meet, for a point) and an [u, v] for each. Each faces away from
 * `centre` (default the middle of all corners), so a convex solid may list its corners either way round.
 */
function faces(m, name, list, texture, centre) {
    if (m.space === name)
        list = list.map(([q, uv]) => [q.map(c => toNode(m, name, c)), uv]);
    const all = list.flatMap(([q]) => q);
    centre ??= [0, 1, 2].map(i => all.reduce((s, c) => s + c[i], 0) / all.length);
    const part = { ...material(m, texture), positions: [], normals: [], uvs: [], indices: [] };
    for (let [q, uv] of list) {
        // Newell's normal, which a quad two of whose corners meet still has
        let n = [0, 0, 0];
        q.forEach((a, i) => {
            const b = q[(i + 1) % q.length];
            n = [n[0] + (a[1] - b[1]) * (a[2] + b[2]), n[1] + (a[2] - b[2]) * (a[0] + b[0]), n[2] + (a[0] - b[0]) * (a[1] + b[1])];
        });
        n = normalise(n);
        const mid = [0, 1, 2].map(i => q.reduce((s, c) => s + c[i], 0) / q.length);
        if (n.reduce((s, v, i) => s + v * (mid[i] - centre[i]), 0) < 0) {
            q = [...q].reverse();
            uv = [...uv].reverse();
            n = n.map(v => -v);
        }
        for (const t of [[0, 1, 2], [0, 2, 3]]) {
            const [a, b, c] = t.map(k => q[k]), ab = b.map((v, i) => v - a[i]), ac = c.map((v, i) => v - a[i]);
            // a corner where two meet leaves one of the two triangles without area
            if (Math.hypot(ab[1] * ac[2] - ab[2] * ac[1], ab[2] * ac[0] - ab[0] * ac[2], ab[0] * ac[1] - ab[1] * ac[0]) < 1e-9)
                continue;
            for (const k of t) {
                part.indices.push(part.positions.length / 3);
                part.positions.push(...q[k]);
                part.normals.push(...n);
                part.uvs.push(...uv[k]);
            }
        }
    }
    node(m, name).parts.push(part);
}

/** A model-space point (at rest) in the space of node `name`; its transforms are rigid. */
function toNode(m, name, p) {
    const w = world(m, nodes(m, name)[0][1]);
    const d = p.map((v, i) => v - w[9 + i]);
    return [0, 1, 2].map(c => w[c * 3] * d[0] + w[c * 3 + 1] * d[1] + w[c * 3 + 2] * d[2]);
}

/** What `build` adds, given in model space at rest, hung on node `name` (so it moves with it). */
function inModel(m, name, build) {
    m.space = name;
    try { build(); } finally { delete m.space; }
}

/** A beam of square section `w` from p0 to p1. */
function beam(m, name, p0, p1, w, texture, uv) {
    const d = normalise(p1.map((v, i) => v - p0[i]));
    const up = Math.abs(d[2]) > 0.9 ? [1, 0, 0] : [0, 0, 1];
    const a = normalise([d[1] * up[2] - d[2] * up[1], d[2] * up[0] - d[0] * up[2], d[0] * up[1] - d[1] * up[0]]);
    const b = [d[1] * a[2] - d[2] * a[1], d[2] * a[0] - d[0] * a[2], d[0] * a[1] - d[1] * a[0]];
    const c = (p, sa, sb) => p.map((v, i) => v + (a[i] * sa + b[i] * sb) * w / 2);
    solid(m, name, [c(p0, -1, -1), c(p0, 1, -1), c(p0, 1, 1), c(p0, -1, 1), c(p1, -1, -1), c(p1, 1, -1), c(p1, 1, 1), c(p1, -1, 1)], texture, uv);
}

/** A box from `lo` to `hi` (node space). */
const box = (m, name, lo, hi, texture, uv) =>
    solid(m, name, [[lo[0], lo[1], lo[2]], [hi[0], lo[1], lo[2]], [hi[0], hi[1], lo[2]], [lo[0], hi[1], lo[2]],
        [lo[0], lo[1], hi[2]], [hi[0], lo[1], hi[2]], [hi[0], hi[1], hi[2]], [lo[0], hi[1], hi[2]]], texture, uv);

/** Node-space point for (a, b) across an axis and h along it ("x", "y" or "z"), about `at`. */
const along = (axis, at, a, b, h) => {
    const p = axis === "x" ? [h, a, b] : axis === "y" ? [a, h, b] : [a, b, h];
    return p.map((v, i) => v + at[i]);
};

/**
 * A ring round an axis (default z): `segments` blocks between radius r0 and r1, from h0 to h1 along it, about `at`.
 * With r0 = 0 it is a solid cylinder; the texture's patch runs round it once.
 */
function ring(m, name, at, r0, r1, h0, h1, segments, texture, uv, axis = "z", turn = 0) {
    for (let k = 0; k < segments; k++) {
        const a = (k / segments) * 2 * Math.PI + turn, b = ((k + 1) / segments) * 2 * Math.PI + turn;
        const p = (r, t, h) => along(axis, at, r * Math.cos(t), r * Math.sin(t), h);
        const u0 = uv[0] + (k / segments) * (uv[2] - uv[0]), u1 = uv[0] + ((k + 1) / segments) * (uv[2] - uv[0]);
        solid(m, name, [p(r0, a, h0), p(r1, a, h0), p(r1, b, h0), p(r0, b, h0), p(r0, a, h1), p(r1, a, h1), p(r1, b, h1), p(r0, b, h1)], texture, [u0, uv[1], u1, uv[3]]);
    }
}

/** A ball (an ellipsoid with radii `r`) of rings and segments, a shell `thickness` (a share of the radius) thick. */
function ball(m, name, at, r, rings, segments, texture, uv, thickness = 0.12) {
    const p = (k, j, f) => {
        const t = (j / rings) * Math.PI, a = (k / segments) * 2 * Math.PI;
        return [at[0] + r[0] * f * Math.sin(t) * Math.cos(a), at[1] + r[1] * f * Math.sin(t) * Math.sin(a), at[2] + r[2] * f * Math.cos(t)];
    };
    for (let j = 0; j < rings; j++)
        for (let k = 0; k < segments; k++) {
            const u0 = uv[0] + (k / segments) * (uv[2] - uv[0]), u1 = uv[0] + ((k + 1) / segments) * (uv[2] - uv[0]);
            const v0 = uv[1] + (j / rings) * (uv[3] - uv[1]), v1 = uv[1] + ((j + 1) / rings) * (uv[3] - uv[1]);
            const i = 1 - thickness;
            solid(m, name, [p(k, j, i), p(k + 1, j, i), p(k + 1, j + 1, i), p(k, j + 1, i), p(k, j, 1), p(k + 1, j, 1), p(k + 1, j + 1, 1), p(k, j + 1, 1)], texture, [u0, v0, u1, v1]);
        }
}

/** A spire: a square foot `w` by `d` at `at` narrowing to a point `h` above it, leaning by `lean` (x, y) at the top. */
function spire(m, name, at, w, d, h, lean, texture, uv) {
    const [x, y, z] = at, tip = [x + lean[0], y + lean[1], z + h], e = 0.02;
    solid(m, name, [[x - w / 2, y - d / 2, z], [x + w / 2, y - d / 2, z], [x + w / 2, y + d / 2, z], [x - w / 2, y + d / 2, z],
        [tip[0] - e, tip[1] - e, tip[2]], [tip[0] + e, tip[1] - e, tip[2]], [tip[0] + e, tip[1] + e, tip[2]], [tip[0] - e, tip[1] + e, tip[2]]], texture, uv);
}

/** The lowest point of the machine at rest (its shadow left out), in model space. */
function lowest(m) {
    let low = Infinity;
    m.rig.nodes.forEach((n, index) => {
        const w = world(m, index);
        for (const part of n.parts.filter(p => p.texture))
            for (let k = 0; k < part.positions.length; k += 3)
                low = Math.min(low, point(w, part.positions.slice(k, k + 3))[2]);
    });
    return low;
}

/** The machine raised or lowered (node `name`, at rest and in every motion) so its lowest point is at `z`. */
function ground(m, name, z) {
    move(m, name, [0, 0, z - lowest(m)]);
}

/**
 * A plate bent along a side outline: `profile` is a list of [x, z] points in node `name`, the plate lies
 * `gap` outside it, `thickness` thick, from y0 to y1; the texture's patch `uv` runs along it from start to end.
 * "Outside" is the left of the way the points go (counter-clockwise outline seen from +y: outside is up for a top).
 */
function shell(m, name, profile, y0, y1, thickness, gap, texture, uv, bend = () => 0) {
    const normals = profile.map((p, i) => {
        const a = profile[Math.max(0, i - 1)], b = profile[Math.min(profile.length - 1, i + 1)];
        const d = normalise([b[0] - a[0], b[1] - a[1]]);
        return [-d[1], d[0]];
    });
    const inner = profile.map((p, i) => [p[0] + normals[i][0] * gap, p[1] + normals[i][1] * gap]);
    const outer = profile.map((p, i) => [p[0] + normals[i][0] * (gap + thickness), p[1] + normals[i][1] * (gap + thickness)]);
    const lengths = profile.slice(1).map((p, i) => Math.hypot(p[0] - profile[i][0], p[1] - profile[i][1]));
    const total = lengths.reduce((a, b) => a + b, 0);
    let run = 0;
    for (let i = 0; i < profile.length - 1; i++) {
        const u0 = uv[0] + (run / total) * (uv[2] - uv[0]);
        run += lengths[i];
        const u1 = uv[0] + (run / total) * (uv[2] - uv[0]);
        // bend(y) moves the plate along x by y, so two halves make a V
        const c = (q, y) => [q[0] + bend(y), y, q[1]];
        solid(m, name, [c(inner[i], y0), c(inner[i + 1], y0), c(inner[i + 1], y1), c(inner[i], y1),
            c(outer[i], y0), c(outer[i + 1], y0), c(outer[i + 1], y1), c(outer[i], y1)], texture, [u0, uv[1], u1, uv[3]]);
    }
}

/** The bounds of node `name`'s own textured parts, [lo, hi], in model space at rest (local: in the node's space). */
function boxOf(m, name, local = false) {
    const [[n, index]] = nodes(m, name);
    const w = local ? IDENTITY : world(m, index), lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
    for (const part of n.parts.filter(p => p.texture))
        for (let k = 0; k < part.positions.length; k += 3)
            point(w, part.positions.slice(k, k + 3)).forEach((v, i) => { lo[i] = Math.min(lo[i], v); hi[i] = Math.max(hi[i], v); });
    return [lo, hi];
}

/** Every corner of node `name`'s own textured parts in model space at rest. */
function points(m, name) {
    // a node's index picks one of several of the same name
    const [n, index] = typeof name === "number" ? [m.rig.nodes[name], name] : nodes(m, name)[0];
    const w = world(m, index), all = [];
    for (const part of n.parts.filter(p => p.texture))
        for (let k = 0; k < part.positions.length; k += 3)
            all.push(point(w, part.positions.slice(k, k + 3)));
    return all;
}

/** The bounds [lo, hi] of node `index` and everything under it, in model space at rest. */
function boxUnder(m, index) {
    const all = [], walk = i => { all.push(...points(m, i)); children(m, i).forEach(([, c]) => walk(c)); };
    walk(index);
    return [[0, 1, 2].map(i => Math.min(...all.map(p => p[i]))), [0, 1, 2].map(i => Math.max(...all.map(p => p[i])))];
}

/** Node `name` turned about its own y axis by `deg` (nose down for positive), at rest and in every motion. */
function tilt(m, name, deg) {
    for (const [n, index] of nodes(m, name)) {
        n.transform = mul(n.transform, tiltY(deg));
        for (const track of motions(m).filter(t => t.node === index))
            track.samples = track.samples.map(t => mul(t, tiltY(deg)));
    }
}

/** The height of the machine's surface at (x, y) in model space at rest: the highest triangle of `names` straight above. */
function surfaceZ(m, x, y, names) {
    let top = -Infinity;
    m.rig.nodes.forEach((n, index) => {
        if (names && !names.includes(n.name))
            return;
        const w = world(m, index);
        for (const part of n.parts.filter(p => p.texture)) {
            const v = k => point(w, part.positions.slice(k * 3, k * 3 + 3));
            for (let t = 0; t + 2 < part.indices.length; t += 3) {
                const [a, b, c] = [v(part.indices[t]), v(part.indices[t + 1]), v(part.indices[t + 2])];
                const d = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1]);
                if (Math.abs(d) < 1e-9)
                    continue;
                const s = ((x - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (y - a[1])) / d;
                const r = ((b[0] - a[0]) * (y - a[1]) - (x - a[0]) * (b[1] - a[1])) / d;
                if (s >= 0 && r >= 0 && s + r <= 1)
                    top = Math.max(top, a[2] + s * (b[2] - a[2]) + r * (c[2] - a[2]));
            }
        }
    });
    return top;
}

/**
 * A copy of a leg (node `thigh` and its child `shin`) turned by `deg` about the vertical through the thigh's parent,
 * walking as the leg it copies: its tracks in every clip are copied, the thigh's turned the same way.
 */
function cloneLeg(m, thigh, shin, deg) {
    const [[t, ti]] = nodes(m, thigh), [[s, si]] = nodes(m, shin);
    const turn = turnZ(deg);
    const nt = { ...JSON.parse(JSON.stringify(t)), name: thigh + "b", transform: mul(turn, t.transform) };
    m.rig.nodes.push(nt);
    const nti = m.rig.nodes.length - 1;
    m.rig.nodes.push({ ...JSON.parse(JSON.stringify(s)), name: shin + "b", parent: nti });
    const nsi = m.rig.nodes.length - 1;
    for (const clip of Object.values(m.rig.animations ?? {})) {
        for (const track of clip.tracks.filter(tr => tr.node === ti))
            clip.tracks.push({ ...track, node: nti, samples: track.samples.map(x => mul(turn, x)) });
        for (const track of clip.tracks.filter(tr => tr.node === si))
            clip.tracks.push({ ...JSON.parse(JSON.stringify(track)), node: nsi });
    }
}

// ---- painted textures ------------------------------------------------------------------------------------------

const TEXTURES = require("./textures");

/** A new mount `name` on node `parent` at a model-space point, its axes the model's (as a machine's mounts stand). */
function mount(m, parent, name, at) {
    const p = nodes(m, parent)[0][1], w = world(m, p);
    const r = [w[0], w[3], w[6], w[1], w[4], w[7], w[2], w[5], w[8]];
    m.rig.nodes.push({ name, parent: p, transform: [...r, ...toNode(m, parent, at)], parts: [], sprites: [], spinners: [] });
    return name;
}

/** The weapon another machine carries on `mounting`, hung on node `on` `size` times as big; it fires as `as`. */
function arm(m, from, mounting, on, size, as) {
    const weapon = from.rig.weapons.find(w => w.mounting === mounting);
    const map = graft(m, from, from.rig.nodes[weapon.node].name, on);
    for (const index of map.values())
        scaleShapes(m.rig.nodes[index], [size, size, size], [0, 0, 0]);
    m.rig.weapons.push({ ...weapon, node: map.get(weapon.node), mounting: as ?? mounting });
}

/** Paints the textures a recipe uses, once per run and colour; returns their paths as parts name them. One painted
 * from a race-coloured original is painted again in every colour's paint, the Red one keeping the plain name and the
 * others named by their colour (hornet_wing_blue_b.bmp): the game swaps only the original's own race textures. */
const painted = new Map();
let painting = "red";
function texture(name) {
    const key = painting + "/" + name;
    if (!painted.has(key)) {
        paint.colour(painting);
        const img = TEXTURES[name]();
        const own = painting !== "red" && paint.wasRaced() ? name.replace(/(_b\.bmp)$/,`_${painting}$1`) : name;
        painted.set(key, paint.write(img, own));
    }
    return painted.get(key);
}

/** The model's top-level parts, glows and spinners again from its rig at rest, as the export writes them. */
function flatten(m) {
    m.parts = [];
    m.sprites = [];
    m.spinners = [];
    m.rig.nodes.forEach((n, index) => {
        const w = world(m, index);
        for (const part of n.parts) {
            const flat = { ...part, positions: [], normals: [] };
            for (let k = 0; k < part.positions.length; k += 3) {
                flat.positions.push(...point(w, part.positions.slice(k, k + 3)));
                flat.normals.push(...normalise(vector(w, part.normals.slice(k, k + 3))));
            }
            m.parts.push(flat);
        }
        for (const sprite of n.sprites ?? [])
            m.sprites.push({ ...sprite, centre: point(w, sprite.centre) });
        for (const spinner of n.spinners ?? [])
            m.spinners.push({ ...spinner, base: point(w, spinner.base), axis: normalise(vector(w, spinner.axis)) });
    });
}

const round = (key, value) => (typeof value === "number" ? Math.round(value * 1e6) / 1e6 : value);

function write(m, colour, words) {
    flatten(m);
    delete m.scale;
    const folder = path.join(TARGET, "models", colour);
    fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(path.join(folder, m.id + ".json"), JSON.stringify(m, round));
}

// ---- the machines ----------------------------------------------------------------------------------------------

const RECIPES = require("./recipes")({ load, scale, stretch, spread, scaleShapes, middle, nodes, world, ground, inModel,
    solid, faces, box, shell, ring, ball, spire, beam, mount, arm, texture, toNode, boxOf, surfaceZ, cloneLeg, move, points, boxUnder, tilt });

// ---- running ---------------------------------------------------------------------------------------------------

function check() {
    // An original rebuilt from its rig must come out as exported.
    const id = "AGGRESSOR_3_ASSASSIN_LR_AUTO_CANNON_X2";
    const m = load("red", id), original = load("red", id);
    flatten(m);
    const flat = o => [...o.parts.flatMap(p => p.positions), ...o.sprites.flatMap(s => s.centre), ...o.spinners.flatMap(s => [...s.base, ...s.corners])];
    const a = flat(m).sort((x, y) => x - y), b = flat(original).sort((x, y) => x - y);
    const worst = a.length === b.length ? Math.max(...a.map((v, i) => Math.abs(v - b[i]))) : Infinity;
    console.log(`check ${id}: ${a.length} / ${b.length} numbers, worst difference ${worst}`);
    if (!(worst < 1e-3))
        process.exitCode = 1;
}

function info(id) {
    const m = load("red", id);
    m.rig.nodes.forEach((n, index) => {
        const w = world(m, index), lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
        for (const part of n.parts.filter(p => p.texture))
            for (let k = 0; k < part.positions.length; k += 3)
                point(w, part.positions.slice(k, k + 3)).forEach((v, i) => { lo[i] = Math.min(lo[i], v); hi[i] = Math.max(hi[i], v); });
        const f = v => v.map(x => x.toFixed(2)).join(",");
        console.log(index, n.name, "p" + n.parent, "at", f(w.slice(9)), lo[0] < Infinity ? `box ${f(lo)} .. ${f(hi)}` : "");
    });
}

const asked = process.argv.slice(2);
if (asked[0] === "info")
    info(asked[1]);
else if (asked[0] === "eval")
    // a look at a model while writing a recipe, e.g. eval 'boxOf(load("red", "SPY_LOCATOR_5"), "head", true)'
    console.log(JSON.stringify(eval(asked[1]), (k, v) => (typeof v === "number" ? Math.round(v * 100) / 100 : v)));
else if (asked.includes("check"))
    check();
else {
    const names = asked.length ? asked : Object.keys(RECIPES);
    const list = path.join(TARGET, "models.txt");
    const lines = new Map((fs.existsSync(list) ? fs.readFileSync(list, "utf8").split(/\r?\n/).filter(Boolean) : []).map(l => [l.split(" ").slice(1).join("_"), l]));
    for (const name of names) {
        const recipe = RECIPES[name];
        if (!recipe)
            throw new Error(`no recipe ${name}; there are ${Object.keys(RECIPES).join(", ")}`);
        const id = recipe.line.split(" ").slice(1).join("_");
        for (const colour of COLOURS) {
            painting = colour;
            const m = recipe.make(colour);
            m.id = id;
            write(m, colour, recipe.line);
        }
        lines.set(id, recipe.line);
        console.log(`${name}: ${id}`);
    }
    fs.writeFileSync(list, [...lines.values()].join("\n") + "\n");
}

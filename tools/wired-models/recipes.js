// Wired's own machines, one recipe each: the line of classic/wired/models.txt it adds (MACHINE <type> <level>
// [<subtype>] [<weapons>]; the id is its words joined by "_"), and how it is made from the original's models of one
// colour with the helpers of build.js. Coordinates are model space at rest (x forward, y left, z up, metres) unless a
// helper is given a node's own space. None of these machines is in the original; the references are the concept
// pictures in renders/concepts/endgame.
"use strict";

module.exports = h => {
    const { load, scale, stretch, spread, scaleShapes, middle, nodes, world, ground, inModel, solid, faces, box, shell, ring,
        ball, spire, beam, mount, arm, texture, toNode, boxOf, surfaceZ, cloneLeg, move, points, boxUnder, tilt } = h;
    const ALL = [0, 0, 1, 1];
    const LEGS = ["l1t", "l2t", "r1t", "r2t", "l1s", "l2s", "r1s", "r2s"];
    const SHINS = ["l1s", "l2s", "r1s", "r2s"], THIGHS = ["l1t", "l2t", "r1t", "r2t"];
    const REAPER = "AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2";

    const sub = (a, b) => a.map((v, i) => v - b[i]);
    const add = (a, b, f = 1) => a.map((v, i) => v + b[i] * f);
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const unit = v => { const l = Math.hypot(...v) || 1; return v.map(x => x / l); };

    /**
     * An empty top mount ("gun") on `parent`'s roof, `dx` from the middle of its length: where a Wired hero's top weapon
     * stands. The weapon brings its own foot.
     */
    const roofMount = (m, parent, dx = 0) => {
        const [lo, hi] = boxOf(m, parent);
        const x = (lo[0] + hi[0]) / 2 + dx;
        mount(m, parent, "gun", [x, 0, surfaceZ(m, x, 0, [parent])]);
    };

    /**
     * Mount `name`'s origin moved to the model-space point `at`, what hangs from it staying where it is: a hero's weapon
     * goes where the mount is, a machine's own weapon is not moved by it.
     */
    const shiftMount = (m, name, at) => {
        const [[n, index]] = nodes(m, name);
        const below = m.rig.nodes.map((c, i) => [c, i]).filter(([c, i]) => c.parent === index && i !== index).map(([c, i]) => [c, world(m, i).slice(9)]);
        n.transform = [...n.transform.slice(0, 9), ...toNode(m, m.rig.nodes[n.parent].name, at)];
        for (const [c, was] of below)
            c.transform = [...c.transform.slice(0, 9), ...toNode(m, name, was)];
    };

    /** A flat plate through the four points `q` (going round), `th` thick, on node `name` (model space with inModel). */
    const plate = (m, name, q, th, tex) => {
        const n = unit(cross(sub(q[1], q[0]), sub(q[3], q[0])));
        solid(m, name, [...q.map(p => add(p, n, -th / 2)), ...q.map(p => add(p, n, th / 2))], tex, ALL);
    };

    /**
     * A plate bent along `profile` ([y, z] points, model space) from x0 to x1, `th` thick, standing off `centre`
     * ([y, z]) outwards: a shoulder pad or a cowl. Hung on node `name`.
     */
    const cowl = (m, name, profile, centre, x0, x1, th, tex) => inModel(m, name, () => {
        const out = profile.map(p => { const d = unit([p[0] - centre[0], p[1] - centre[1]]); return [p[0] + d[0] * th, p[1] + d[1] * th]; });
        for (let i = 0; i + 1 < profile.length; i++) {
            const c = (q, x) => [x, q[0], q[1]];
            solid(m, name, [c(profile[i], x0), c(profile[i + 1], x0), c(profile[i + 1], x1), c(profile[i], x1),
                c(out[i], x0), c(out[i + 1], x0), c(out[i + 1], x1), c(out[i], x1)], tex,
                [i / (profile.length - 1), 0, (i + 1) / (profile.length - 1), 1]);
        }
    });

    /**
     * Node `name`'s own shapes widened towards their foot about the node's middle, by `k` ([x, y]) at the bottom and
     * not at all at the top: mass low down. The normals stay (the faces lean only a little).
     */
    const taper = (m, name, k) => {
        for (const [n] of nodes(m, name)) {
            const c = middle(n), zs = n.parts.flatMap(p => p.positions.filter((_, i) => i % 3 === 2));
            const lo = Math.min(...zs), hi = Math.max(...zs);
            for (const p of n.parts)
                for (let i = 0; i < p.positions.length; i += 3) {
                    const f = (hi - p.positions[i + 2]) / (hi - lo);
                    p.positions[i] = c[0] + (p.positions[i] - c[0]) * (1 + k[0] * f);
                    p.positions[i + 1] = c[1] + (p.positions[i + 1] - c[1]) * (1 + k[1] * f);
                }
        }
    };

    /**
     * Mount `gun` moved out sideways (s = 1 to the left, -1 to the right) until all that hangs on it clears the
     * nodes `hull` by `gap` beside and below it.
     */
    const clearOut = (m, gun, s, hull, gap = 0.05) => {
        const [lo, hi] = boxUnder(m, nodes(m, gun)[0][1]);
        const inner = s > 0 ? lo[1] : -hi[1];
        const near = hull.flatMap(n => points(m, n)).filter(p => p[0] > lo[0] && p[0] < hi[0] && p[2] > lo[2] - 0.6 && p[2] < hi[2]);
        if (near.length === 0)
            return;
        const clear = Math.max(...near.map(p => s * p[1])) + gap - inner;
        if (clear > 0)
            move(m, gun, [0, s * clear, 0]);
    };

    /** A shield on the outside of a shin's knee, rising to a point above it (in model space, hung on the shin). */
    const kneeShield = (m, shin, tex, rise = 0.6) => {
        const all = points(m, shin), top = Math.max(...all.map(p => p[2]));
        const k = world(m, nodes(m, shin)[0][1]).slice(9);
        const o = unit([k[0] - 0.05, k[1], 0]), t = [-o[1], o[0], 0];
        const near = all.filter(p => p[2] > top - 0.9);
        const along = (p, d) => (p[0] - k[0]) * d[0] + (p[1] - k[1]) * d[1];
        const out = Math.max(...near.map(p => along(p, o))), half = Math.max(...near.map(p => Math.abs(along(p, t))));
        const at = (r, s, z) => [k[0] + o[0] * r + t[0] * s, k[1] + o[1] * r + t[1] * s, z];
        // set high on the shin, its top leaning back towards the body
        const z0 = top - 0.3, z1 = top + rise, r0 = out + 0.02, r1 = out - 0.14, th = 0.14, tip = 0.25;
        inModel(m, shin, () => solid(m, shin, [at(r0, -half, z0), at(r0, half, z0), at(r1, tip, z1), at(r1, -tip, z1),
            at(r0 + th, -half, z0), at(r0 + th, half, z0), at(r1 + th, tip, z1), at(r1 + th, -tip, z1)], tex, ALL));
    };

    /**
     * The Thresher's shape before its size: Reaper 3's four legs a fifth longer and `thick` times as thick, a wider
     * body, knee shields (unless `armour` makes the legs anew, which then grow their own).
     */
    const thresher = (colour, armour, thick = 1.6, shields = !armour) => {
        const m = load(colour, REAPER);
        // each leg's piece starts at its joint (x = 0) and runs along -x
        for (const [n] of nodes(m, LEGS)) {
            const c = middle(n);
            scaleShapes(n, [1.2, thick, thick], [0, c[1], c[2]]);
        }
        spread(m, SHINS, [1.2, 1, 1]);
        ground(m, "hip", 0.04);
        stretch(m, "body", [1.1, 1.2, 1.2], [0, 0, 0]);
        spread(m, ["l_gun", "r_gun"], [1, 1.2, 1]);
        spread(m, "fplat", [1.1, 1, 1]);
        stretch(m, "hip", [1.2, 1.2, 1]);
        if (armour)
            armour(m);
        const knee = texture("knee_b.bmp");
        for (const shin of shields ? SHINS : [])
            kneeShield(m, shin, knee);
        return m;
    };

    /**
     * Node `name`'s own piece taken off it to be made again: its triangles, each three [corner, uv] in node space
     * (a corner shared by several triangles is one array, so corners compare with ===), its corners, and the part's
     * material. A loose triangle that shares no corner with the rest is left out: each of the Reaper's leg pieces has
     * a thin fin, which the thick leg swallows.
     */
    const takePiece = (m, name) => {
        const [[n]] = nodes(m, name);
        const parts = n.parts.filter(p => p.texture);
        n.parts = n.parts.filter(p => !p.texture);
        const same = new Map(), key = c => c.map(v => v.toFixed(3)).join();
        const tris = parts.flatMap(p => [...Array(p.indices.length / 3).keys()].map(t => [0, 1, 2].map(j => {
            const k = p.indices[t * 3 + j], c = p.positions.slice(k * 3, k * 3 + 3);
            if (!same.has(key(c)))
                same.set(key(c), c);
            return [same.get(key(c)), p.uvs.slice(k * 2, k * 2 + 2)];
        })));
        const uses = c => tris.filter(t => t.some(([d]) => d === c)).length;
        const piece = tris.filter(t => t.some(([c]) => uses(c) > 1));
        const { texture: tex, diffuse, ambient, emissive } = parts[0];
        return { tris: piece, corners: [...new Set(piece.flat().map(([c]) => c))], material: { texture: tex, diffuse, ambient, emissive } };
    };

    /** Faces (as `faces` takes them) given to node `name` as one part in `material`, a taken piece's. */
    const givePiece = (m, name, list, material) => {
        faces(m, name, list, material.texture.split("/").pop());
        Object.assign(nodes(m, name)[0][0].parts.at(-1), material);
    };

    /** A triangle as `faces` takes it: its last corner twice. */
    const tri3 = (q, uv) => [[...q, q[2]], [...uv, uv[2]]];

    /**
     * A Reaper's shin made thick from its own piece, which is a point (the foot) under a top of three corners, two
     * wide apart outside (+z) and one inside where its two inner faces meet in a ridge. The ridge is opened into an
     * inner face `narrow` times as wide as the outside, the section `depth` times as deep about its middle, the top
     * raised to `top` (node x: the piece's fin reached that high), and the section kept down to `foot` (along the
     * ridge towards the point), `taper` times as wide there, from where the faces run to the Reaper's own foot point.
     * With a `guard` ({ rise, half, lean }) the outside goes on up past the top into a guard over the knee: its top
     * edge `rise` higher, `half` times as wide and leaning back `lean`, from where the top slopes down to the inside.
     * Each face is one of the Reaper's with its corners' texture coordinates; down to `foot` a face's patch moves
     * along with it (so the texture keeps its width there instead of narrowing as on the Reaper's cone), the guard
     * carries on the patch of the face it grows out of, and the inner face takes the outside's patch at its width.
     */
    const thickShin = (m, s, depth, top, foot, { narrow = 0.5, taper = 1, guard } = {}) => {
        const { tris, corners, material } = takePiece(m, s);
        const tip = corners.reduce((a, b) => (b[0] < a[0] ? b : a));
        const [o1, o2, inner] = corners.filter(c => c !== tip).sort((a, b) => b[2] - a[2]);
        const [left, right] = o1[1] > o2[1] ? [o1, o2] : [o2, o1];
        const yc = (left[1] + right[1]) / 2, w = (left[1] - right[1]) / 2, zc = (left[2] + inner[2]) / 2;
        // a corner of the top in its new place; the inner one opened into an edge, side +1 its left end
        const at = (c, side = 0) => [c[0] + top - inner[0], c === inner ? yc + side * w * narrow : c[1], zc + (c[2] - zc) * depth];
        // the same section where the foot begins, moved along the ridge towards the point and `taper` times as wide
        const f = (top - foot) / (top - tip[0]), down = sub(tip, at(inner)).map(v => v * f);
        const low = (c, side) => { const p = add(at(c, side), down); p[1] = yc + down[1] + (at(c, side)[1] - yc) * taper; return p; };
        // an outside corner of the guard's top edge, and a face's patch carried on up to it from the top
        const rise = guard ? guard.rise : 0;
        const high = c => { const p = at(c); return [p[0] + rise, yc + (p[1] - yc) * guard.half, p[2] - guard.lean]; };
        const climb = (uv, shift) => sub(uv, shift.map(v => v * rise / (top - foot)));
        const list = [];
        let topUv, outside;
        for (const t of tris) {
            const upper = t.filter(([c]) => c !== tip), uv = c => upper.find(([d]) => d === c)[1];
            if (upper.length === 3) {
                const half = sub(uv(left), uv(right)).map(v => v * narrow / 2);
                topUv = [uv(left), uv(right), sub(uv(inner), half), add(uv(inner), half)];
                continue;
            }
            const side = upper.some(([c]) => c === right) ? -1 : 1, tipUv = t.find(([c]) => c === tip)[1];
            const shift = sub(tipUv, add(upper[0][1], upper[1][1]).map(v => v / 2)).map(v => v * f);
            const [[a, ua], [b, ub]] = upper;
            list.push([[at(a, side), at(b, side), low(b, side), low(a, side)], [ua, ub, add(ub, shift), add(ua, shift)]]);
            list.push(tri3([low(a, side), low(b, side), tip], [add(ua, shift), add(ub, shift), tipUv]));
            if (!upper.some(([c]) => c === inner))
                outside = { l: uv(left), r: uv(right), shift, tipUv };
            else if (guard) {
                // the inner face's side of the guard, from its outside corner up to the guard's top edge
                const o = side > 0 ? left : right;
                list.push(tri3([at(o), high(o), at(inner, side)], [uv(o), climb(uv(o), shift), uv(inner)]));
            }
        }
        const [tl, tr] = guard ? [high(left), high(right)] : [at(left), at(right)];
        list.push([[tl, tr, at(inner, -1), at(inner, 1)], topUv]);
        const { l, r, shift, tipUv } = outside, mid = add(l, r).map(v => v / 2);
        // the outside's patch about its middle, `k` times as wide
        const across = (k, by = [0, 0]) => [add(add(mid, sub(l, mid).map(v => v * k)), by), add(add(mid, sub(r, mid).map(v => v * k)), by)];
        if (guard) {
            const [gl, gr] = across(guard.half, climb([0, 0], shift));
            list.push([[tl, tr, at(right), at(left)], [gl, gr, r, l]]);
        }
        const [il, ir] = across(narrow), [fl, fr] = across(narrow, shift);
        list.push([[at(inner, 1), at(inner, -1), low(inner, -1), low(inner, 1)], [il, ir, fr, fl]]);
        list.push(tri3([low(inner, 1), low(inner, -1), tip], [fl, fr, tipUv]));
        givePiece(m, s, list, material);
    };

    /**
     * A Reaper's thigh made thick from its own piece, which is wedge-shaped: at the knee's end two corners wide apart
     * on top and one underneath, from where its top runs to a point and its underside to a ridge towards the hip. It
     * becomes a block from `hip` to `knee` (node x): the top's point and the ridge's two ends opened into edges, the
     * top as wide as at the knee's end (`widen` times), the underside `narrow` times that at the hip and `wide` times
     * at the knee (so it covers the shin's top there), the section `depth` times as deep about its middle. Each face is
     * one of the Reaper's with its corners' texture coordinates; the top's patch runs on along the longer block at the
     * Reaper's size (its arcs repeat instead of stretching), the new underside takes the top's patch as wide as it is,
     * the new end at the hip the knee end's patch.
     */
    const thickThigh = (m, t, knee, hip, depth, widen, narrow = 0.5, wide = narrow) => {
        const { tris, corners, material } = takePiece(m, t);
        const byX = corners.slice().sort((a, b) => a[0] - b[0]);
        const end = byX.slice(0, 3).sort((a, b) => b[2] - a[2]), [inTop, inLow] = byX.slice(3).sort((a, b) => b[2] - a[2]);
        const [left, right] = end[0][1] > end[1][1] ? [end[0], end[1]] : [end[1], end[0]], under = end[2];
        const yc = (left[1] + right[1]) / 2, w = (left[1] - right[1]) / 2 * widen;
        const zc = (left[2] + under[2]) / 2, half = (left[2] - under[2]) / 2 * depth;
        // a corner in its new place; the top's point and the ridge's ends opened into edges, side +1 their left ends
        const place = (c, side = 0) => {
            const top = c === left || c === right || c === inTop;
            const y = c === left ? yc + w : c === right ? yc - w : yc + side * w * (top ? 1 : c === under ? wide : narrow);
            return [c === inTop || c === inLow ? hip : knee, y, zc + (top ? half : -half)];
        };
        // the top's patch at the knee's end (its middle and half its width) and at the hip's end, as far on as the
        // block is longer than the Reaper's top
        const run = (hip - knee) / (inTop[0] - left[0]);
        let kneeEnd, mid, d, far;
        const list = [];
        for (const tri of tris) {
            const has = c => tri.some(([d]) => d === c), uv = c => tri.find(([d]) => d === c)[1];
            if (has(left) && has(right)) {
                // the top or the knee's end: its third corner opened into an edge as wide as the face is there
                const [r] = tri.map(([c]) => c).filter(c => c !== left && c !== right);
                const across = sub(uv(left), uv(right)).map(v => v / 2);
                if (r === inTop) {
                    mid = add(uv(left), uv(right)).map(v => v / 2);
                    d = across;
                    far = add(mid, sub(uv(inTop), mid).map(v => v * run));
                    list.push([[place(left), place(right), place(r, -1), place(r, 1)], [uv(left), uv(right), sub(far, d), add(far, d)]]);
                } else {
                    const e = across.map(v => v * wide);
                    kneeEnd = [uv(left), uv(right), sub(uv(r), e), add(uv(r), e)];
                    list.push([[place(left), place(right), place(r, -1), place(r, 1)], kneeEnd]);
                }
                continue;
            }
            const side = has(left) ? 1 : -1;
            list.push(tri3(tri.map(([c]) => place(c, side)), tri.map(([, u]) => u)));
        }
        const by = k => d.map(v => v * k);
        list.push([[place(under, 1), place(under, -1), place(inLow, -1), place(inLow, 1)],
            [add(mid, by(wide)), sub(mid, by(wide)), sub(far, by(narrow)), add(far, by(narrow))]]);
        list.push([[place(inTop, 1), place(inTop, -1), place(inLow, -1), place(inLow, 1)], kneeEnd]);
        givePiece(m, t, list, material);
    };

    /**
     * Harvester's legs: the Reaper's own leg pieces made thick, each one block in the Reaper's leg texture laid as on
     * its legs. Each thigh from inside the hip to the knee, wide on top and half as wide underneath towards the hip;
     * each shin from the knee to the Reaper's foot point, wide outside and half as wide inside, narrowing a quarter
     * down to where its foot begins, its outside rising past the knee into a guard as the Thresher's knee shield.
     */
    const legArmour = m => {
        // each shin from as high as its fin reached (filling the thigh's end) down to the point
        for (const s of SHINS)
            thickShin(m, s, 1.2, boxOf(m, s, true)[1][0], -1.1, { taper: 0.75, guard: { rise: 0.7, half: 0.55, lean: 0.08 } });
        THIGHS.forEach((t, k) => {
            // the thigh runs along -x from its joint; its end at the knee over the shin's top, a little short of the
            // shin's outside so the guard stands before it, and underneath as wide as the shin there
            const hip = boxOf(m, t, true)[1][0] - 0.3;
            const knee = Math.min(...points(m, SHINS[k]).map(p => toNode(m, t, p)[0])) + 0.12;
            thickThigh(m, t, knee, hip, 1.75, 1.05, 0.5, 0.95);
        });
    };

    /** Sarge's shape before its size: Grunt 3, its body higher and further forward, fenders over the tracks. */
    const sarge = (colour, front = true) => {
        const m = load(colour, "AGGRESSOR_3_GRUNT_LR_HEAVY_BOLTER1_X2");
        const fender = texture("sarge_fender_b.bmp");
        // the track's side outline in its own space from its back up over the top to the slope in front, which it
        // leaves open so the track shows; as wide as the tread
        const top = [[1.0, 0.01], [-0.91, 0.76], [-1.35, 0.6], [-1.82, -0.3]];
        for (const side of ["l_trs", "r_trs"])
            shell(m, side, top.slice().reverse(), -0.74, 0.74, 0.16, 0.06, fender, ALL);
        // the body grows from its back foot: higher and further forward, its guns and face plate with it
        stretch(m, "body", [1.2, 1, 1.3], [-1.1, 0, -0.6]);
        move(m, ["l_gun", "r_gun"], [0.14, 0, 0.33]);
        move(m, "fplat", [0.45, 0, 0.16]);
        // more mass low down: the body widens towards its foot, its guns out beside it
        taper(m, "body", [0.2, 0.35]);
        for (const [gun, s] of [["l_gun", -1], ["r_gun", 1]])
            clearOut(m, gun, s, ["body"]);
        if (front) {
            solid(m, "hip", [[1.25, -1.0, -0.65], [1.45, -1.0, -0.65], [1.45, 1.0, -0.65], [1.25, 1.0, -0.65],
                [0.95, -0.9, 0.15], [1.15, -0.9, 0.15], [1.15, 0.9, 0.15], [0.95, 0.9, 0.15]], texture("sarge_plate_b.bmp"), ALL);
            // the plate hangs from the body's underside on a slanted steel piece
            const [lo, hi] = boxOf(m, "body");
            inModel(m, "hip", () => solid(m, "hip", [[0.45, -0.72, 0.75], [0.8, -0.72, 0.75], [0.8, 0.72, 0.75], [0.45, 0.72, 0.75],
                [hi[0] - 1.0, -0.72, lo[2] + 0.15], [hi[0] - 0.45, -0.72, lo[2] + 0.15], [hi[0] - 0.45, 0.72, lo[2] + 0.15], [hi[0] - 1.0, 0.72, lo[2] + 0.15]],
                texture("steel_b.bmp"), ALL));
        }
        return m;
    };

    return {
        // Sarge (Grunt 4): a little bigger than Grunt 3.
        sarge: {
            line: "MACHINE AGGRESSOR 4 GRUNT LR_HEAVY_BOLTER1_X2",
            make: colour => { const m = sarge(colour); roofMount(m, "body"); scale(m, 1.15); return m; },
        },
        // Old Iron (Grunt 5): Sarge with a bigger tower, a V-shaped dozer blade on two arms in front, bigger again.
        oldiron: {
            line: "MACHINE AGGRESSOR 5 GRUNT LR_HEAVY_BOLTER1_X2",
            make: colour => {
                const m = sarge(colour, false);
                stretch(m, "body", [1.2, 1.2, 1.2], [-1.1, 0, -0.6]);
                spread(m, ["l_gun", "r_gun"], [1, 1.2, 1]);
                move(m, ["l_gun", "r_gun"], [0.17, 0, 0.29]);
                move(m, "fplat", [0.54, 0, 0.14]);
                for (const [gun, s] of [["l_gun", -1], ["r_gun", 1]])
                    clearOut(m, gun, s, ["body"]);
                const blade = texture("oldiron_blade_b.bmp"), steel = texture("steel_b.bmp");
                // the blade's side outline, from its top edge down to its cutting edge: higher than a man
                const outline = [[3.1, 1.95], [2.9, 1.45], [2.8, 0.8], [2.85, 0.3], [3.05, 0.04]];
                // two halves meeting in a point in front, their ends swept back
                const bend = y => -0.4 * Math.abs(y);
                const [lo, hi] = boxOf(m, "body");
                inModel(m, "hip", () => {
                    for (const [y0, y1] of [[-2.7, 0], [0, 2.7]])
                        shell(m, "hip", outline, y0, y1, 0.14, 0, blade, [(y0 + 2.7) / 5.4, 0, (y1 + 2.7) / 5.4, 1], bend);
                    for (const y of [-0.85, 0.85]) {
                        const back = 2.8 + bend(y) - 0.05;
                        beam(m, "hip", [hi[0] - 0.9, y, lo[2] + 0.2], [back, y, 1.15], 0.3, steel, ALL);
                        beam(m, "hip", [0.1, y, 0.45], [back, y, 0.4], 0.26, steel, ALL);
                    }
                });
                roofMount(m, "body");
                scale(m, 1.3);
                return m;
            },
        },
        // Thresher (Reaper 4): a fifth bigger than Reaper 3, a shield over each knee.
        thresher: {
            line: "MACHINE AGGRESSOR 4 ASSASSIN LR_PLASMA_RIFLE_X2",
            make: colour => { const m = thresher(colour); scale(m, 1.2); return m; },
        },
        // Harvester (Reaper 5): the Thresher bigger again on thick legs made from the Reaper's own, a thicker and
        // higher body.
        harvester: {
            line: "MACHINE AGGRESSOR 5 ASSASSIN LR_PLASMA_RIFLE_X2",
            make: colour => {
                const m = thresher(colour, legArmour, 1.15);
                // the body grows from its foot, its guns and face plate with it
                stretch(m, "body", [1.1, 1.1, 1.35], [0, 0, -0.25]);
                spread(m, ["l_gun", "r_gun"], [1, 1.1, 1]);
                move(m, ["l_gun", "r_gun"], [0, 0, 0.31]);
                move(m, "fplat", [0.12, 0, 0.21]);
                ground(m, "hip", 0.04);
                roofMount(m, "body");
                scale(m, 1.32);
                return m;
            },
        },
        // Widow (Reaper line, the thin one): eight long thin legs, a slim front with lamps, a big round abdomen.
        widow: {
            line: "MACHINE AGGRESSOR 6 ASSASSIN LR_PLASMA_RIFLE_X2",
            make: colour => {
                const m = load(colour, REAPER);
                for (const [n] of nodes(m, LEGS)) {
                    const c = middle(n);
                    scaleShapes(n, [1.7, 0.7, 0.7], [0, c[1], c[2]]);
                }
                spread(m, SHINS, [1.7, 1, 1]);
                // four more legs between the four, walking as the leg each is copied from
                cloneLeg(m, "l1t", "l1s", 32);
                cloneLeg(m, "l2t", "l2s", -32);
                cloneLeg(m, "r1t", "r1s", -32);
                cloneLeg(m, "r2t", "r2s", 32);
                stretch(m, "body", [1, 0.6, 1], [0, 0, 0]);
                spread(m, ["l_gun", "r_gun"], [1, 0.75, 1]);
                ground(m, "hip", 0.04);
                const lamp = texture("lamp_b.bmp");
                inModel(m, "body", () => {
                    for (const y of [-0.4, 0.4])
                        box(m, "body", [0.98, y - 0.2, 3.98], [1.2, y + 0.2, 4.36], lamp, ALL);
                });
                const [lo, hi] = boxOf(m, "body"), z = (lo[2] + hi[2]) / 2 + 0.35, skin = texture("widow_abdomen_b.bmp");
                inModel(m, "body", () => {
                    ball(m, "body", [lo[0] - 1.9, 0, z], [2.6, 1.9, 1.8], 6, 12, skin, ALL);
                    ring(m, "body", [lo[0] + 0.1, 0, z - 0.2], 0, 0.95, -0.9, 0.35, 10, skin, ALL, "x");
                });
                return m;
            },
        },
        // Colossus (Ballista 5): the Goliath a quarter bigger on long flat tracks, its launchers out at the sides,
        // a ring on top of the dome for the third, top weapon (a Ballista 3's launcher half again as big).
        colossus: {
            line: "MACHINE AGGRESSOR 5 BALLISTA LRT_MULTI_LAUNCHER2_X3",
            make: colour => {
                const m = load(colour, "AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2");
                // long flat tracks, from under the rounded front to under the rounded back
                for (const side of ["l_trs", "l_trx", "r_trs", "r_trx"]) {
                    const [[n]] = nodes(m, side), c = middle(n);
                    scaleShapes(n, [1.39, 1.5, 0.5], [c[0], c[1], -2.51]);
                    spread(m, side, [1, 1.3, 1]);
                    move(m, side, [0.83, 0, 0]);
                }
                stretch(m, "hip", [1.05, 1.2, 1], [0, 0, 0]);
                spread(m, "fplat", [1.05, 1, 1]);
                inModel(m, "hip", () => {
                    ball(m, "hip", [3.7, 0, 4.3], [3.0, 3.3, 2.9], 6, 12, texture("colossus_dome_b.bmp"), ALL);
                    ball(m, "hip", [-1.7, 0, 4.0], [2.6, 3.3, 3.1], 6, 12, texture("colossus_back_b.bmp"), ALL);
                });
                // the launchers lower at the sides, their muzzles turned up, out until they clear the hull
                move(m, ["l_gun", "r_gun"], [0, 0, -1.8]);
                tilt(m, ["l_gun", "r_gun"], -30);
                for (const [gun, s] of [["l_gun", -1], ["r_gun", 1]])
                    clearOut(m, gun, s, ["hip"], 0.15);
                inModel(m, "hip", () => ring(m, "hip", [1.0, 0, 7.6], 0.95, 1.35, 0, 0.55, 12, texture("steel_b.bmp"), ALL));
                arm(m, load(colour, "AGGRESSOR_3_BALLISTA_T_MULTI_LAUNCHER1"), "top", mount(m, "hip", "gun", [0.3, 0, 8.1]), 1.5);
                scale(m, 1.25);
                return m;
            },
        },
        // Hornet (Boss 3): the Wasp two fifths bigger, wings further out, a longer sting, two gold horns, four wing
        // plates behind the turbines and a gun under each.
        hornet: {
            line: "MACHINE ADMINISTRATOR 3 BOSS T_WASP_LIGHT_STING",
            make: colour => {
                const m = load(colour, "ADMINISTRATOR_1_BOSS_T_WASP_LIGHT_STING");
                spread(m, ["l_wing", "r_wing"], [1, 1.2, 1]);
                // a longer sting: abdomen and tail grow downwards from where they hang, the sting's gun at its tip
                const ab = middle(nodes(m, "ab")[0][0]), tail = middle(nodes(m, "tail")[0][0]);
                stretch(m, "ab", [1, 1, 1.2], [ab[0], ab[1], 0]);
                move(m, "tail", [0, 0, -0.53]);
                stretch(m, "tail", [1, 1, 1.5], [tail[0], tail[1], 0]);
                move(m, "gun", [0, 0, -0.93]);
                const gold = texture("gold_b.bmp"), wing = texture("hornet_wing_b.bmp");
                inModel(m, "head", () => {
                    for (const y of [-0.7, 0.7])
                        spire(m, "head", [2.0, y, 20.5], 0.3, 0.25, 1.7, [-0.6, y * 0.6], gold, ALL);
                });
                inModel(m, "body", () => {
                    for (const s of [-1, 1]) {
                        // their roots sunk into the body
                        plate(m, "body", [[-0.6, s * 0.6, 20.3], [-1.9, s * 0.6, 19.9], [-3.9, s * 2.6, 24.2], [-2.4, s * 2.6, 25.2]], 0.12, wing);
                        plate(m, "body", [[-0.8, s * 1.2, 19.6], [-1.8, s * 1.2, 18.8], [-4.6, s * 4.4, 18.6], [-3.6, s * 4.6, 19.8]], 0.12, wing);
                    }
                });
                // under each turbine a Ballista 3's launcher, a stand-in for whatever weapon is fitted there
                const launcher = load(colour, "AGGRESSOR_3_BALLISTA_T_MULTI_LAUNCHER1");
                for (const [name, side] of [["r_wing", "right"], ["l_wing", "left"]]) {
                    const [[n, index]] = nodes(m, name);
                    // the Wasp's thin fin gives way to the wings
                    n.parts = n.parts.filter(p => !/kfbak\d_b/i.test(p.texture));
                    const w = world(m, index).slice(9);
                    // the launcher at 0.8 reaches 0.26 to 1.48 above its pivot and -0.81 to 1.41 ahead of it; the
                    // turbine's foot is 1.61 under the wing's pivot
                    arm(m, launcher, "top", mount(m, name, side[0] + "_gun", [w[0] - 0.3, w[1], w[2] - 1.61 - 1.48]), 0.8, side);
                }
                // it flies as high as the Wasp, its hip's bob about the Wasp's 20 m
                scale(m, 1.4, ["hip"]);
                return m;
            },
        },
        // Marshal (Commander 6): Commander 5 a quarter bigger, a broader chest, plates over its shoulders and four
        // gold spires off them.
        marshal: {
            line: "MACHINE ADMINISTRATOR 6 COMMANDER LR_PULSE_CANNON_X2",
            make: colour => {
                const m = load(colour, "ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2");
                stretch(m, "body", [1.1, 1.1, 1.1], [0, 0, 0]);
                spread(m, ["l_gun", "r_gun"], [1, 1.1, 1]);
                const gold = texture("gold_b.bmp"), pad = texture("marshal_shoulder_b.bmp");
                for (const [gun, s] of [["r_gun", 1], ["l_gun", -1]]) {
                    const [lo, hi] = boxOf(m, gun);
                    const a = Math.min(Math.abs(lo[1]), Math.abs(hi[1])), b = Math.max(Math.abs(lo[1]), Math.abs(hi[1])), top = hi[2];
                    // from the neck over the gun, down its outside and round under it
                    const cy = (a + b) / 2, cz = (lo[2] + hi[2]) / 2, out = (b - a) / 2 + 0.35, inn = cy - 0.95, rz = (hi[2] - lo[2]) / 2 + 0.35;
                    const profile = [...Array(13).keys()].map(k => {
                        const th = (170 - k * 285 / 12) * Math.PI / 180, c = Math.cos(th);
                        return [cy + (c > 0 ? out : Math.sin(th) > 0 ? inn : (b - a) / 2) * c, cz + rz * Math.sin(th)];
                    });
                    cowl(m, "body", profile.map(([y, z]) => [s * y, z]), [s * cy, cz], lo[0], hi[0] - 0.3, 0.16, pad);
                }
                inModel(m, "body", () => {
                    for (const s of [-1, 1]) {
                        spire(m, "body", [-0.5, s * 1.2, 5.6], 0.36, 0.3, 3.0, [-0.5, s * 0.4], gold, ALL);
                        spire(m, "body", [-0.6, s * 2.0, 5.5], 0.3, 0.26, 2.2, [-0.4, s * 0.6], gold, ALL);
                    }
                });
                roofMount(m, "body", -0.3);
                scale(m, 1.25);
                return m;
            },
        },
        // Phantom (end of the spy line): Spy 5 well over twice as big, a bigger upper body under a rounded roof with
        // a row of spikes, a pair of twin guns left and right in front.
        phantom: {
            line: "MACHINE SPY_LOCATOR 6",
            make: colour => {
                const m = load(colour, "SPY_LOCATOR_5");
                stretch(m, ["l_hv", "r_hv"], [1.3, 1.3, 1.3]);
                stretch(m, "head", [1.5, 1.3, 1.4], [0, 0, -0.22]);
                move(m, "fplat", [0.18, 0, 0.11]);
                const roof = texture("phantom_plate_b.bmp"), steel = texture("steel_b.bmp");
                const z = Math.max(...[[0, 0], [0, 0.8], [0, -0.8], [0.2, 0]].map(([x, y]) => surfaceZ(m, x, y, ["head"])));
                inModel(m, "head", () => {
                    ball(m, "head", [-0.1, 0, z + 0.14], [1.05, 1.85, 0.22], 2, 8, roof, ALL, 0.5);
                    for (const s of [-1, 1]) {
                        spire(m, "head", [-0.4, s * 0.5, z + 0.3], 0.14, 0.1, 1.2, [-0.5, s * 0.15], roof, ALL);
                        spire(m, "head", [-0.6, s * 1.1, z + 0.2], 0.12, 0.1, 0.9, [-0.4, s * 0.25], roof, ALL);
                        // a mount for whatever weapon is fitted, without one
                        box(m, "head", [0.15, s * 0.58, 1.95], [0.62, s * 1.08, 2.42], steel, ALL);
                    }
                });
                // the weapons' mounts low at the boxes' outer faces, bare until a weapon is fitted (the user: "Waffen
                // weiter unten"): a weapon hangs beside its box, held against its outer face, and under the wide roof even
                // the tallest (Multi Launcher 3) leaves 0.2 to spare. In the boxes, at their tops, the medium weapons stood
                // through the roof's edge, and lower down in them they sank into the boxes.
                for (const [gun, s] of [["l_gun", -1], ["r_gun", 1]])
                    mount(m, "head", gun, [0.385, s * 1.17, 1.75]);
                scale(m, 2.4);
                return m;
            },
        },
        // Titan (the constructor line's end): the Behemoth with a ring set into its back and a heavy launcher
        // standing in it, its one weapon (the Goliath's launcher).
        titan: {
            line: "MACHINE CONSTRUCTOR 6 BEHEMOTH",
            make: colour => {
                const m = load(colour, "CONSTRUCTOR_5_BEHEMOTH");
                m.rig.weapons ??= [];
                const c = [-2.6, 0], r = 2.3;
                const under = [...Array(16).keys()].map(k => surfaceZ(m, c[0] + r * Math.cos(k * Math.PI / 8), c[1] + r * Math.sin(k * Math.PI / 8), ["hip"]));
                const low = Math.min(...under), high = Math.max(...under), top = high + 0.55;
                inModel(m, "hip", () => ring(m, "hip", [c[0], c[1], low - 0.15], r - 0.55, r, 0, top - low + 0.15, 16, texture("steel_b.bmp"), ALL));
                arm(m, load(colour, "AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2"), "right", mount(m, "hip", "gun", [c[0], c[1], top]), 1.6, "top");
                // the launcher hangs off its pivot to one side; stand its middle over the ring, its foot on the rim
                const all = points(m, "mult2.cdf");
                const lo = [0, 1, 2].map(i => Math.min(...all.map(p => p[i]))), hi = [0, 1, 2].map(i => Math.max(...all.map(p => p[i])));
                move(m, "gun", [c[0] - (lo[0] + hi[0]) / 2, c[1] - (lo[1] + hi[1]) / 2, top - 0.25 - lo[2]]);
                // the mount itself in the ring's middle on its top, where a hero's weapon goes; the launcher stays
                shiftMount(m, "gun", [c[0], c[1], top]);
                return m;
            },
        },
        // The original's constructors and spies as Wired's heroes wear them: each the original unchanged with empty
        // mounts for the weapons it is given, under an id of its own (an original's id here would replace the
        // original in every match).
        // Dozer 1: a top mount on the cab's sloping roof.
        dozer: {
            line: "MACHINE CONSTRUCTOR 1 DOZER WIRED",
            make: colour => { const m = load(colour, "CONSTRUCTOR_1_DOZER"); roofMount(m, "head"); return m; },
        },
        // Builder 3: a top mount on the ridge of its hopper.
        builder: {
            line: "MACHINE CONSTRUCTOR 3 BUILDER WIRED",
            make: colour => { const m = load(colour, "CONSTRUCTOR_3_BUILDER"); roofMount(m, "head"); return m; },
        },
        // Behemoth 4: a top mount on the cab, whose flat roof runs from x -2.5 (the back's ridge ends there) to its
        // front edge at 0.85; it has no head, the cab is part of the hip.
        behemoth: {
            line: "MACHINE CONSTRUCTOR 4 BEHEMOTH WIRED",
            make: colour => { const m = load(colour, "CONSTRUCTOR_4_BEHEMOTH"); roofMount(m, "hip", 2.6); return m; },
        },
        // Spy 3: a top mount over the head's right half, raised to the tip of the short spike on its top (y 0.08 to
        // 0.28), which a weapon would otherwise pierce. The tall antenna rises on the head's left, its inner edge from
        // y -0.45 at z 2.2 to -0.38 at its tip (z 4.06): on the centre line a weapon reaching further than about 0.38 m
        // to that side stood into it. Over the right hover pod, which reaches further fore and aft than the head, a
        // weapon may also be longer before it hangs off the machine.
        spy3: {
            line: "MACHINE SPY_LOCATOR 3 WIRED",
            make: colour => {
                const m = load(colour, "SPY_LOCATOR_3");
                const [lo, hi] = boxOf(m, "head");
                const tip = Math.max(...points(m, "head").filter(p => Math.abs(p[1]) < 0.3).map(p => p[2]));
                mount(m, "head", "gun", [(lo[0] + hi[0]) / 2, 0.4, tip]);
                return m;
            },
        },
        // Spy 5: a mount on either side of the head, on its walls at half their height (z 1.64 to 2.35 on the left,
        // to 2.28 on the right, whose top falls away; the side blades reach further out, but thin as paper).
        spy5: {
            line: "MACHINE SPY_LOCATOR 5 WIRED",
            make: colour => {
                const m = load(colour, "SPY_LOCATOR_5");
                for (const [gun, s] of [["l_gun", -1], ["r_gun", 1]])
                    mount(m, "head", gun, [0, s * 0.8, 2.0]);
                return m;
            },
        },
        // The locators (geo1, geo3): the original's Geo Locator 1 and 3 unchanged with one empty top mount each, over
        // the right half of the roof, where nothing stands up through a weapon.
        // The crab: on its head's roof (box x -1.02 to 1.01, y -0.62 to 0.59, z 1.26 to 2.18), which goes along with the
        // walk. Its face looks along -y while it walks along +x, sideways, as the original's: the hero's front is +x, so
        // the weapon looks the way it walks, and +y is the crab's back. The middle of the roof (z 1.92) is no place for
        // a weapon: a tall antenna rod rises on the centre line at the back (x -0.77, from z 1.62 to 3.06), two short
        // ones on the face's side (x -0.2 and 0.2, y -0.45, up to z 2.5), and the bump with its flag stands ahead of them
        // (x 0.27 to 0.62, y -0.62 to -0.1, up to z 2.18); a weapon there stood through the rods and the bump. On the
        // back half, a little ahead of the middle (x 0.2, y 0.4, z 1.81), it keeps clear of all of them, its back end
        // of the tall rod (the smaller sizes in HeroDonors.Scale see to the rest).
        crab: {
            line: "MACHINE GEO_LOCATOR 1 WIRED",
            make: colour => {
                const m = load(colour, "GEO_LOCATOR_1");
                const [x, y] = [0.2, 0.4];
                mount(m, "head", "gun", [x, y, surfaceZ(m, x, y, ["head"])]);
                return m;
            },
        },
        // The glider: on its body, an upright egg that pitches and rolls in flight (the hip at z 20 only bobs). An
        // antenna fin stands on the centre line on top behind the middle (x -0.26 to -0.73, up to z 24.32) and the tail
        // flap behind it (x -0.84 to -2.12, z 21.32 to 22.33), sheets without thickness: on the centre line (x 0.3, z
        // 22.06) a long weapon pushed back over the egg went into the fin. Over the right half, ahead of the middle
        // (x 0.4, y 0.3, z 21.58), the weapons keep clear of both (the Pulse Rifle a little smaller, HeroDonors.Scale).
        glider: {
            line: "MACHINE GEO_LOCATOR 3 WIRED",
            make: colour => {
                const m = load(colour, "GEO_LOCATOR_3");
                const [x, y] = [0.4, 0.3];
                mount(m, "body", "gun", [x, y, surfaceZ(m, x, y, ["body"])]);
                return m;
            },
        },
        // The size module on a Reaper 3 (Masse Mk III, x1.5): nothing but bigger.
        masse: {
            line: "MACHINE AGGRESSOR 9 ASSASSIN LR_PLASMA_RIFLE_X2",
            make: colour => { const m = load(colour, REAPER); scale(m, 1.5); return m; },
        },
    };
};

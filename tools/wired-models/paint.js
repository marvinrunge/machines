// Textures for Wired's own machines, painted at the original's size (64 or 128 pixels square) in the colours of the
// original's own textures: read an original BMP to take colours or a patch from, paint plates, seams, rivets, wear
// and stripes, and write a 24-bit BMP to classic/wired/textures, which the game loads for a part whose texture is
// "wired/textures/<name>.bmp" (ClassicAssets.LoadTexture). The same seed paints the same picture.
"use strict";
const fs = require("fs");
const path = require("path");

const GAME = fs.readFileSync(path.resolve(__dirname, "..", "..", "classic", "game_path.txt"), "utf8").trim();
const OUT = path.resolve(__dirname, "..", "..", "classic", "wired", "textures");

/** An original texture (models/texture4/<name>), as rows of [r, g, b] from the top. */
function read(name) {
    const b = fs.readFileSync(path.join(GAME, "models", "texture4", name));
    const offset = b.readUInt32LE(10), w = b.readInt32LE(18), raw = b.readInt32LE(22), bpp = b.readUInt16LE(28);
    const h = Math.abs(raw), stride = Math.ceil((w * bpp) / 32) * 4;
    const palette = bpp === 8 ? Array.from({ length: 256 }, (_, i) => [b[54 + i * 4 + 2], b[54 + i * 4 + 1], b[54 + i * 4]]) : null;
    const img = picture(w, h);
    for (let y = 0; y < h; y++) {
        const row = offset + (raw > 0 ? h - 1 - y : y) * stride;
        for (let x = 0; x < w; x++)
            img.px[y][x] = bpp === 8 ? [...palette[b[row + x]]] : [b[row + x * (bpp / 8) + 2], b[row + x * (bpp / 8) + 1], b[row + x * (bpp / 8)]];
    }
    return img;
}

function picture(w, h, colour = [0, 0, 0]) {
    return { w, h, px: Array.from({ length: h }, () => Array.from({ length: w }, () => [...colour])) };
}

function write(img, name) {
    fs.mkdirSync(OUT, { recursive: true });
    const stride = Math.ceil((img.w * 3) / 4) * 4, size = 54 + stride * img.h;
    const b = Buffer.alloc(size);
    b.write("BM", 0);
    b.writeUInt32LE(size, 2);
    b.writeUInt32LE(54, 10);
    b.writeUInt32LE(40, 14);
    b.writeInt32LE(img.w, 18);
    b.writeInt32LE(img.h, 22);
    b.writeUInt16LE(1, 26);
    b.writeUInt16LE(24, 28);
    b.writeUInt32LE(stride * img.h, 34);
    for (let y = 0; y < img.h; y++)
        for (let x = 0; x < img.w; x++) {
            const [r, g, bl] = img.px[y][x].map(v => Math.max(0, Math.min(255, Math.round(v))));
            const at = 54 + (img.h - 1 - y) * stride + x * 3;
            b[at] = bl; b[at + 1] = g; b[at + 2] = r;
        }
    fs.writeFileSync(path.join(OUT, name), b);
    return "wired/textures/" + name;
}

/** The mean colour of a patch of an image (fractions of its size). */
function mean(img, u0, v0, u1, v1) {
    const s = [0, 0, 0];
    let n = 0;
    for (let y = Math.floor(v0 * img.h); y < Math.ceil(v1 * img.h); y++)
        for (let x = Math.floor(u0 * img.w); x < Math.ceil(u1 * img.w); x++, n++)
            for (let i = 0; i < 3; i++) s[i] += img.px[y][x][i];
    return s.map(v => v / n);
}

const shade = (c, f) => c.map(v => v * f);
const mix = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);

function random(seed) {
    let s = seed >>> 0 || 1;
    return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
}

function set(img, x, y, c) {
    if (x >= 0 && y >= 0 && x < img.w && y < img.h)
        img.px[y][x] = [...c];
}

function rect(img, x0, y0, x1, y1, c) {
    for (let y = y0; y < y1; y++)
        for (let x = x0; x < x1; x++)
            set(img, x, y, c);
}

/** Speckle: each pixel a little lighter or darker, as the original's dithered paint. */
function grain(img, amount, seed) {
    const r = random(seed);
    for (const row of img.px)
        for (let x = 0; x < row.length; x++)
            row[x] = shade(row[x], 1 + (r() - 0.5) * 2 * amount);
}

/** A plate: its face, a light top and left edge, a dark bottom and right edge. */
function plate(img, x0, y0, x1, y1, face) {
    rect(img, x0, y0, x1, y1, face);
    for (let x = x0; x < x1; x++) { set(img, x, y0, shade(face, 1.35)); set(img, x, y1 - 1, shade(face, 0.5)); }
    for (let y = y0; y < y1; y++) { set(img, x0, y, shade(face, 1.25)); set(img, x1 - 1, y, shade(face, 0.55)); }
}

/** A rivet: a light dot with a dark shadow under it. */
function rivet(img, x, y, face) {
    set(img, x, y, shade(face, 1.6));
    set(img, x + 1, y + 1, shade(face, 0.45));
}

function rivets(img, x0, y0, x1, y1, step, face) {
    for (let x = x0 + 2; x < x1 - 2; x += step) { rivet(img, x, y0 + 2, face); rivet(img, x, y1 - 4, face); }
    for (let y = y0 + 2 + step; y < y1 - 4; y += step) { rivet(img, x0 + 2, y, face); rivet(img, x1 - 4, y, face); }
}

/** Worn paint: patches near the edges where the bare metal shows through. */
function wear(img, metal, count, seed, x0 = 0, y0 = 0, x1 = img.w, y1 = img.h) {
    const r = random(seed);
    for (let k = 0; k < count; k++) {
        const edge = r() < 0.5;
        const x = Math.floor(edge ? (r() < 0.5 ? x0 + r() * 3 : x1 - 1 - r() * 3) : x0 + r() * (x1 - x0));
        const y = Math.floor(edge ? y0 + r() * (y1 - y0) : (r() < 0.5 ? y0 + r() * 3 : y1 - 1 - r() * 3));
        const len = 1 + Math.floor(r() * 3);
        for (let i = 0; i < len; i++)
            set(img, x + (r() < 0.5 ? i : 0), y + (r() < 0.5 ? 0 : i), mix(img.px[Math.min(y, img.h - 1)][Math.min(x, img.w - 1)], metal, 0.7));
    }
}

/** Diagonal warning stripes in two colours. */
function stripes(img, x0, y0, x1, y1, a, b, width = 4) {
    for (let y = y0; y < y1; y++)
        for (let x = x0; x < x1; x++)
            set(img, x, y, Math.floor((x + y) / width) % 2 ? a : b);
}

/** A slatted vent: dark slits with a light lip. */
function vent(img, x0, y0, x1, y1, face, gap = 3) {
    rect(img, x0, y0, x1, y1, shade(face, 0.35));
    for (let y = y0 + 1; y < y1; y += gap) {
        for (let x = x0; x < x1; x++) set(img, x, y, shade(face, 1.1));
    }
}

/** A patch of another image copied in, scaled to the given box. */
function stamp(img, from, u0, v0, u1, v1, x0, y0, x1, y1) {
    for (let y = y0; y < y1; y++)
        for (let x = x0; x < x1; x++) {
            const sx = Math.floor((u0 + ((x - x0 + 0.5) / (x1 - x0)) * (u1 - u0)) * from.w);
            const sy = Math.floor((v0 + ((y - y0 + 0.5) / (y1 - y0)) * (v1 - v0)) * from.h);
            set(img, x, y, from.px[Math.min(sy, from.h - 1)][Math.min(sx, from.w - 1)]);
        }
}

module.exports = { read, picture, write, mean, shade, mix, random, set, rect, grain, plate, rivet, rivets, wear, stripes, vent, stamp };

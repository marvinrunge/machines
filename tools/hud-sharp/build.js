// Makes the first-person HUD's painted art sharp at today's screen sizes: the original's 640x480 bitmaps of the weapon
// panels (gui/fstpersn/weapon), the radar's casing, start-up frames and bars (gui/fstpersn/radar), the machine pictures
// beside it (gui/fstpersn/machines) and the tower pictures a Wired constructor offers (gui/construc/*/me*.bmp,
// commands/deconst.bmp) are enlarged four times with Real-ESRGAN (realesrgan-x4plus, which keeps the hoses' ribs and the
// pictures' grids that the anime model flattens) and written as PNG to classic/remake/gui, by the same relative path in
// lower case (gui/fstpersn/radar/RMmap.BMP -> classic/remake/gui/fstpersn/radar/rmmap.png). The game (HudArt.cs) shrinks
// them to the size the layout wants and falls back to the bitmaps when they are missing. The crosshair, the radar's
// blips, arrow and dome are drawn as shapes by the game and need nothing from here.
//
// Magenta is see-through. Where the original screens the view through a checkerboard of magenta (the radar's scanner
// glass), the pattern becomes half see-through, evenly. Cut-out pixels take their neighbours' colour before enlarging,
// so no pink fringe appears, and the coverage is enlarged on its own.
//
//   node tools/hud-sharp/build.js [game folder] [output folder]
//
// The game folder defaults to classic/game_path.txt, the output folder to classic/remake/gui; HUD_SHARP_MODEL names
// another of the upscaler's models to try. About 20 s on a GPU. Needs tools/texture-upscale/bin (tools/texture-upscale/fetch.sh);
// without it the art is enlarged smoothly (bicubic) instead, which is still sharper than blown-up pixels.
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');
const zlib = require('zlib');
const { spawnSync } = require('child_process');

const FORMAT = 1;
const SCALE = 4;
const PAD = 6;
const MODEL = process.env.HUD_SHARP_MODEL || 'realesrgan-x4plus';
const root = path.resolve(__dirname, '..', '..');
const out = process.argv[3] ? path.resolve(process.argv[3]) : path.join(root, 'classic', 'remake', 'gui');
const upscaler = path.join(root, 'tools', 'texture-upscale', 'bin', 'realesrgan-ncnn-vulkan.exe');

// ---- reading the game's bitmaps -------------------------------------------------------------------------------------

function readBmp(file) {
  const b = fs.readFileSync(file);
  if (b.toString('latin1', 0, 2) !== 'BM') return null;
  const offset = b.readUInt32LE(10), header = b.readUInt32LE(14);
  const width = b.readInt32LE(18), rawHeight = b.readInt32LE(22), bits = b.readUInt16LE(28), compression = b.readUInt32LE(30);
  if (compression !== 0 && compression !== 3) return null;
  const height = Math.abs(rawHeight);
  const colours = b.readUInt32LE(46) || (bits <= 8 ? 1 << bits : 0);
  const palette = [];
  for (let i = 0; i < colours; i++) {
    const o = 14 + header + i * 4;
    palette.push([b[o + 2], b[o + 1], b[o]]);
  }
  const stride = Math.ceil(width * bits / 32) * 4;
  const rgba = new Uint8Array(width * height * 4);
  for (let y = 0; y < height; y++) {
    const row = offset + (rawHeight > 0 ? height - 1 - y : y) * stride;
    for (let x = 0; x < width; x++) {
      let c;
      if (bits === 24 || bits === 32) {
        const o = row + x * (bits / 8);
        c = [b[o + 2], b[o + 1], b[o]];
      } else {
        const bit = x * bits;
        const index = (b[row + (bit >> 3)] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
        c = palette[index] ?? [0, 0, 0];
      }
      rgba.set([c[0], c[1], c[2], 255], (y * width + x) * 4);
    }
  }
  return { width, height, rgba };
}

// ---- PNG, without packages ----------------------------------------------------------------------------------------

const crcTable = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});
function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function writePng(file, width, height, rgba, channels = 4) {
  const raw = Buffer.alloc((width * channels + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * channels + 1)] = 0;
    for (let x = 0; x < width; x++)
      for (let c = 0; c < channels; c++)
        raw[y * (width * channels + 1) + 1 + x * channels + c] = rgba[(y * width + x) * 4 + c];
  }
  const chunk = (type, data) => {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));
    return Buffer.concat([length, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = channels === 4 ? 6 : 2;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0)),
  ]));
}

function readPng(file) {
  const b = fs.readFileSync(file);
  let at = 8, width = 0, height = 0, depth = 0, type = 0;
  const data = [];
  while (at < b.length) {
    const length = b.readUInt32BE(at), name = b.toString('latin1', at + 4, at + 8);
    const body = b.subarray(at + 8, at + 8 + length);
    if (name === 'IHDR') [width, height, depth, type] = [body.readUInt32BE(0), body.readUInt32BE(4), body[8], body[9]];
    else if (name === 'IDAT') data.push(body);
    at += 12 + length;
  }
  const channels = { 0: 1, 2: 3, 4: 2, 6: 4 }[type];
  if (depth !== 8 || !channels) throw new Error(`${file}: PNG of depth ${depth}, type ${type}`);
  const raw = zlib.inflateSync(Buffer.concat(data));
  const stride = width * channels;
  const pixels = Buffer.alloc(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    for (let x = 0; x < stride; x++) {
      const value = raw[y * (stride + 1) + 1 + x];
      const a = x >= channels ? pixels[y * stride + x - channels] : 0;
      const up = y > 0 ? pixels[(y - 1) * stride + x] : 0;
      const corner = x >= channels && y > 0 ? pixels[(y - 1) * stride + x - channels] : 0;
      let predicted = 0;
      if (filter === 1) predicted = a;
      else if (filter === 2) predicted = up;
      else if (filter === 3) predicted = (a + up) >> 1;
      else if (filter === 4) {
        const p = a + up - corner, pa = Math.abs(p - a), pb = Math.abs(p - up), pc = Math.abs(p - corner);
        predicted = pa <= pb && pa <= pc ? a : pb <= pc ? up : corner;
      }
      pixels[y * stride + x] = (value + predicted) & 0xff;
    }
  }
  const rgba = new Uint8Array(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    const p = pixels.subarray(i * channels, i * channels + channels);
    const [r, g, bl, a] = channels === 1 ? [p[0], p[0], p[0], 255] : channels === 2 ? [p[0], p[0], p[0], p[1]] : channels === 3 ? [p[0], p[1], p[2], 255] : p;
    rgba.set([r, g, bl, a], i * 4);
  }
  return { width, height, rgba };
}

// ---- coverage ---------------------------------------------------------------------------------------------------

/**
 * How much of each pixel covers: magenta none, anything else all, and a checkerboard of the two (the original's way
 * of drawing see-through glass) half, evenly.
 */
function coverage(image) {
  const { width: w, height: h, rgba } = image;
  const solid = new Uint8Array(w * h);
  for (let i = 0; i < w * h; i++)
    solid[i] = rgba[i * 4] === 255 && rgba[i * 4 + 1] === 0 && rgba[i * 4 + 2] === 255 ? 0 : 1;
  const at = (x, y) => (x < 0 || y < 0 || x >= w || y >= h ? -1 : solid[y * w + x]);
  // A pixel lies in a checkerboard when its four sides differ from it and its four corners match it.
  const checker = new Uint8Array(w * h);
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const s = solid[y * w + x];
      const sides = [at(x - 1, y), at(x + 1, y), at(x, y - 1), at(x, y + 1)];
      const corners = [at(x - 1, y - 1), at(x + 1, y - 1), at(x - 1, y + 1), at(x + 1, y + 1)];
      if (sides.every(v => v === 1 - s) && corners.every(v => v === s)) checker[y * w + x] = 1;
    }
  // Its edge pixels miss a neighbour or more. A gap there alternates with a pixel found in it; a covered pixel there does
  // too, and has the colour of the glass nearby, unlike the rim round it.
  const near = (x, y) => {
    let [r, g, b, n] = [0, 0, 0, 0];
    for (let dy = -2; dy <= 2; dy++)
      for (let dx = -2; dx <= 2; dx++) {
        const [nx, ny] = [x + dx, y + dy];
        if (nx < 0 || ny < 0 || nx >= w || ny >= h || !checker[ny * w + nx] || !solid[ny * w + nx]) continue;
        const j = (ny * w + nx) * 4;
        [r, g, b, n] = [r + rgba[j], g + rgba[j + 1], b + rgba[j + 2], n + 1];
      }
    return n === 0 ? null : [r / n, g / n, b / n];
  };
  for (let pass = 0; pass < 3; pass++) {
    const grown = checker.slice();
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        if (checker[y * w + x]) continue;
        const s = solid[y * w + x];
        let n = 0;
        for (const [dx, dy] of [[-1, 0], [1, 0], [0, -1], [0, 1]]) {
          const [nx, ny] = [x + dx, y + dy];
          if (nx >= 0 && ny >= 0 && nx < w && ny < h && checker[ny * w + nx] && solid[ny * w + nx] === 1 - s) n++;
        }
        if (n === 0) continue;
        if (!s) grown[y * w + x] = 1;
        else {
          const glass = near(x, y), i = (y * w + x) * 4;
          if (glass && Math.abs(rgba[i] - glass[0]) + Math.abs(rgba[i + 1] - glass[1]) + Math.abs(rgba[i + 2] - glass[2]) < 90) grown[y * w + x] = 1;
        }
      }
    checker.set(grown);
  }
  const alpha = new Float32Array(w * h);
  for (let i = 0; i < w * h; i++) alpha[i] = checker[i] ? 0.5 : solid[i];
  return { alpha, checker, solid };
}

/** Gives uncovered pixels the colour of their covered neighbours, and evens out the colour of a checkerboard's glass. */
function fill(image, cover) {
  const { width: w, height: h, rgba } = image;
  const known = Array.from(cover.solid, s => s === 1);
  for (let pass = 0; pass < w + h && known.includes(false); pass++) {
    const next = known.slice();
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        const i = y * w + x;
        if (known[i]) continue;
        let [r, g, b, n] = [0, 0, 0, 0];
        // A checkerboard's gaps look at their four sides only, the pixels of the glass.
        const ways = cover.checker[i] ? [[-1, 0], [1, 0], [0, -1], [0, 1]] : [[-1, -1], [0, -1], [1, -1], [-1, 0], [1, 0], [-1, 1], [0, 1], [1, 1]];
        for (const [dx, dy] of ways) {
          const [nx, ny] = [x + dx, y + dy];
          if (nx < 0 || ny < 0 || nx >= w || ny >= h || !known[ny * w + nx]) continue;
          const j = (ny * w + nx) * 4;
          [r, g, b, n] = [r + rgba[j], g + rgba[j + 1], b + rgba[j + 2], n + 1];
        }
        if (n === 0) continue;
        rgba.set([r / n, g / n, b / n], i * 4);
        next[i] = true;
      }
    known.splice(0, known.length, ...next);
  }
  // The glass: each pixel the average of itself and its neighbours on the glass, twice, so no pattern is left to enlarge.
  for (let pass = 0; pass < 2; pass++) {
    const source = rgba.slice();
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        if (!cover.checker[y * w + x]) continue;
        let [r, g, b, n] = [0, 0, 0, 0];
        for (let dy = -1; dy <= 1; dy++)
          for (let dx = -1; dx <= 1; dx++) {
            const [nx, ny] = [x + dx, y + dy];
            if (nx < 0 || ny < 0 || nx >= w || ny >= h || !cover.checker[ny * w + nx]) continue;
            const j = (ny * w + nx) * 4;
            [r, g, b, n] = [r + source[j], g + source[j + 1], b + source[j + 2], n + 1];
          }
        rgba.set([r / n, g / n, b / n], (y * w + x) * 4);
      }
  }
}

/** The picture with its edge pixels repeated PAD times round it, so the upscaler sees no border. */
function padded(width, height, value) {
  const [pw, ph] = [width + 2 * PAD, height + 2 * PAD];
  const rgba = new Uint8Array(pw * ph * 4);
  for (let y = 0; y < ph; y++)
    for (let x = 0; x < pw; x++) {
      const [sx, sy] = [Math.min(width - 1, Math.max(0, x - PAD)), Math.min(height - 1, Math.max(0, y - PAD))];
      rgba.set([...value(sx, sy), 255], (y * pw + x) * 4);
    }
  return { width: pw, height: ph, rgba };
}

// ---- enlarging without the upscaler ------------------------------------------------------------------------------

function cubic(t) {
  const a = -0.5, x = Math.abs(t);
  return x <= 1 ? (a + 2) * x ** 3 - (a + 3) * x ** 2 + 1 : x < 2 ? a * x ** 3 - 5 * a * x ** 2 + 8 * a * x - 4 * a : 0;
}

function bicubic(image, scale) {
  const { width: w, height: h, rgba } = image;
  const [W, H] = [w * scale, h * scale];
  const result = new Uint8Array(W * H * 4);
  for (let y = 0; y < H; y++)
    for (let x = 0; x < W; x++) {
      const [fx, fy] = [(x + 0.5) / scale - 0.5, (y + 0.5) / scale - 0.5];
      const [ix, iy] = [Math.floor(fx), Math.floor(fy)];
      const sum = [0, 0, 0, 0];
      let weight = 0;
      for (let j = -1; j <= 2; j++)
        for (let i = -1; i <= 2; i++) {
          const k = cubic(fx - (ix + i)) * cubic(fy - (iy + j));
          const o = (Math.min(h - 1, Math.max(0, iy + j)) * w + Math.min(w - 1, Math.max(0, ix + i))) * 4;
          for (let c = 0; c < 4; c++) sum[c] += rgba[o + c] * k;
          weight += k;
        }
      for (let c = 0; c < 4; c++) result[(y * W + x) * 4 + c] = Math.min(255, Math.max(0, Math.round(sum[c] / weight)));
    }
  return { width: W, height: H, rgba: result };
}

// ---- the art ------------------------------------------------------------------------------------------------------

function findFolder(base, relative) {
  let folder = base;
  for (const part of relative.split('/').filter(Boolean)) {
    const match = fs.existsSync(folder) && fs.readdirSync(folder).find(name => name.toLowerCase() === part.toLowerCase());
    if (!match) return null;
    folder = path.join(folder, match);
  }
  return folder;
}

/** The bitmaps to enlarge, as [path under gui/ in lower case, file]. */
function artOf(gui) {
  const list = [];
  const add = (folder, keep) => {
    const found = findFolder(gui, folder);
    if (!found) return;
    for (const name of fs.readdirSync(found))
      if (name.toLowerCase().endsWith('.bmp') && keep(name.toLowerCase()))
        list.push([`${folder}/${name.toLowerCase()}`, path.join(found, name)]);
  };
  const all = () => true;
  add('fstpersn/weapon', all);
  add('fstpersn/radar', all);
  add('fstpersn/machines', all);
  for (const race of ['red', 'blue', 'green', 'yellow']) add(`construc/${race}`, name => name.startsWith('me'));
  add('commands', name => name === 'deconst.bmp');
  return list;
}

function main() {
  const gamePath = process.argv[2] ?? fs.readFileSync(path.join(root, 'classic', 'game_path.txt'), 'utf8').trim();
  const game = path.isAbsolute(gamePath) ? gamePath : path.join(root, 'classic', gamePath);
  const gui = findFolder(game, 'gui');
  if (!gui) throw new Error(`no gui folder in ${game}`);
  const art = artOf(gui);
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'hud-sharp-'));
  const [staged, enlarged] = [path.join(work, 'in'), path.join(work, 'out')];
  fs.mkdirSync(staged);
  fs.mkdirSync(enlarged);

  const jobs = [];
  art.forEach(([relative, file], index) => {
    const image = readBmp(file);
    if (!image) return console.warn(`skipped ${relative}: not a bitmap this reads`);
    const cover = coverage(image);
    fill(image, cover);
    const { width: w, height: h } = image;
    const colour = padded(w, h, (x, y) => image.rgba.subarray((y * w + x) * 4, (y * w + x) * 4 + 3));
    const mask = padded(w, h, (x, y) => Array(3).fill(Math.round(cover.alpha[y * w + x] * 255)));
    const name = String(index).padStart(4, '0');
    writePng(path.join(staged, `${name}c.png`), colour.width, colour.height, colour.rgba, 3);
    const clear = cover.alpha.every(a => a === 1);
    if (!clear) writePng(path.join(staged, `${name}a.png`), mask.width, mask.height, mask.rgba, 3);
    jobs.push({ relative, name, w, h, clear, colour, mask });
  });

  let upscaled = false;
  if (fs.existsSync(upscaler)) {
    const run = spawnSync(upscaler, ['-i', staged, '-o', enlarged, '-n', MODEL, '-s', String(SCALE), '-f', 'png'], {
      cwd: path.dirname(upscaler),
      env: { ...process.env, VK_LOADER_LAYERS_DISABLE: '~implicit~' },
      encoding: 'utf8',
      maxBuffer: 64 << 20,
    });
    upscaled = run.status === 0;
    if (!upscaled) console.warn(`Real-ESRGAN failed (${run.status}), enlarging smoothly instead:\n${(run.stderr ?? '').slice(-2000)}`);
  } else console.warn(`no ${upscaler}: enlarging smoothly instead (tools/texture-upscale/fetch.sh fetches it)`);

  const big = (job, part, source) => {
    const file = path.join(enlarged, `${job.name}${part}.png`);
    if (upscaled && fs.existsSync(file)) {
      const image = readPng(file);
      if (image.width === source.width * SCALE && image.height === source.height * SCALE) return image;
    }
    return bicubic(source, SCALE);
  };
  let made = 0;
  for (const job of jobs) {
    const colour = big(job, 'c', job.colour);
    const mask = job.clear ? null : big(job, 'a', job.mask);
    const [W, H] = [job.w * SCALE, job.h * SCALE];
    const rgba = new Uint8Array(W * H * 4);
    for (let y = 0; y < H; y++)
      for (let x = 0; x < W; x++) {
        const from = ((y + PAD * SCALE) * colour.width + x + PAD * SCALE) * 4;
        const to = (y * W + x) * 4;
        rgba.set(colour.rgba.subarray(from, from + 3), to);
        rgba[to + 3] = mask ? mask.rgba[from] : 255;
      }
    writePng(path.join(out, job.relative.replace(/\.bmp$/, '.png')), W, H, rgba);
    made++;
  }
  fs.writeFileSync(path.join(out, 'format.txt'), `${FORMAT}\n`);
  fs.rmSync(work, { recursive: true, force: true });
  console.log(`${made} pictures in ${out}, ${upscaled ? `enlarged by Real-ESRGAN (${MODEL})` : 'enlarged smoothly (bicubic)'}`);
}

main();

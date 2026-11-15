// The textures painted for Wired's own machines (see paint.js), by file name. Colours are taken from the original's
// own textures, so the new surfaces sit beside the old ones in the same paint.
"use strict";
const paint = require("./paint");

const gruntRed = () => paint.mean(paint.read("gr3fr1_b.bmp"), 0.02, 0.7, 0.14, 0.84);
const steel = () => paint.mean(paint.read("co256.bmp"), 0.56, 0.56, 0.72, 0.7);
const legRed = () => paint.mean(paint.read("asleg1_b.bmp"), 0.1, 0.05, 0.9, 0.4);
const BLACK = [40, 36, 30], YELLOW = [214, 178, 40], METAL = [150, 140, 130];

/** The Goliath's hull red with faint panel seams, with or without its emblem where u = 0 (split across the seam). */
function colossusHull(emblem) {
    const hull = paint.read("bal4s1_b.bmp"), red = paint.mean(hull, 0.7, 0.02, 0.98, 0.35);
    const img = paint.picture(64, 64, red);
    for (let x = 8; x < 64; x += 16) for (let y = 0; y < 64; y++) paint.set(img, x, y, paint.shade(red, 0.7));
    for (let y = 0; y < 64; y += 21) for (let x = 0; x < 64; x++) paint.set(img, x, y, paint.shade(red, 0.75));
    if (emblem) {
        paint.stamp(img, hull, 0.47, 0.21, 0.565, 0.44, 58, 16, 64, 31);
        paint.stamp(img, hull, 0.565, 0.21, 0.66, 0.44, 0, 16, 6, 31);
    }
    paint.wear(img, METAL, 50, emblem ? 97 : 103);
    paint.grain(img, 0.06, emblem ? 101 : 107);
    return img;
}

module.exports = {
    /** A knee shield: the Reaper's leg red, a dark rim and the shin's chevrons pointing up. */
    "knee_b.bmp": () => {
        const red = legRed();
        const img = paint.picture(64, 64, red);
        paint.plate(img, 4, 4, 60, 60, red);
        for (let y = 12; y < 48; y += 12)
            for (let x = 8; x < 56; x++) {
                const d = Math.round(Math.abs(x - 32) / 2);
                paint.rect(img, x, y + d - 6, x + 1, y + d - 4, paint.shade(red, 0.45));
            }
        paint.rect(img, 0, 0, 64, 3, paint.shade(red, 0.35));
        paint.wear(img, METAL, 60, 59);
        paint.grain(img, 0.08, 61);
        return img;
    },
    /** Colossus's rounded front: the Goliath's hull red, faint panel seams, its emblem where the dome faces forward
     * (u = 0 is straight ahead, so the emblem is split across the seam). */
    "colossus_dome_b.bmp": () => colossusHull(true),
    /** Colossus's rounded back: the same paint without the emblem. */
    "colossus_back_b.bmp": () => colossusHull(false),
    /** Harvester's leg armour: thick red plates, dark seams, rivets, a vent. */
    "leg_armour_b.bmp": () => {
        const red = paint.shade(gruntRed(), 0.95);
        const img = paint.picture(64, 64, red);
        for (let y = 0; y < 64; y += 32)
            for (let x = 0; x < 64; x += 32) {
                paint.plate(img, x, y, x + 32, y + 32, paint.shade(red, (x + y) % 64 ? 0.88 : 1));
                paint.rivets(img, x, y, x + 32, y + 32, 7, red);
            }
        paint.vent(img, 38, 40, 58, 54, red);
        paint.wear(img, METAL, 90, 67);
        paint.grain(img, 0.09, 71);
        return img;
    },
    /** Hornet's wings: the turbines' red in long plates, a dark rib along them, worn edges. */
    "hornet_wing_b.bmp": () => {
        const red = paint.mean(paint.read("kturb1_b.bmp"), 0.1, 0.1, 0.9, 0.5);
        const img = paint.picture(64, 64, red);
        for (let y = 0; y < 64; y += 16)
            paint.plate(img, 0, y, 64, y + 16, paint.shade(red, y % 32 ? 0.9 : 1));
        paint.rect(img, 0, 30, 64, 34, paint.shade(red, 0.4));
        paint.rivets(img, 0, 0, 64, 64, 10, red);
        paint.wear(img, METAL, 80, 73);
        paint.grain(img, 0.08, 79);
        return img;
    },
    /** Marshal's shoulder plates: the Commander's red, riveted, the warning stripes along the front edge. */
    "marshal_shoulder_b.bmp": () => {
        const red = paint.shade(gruntRed(), 0.85);
        const img = paint.picture(64, 64, red);
        for (let x = 0; x < 64; x += 16)
            paint.plate(img, x, 0, x + 16, 52, paint.shade(red, x % 32 ? 0.9 : 1));
        paint.rivets(img, 0, 0, 64, 52, 8, red);
        paint.stripes(img, 0, 52, 64, 64, BLACK, YELLOW, 5);
        paint.wear(img, METAL, 70, 83);
        paint.grain(img, 0.08, 89);
        return img;
    },
    /** Sarge's fenders: the Grunt's red in riveted plates along their length, worn at the edges. */
    "sarge_fender_b.bmp": () => {
        const red = gruntRed();
        const img = paint.picture(64, 64, red);
        for (let x = 0; x < 64; x += 16)
            paint.plate(img, x, 0, x + 16, 64, paint.shade(red, x % 32 ? 0.92 : 1));
        paint.rect(img, 0, 28, 64, 36, paint.shade(red, 0.75));
        for (let x = 0; x < 64; x++) paint.set(img, x, 28, paint.shade(red, 1.3));
        for (let x = 0; x < 64; x += 16)
            paint.rivets(img, x, 0, x + 16, 64, 6, red);
        paint.wear(img, METAL, 70, 7);
        paint.grain(img, 0.08, 3);
        return img;
    },
    /** Sarge's front plate: red, a band of the original's yellow and black stripes along its foot. */
    "sarge_plate_b.bmp": () => {
        const red = gruntRed();
        const img = paint.picture(64, 64, red);
        paint.plate(img, 0, 0, 64, 46, red);
        paint.rivets(img, 0, 0, 64, 46, 8, red);
        paint.stripes(img, 0, 46, 64, 64, BLACK, YELLOW, 5);
        paint.wear(img, METAL, 50, 11);
        paint.grain(img, 0.08, 5);
        return img;
    },
    /** Bare steel, as the original's co256 panels, plated and riveted: arms, rings, axles. */
    "steel_b.bmp": () => {
        const s = steel();
        const img = paint.picture(64, 64, s);
        for (let y = 0; y < 64; y += 32)
            for (let x = 0; x < 64; x += 32) {
                paint.plate(img, x, y, x + 32, y + 32, paint.shade(s, (x + y) % 64 ? 0.9 : 1));
                paint.rivets(img, x, y, x + 32, y + 32, 7, s);
            }
        paint.grain(img, 0.07, 13);
        return img;
    },
    /** Old Iron's blade: steel scraped bright along the cutting edge, the stripes along the top. */
    "oldiron_blade_b.bmp": () => {
        const s = paint.shade(steel(), 0.85);
        const img = paint.picture(64, 64, s);
        for (let x = 0; x < 64; x += 21)
            paint.plate(img, x, 0, Math.min(64, x + 21), 64, s);
        paint.stripes(img, 0, 0, 64, 8, BLACK, YELLOW, 5);
        for (let y = 54; y < 64; y++)
            for (let x = 0; x < 64; x++) paint.set(img, x, y, paint.mix(img.px[y][x], [225, 225, 220], (y - 54) / 14));
        paint.rivets(img, 0, 8, 64, 54, 9, s);
        paint.wear(img, [70, 50, 35], 90, 17);
        paint.grain(img, 0.1, 19);
        return img;
    },
    /** An amber lamp behind a dark frame, brightest at its middle. */
    "lamp_b.bmp": () => {
        const img = paint.picture(32, 32, [45, 38, 30]);
        for (let y = 3; y < 29; y++)
            for (let x = 3; x < 29; x++)
                paint.set(img, x, y, paint.mix([255, 236, 150], [205, 110, 20], Math.min(1, Math.hypot(x - 15.5, y - 13) / 16)));
        paint.rect(img, 3, 15, 29, 16, [90, 60, 25]);
        paint.rect(img, 15, 3, 16, 29, [90, 60, 25]);
        return img;
    },
    /** Widow's abdomen: the Reaper's legs' red in dark bands, a yellow mark either side. */
    "widow_abdomen_b.bmp": () => {
        // the body's paint, so body and abdomen look of one piece
        const red = paint.shade(paint.mean(paint.read("as2hd1_b.bmp"), 0, 0, 1, 1), 1.35);
        const img = paint.picture(64, 64, red);
        for (let y = 0; y < 64; y += 16) paint.rect(img, 0, y, 64, y + 3, paint.shade(red, 0.4));
        for (let y = 20; y < 36; y++) {
            const w = Math.abs(y - 28) + 1;
            paint.rect(img, 14, y, 14 + w, y + 1, YELLOW);
            paint.rect(img, 46, y, 46 + w, y + 1, YELLOW);
        }
        paint.wear(img, [120, 100, 90], 60, 29);
        paint.grain(img, 0.08, 31);
        return img;
    },
    /** Gold spires: the original's warning yellow, banded dark. */
    "gold_b.bmp": () => {
        const img = paint.picture(32, 64, [214, 170, 50]);
        for (let y = 0; y < 64; y += 10) paint.rect(img, 0, y, 32, y + 2, [90, 60, 20]);
        for (let y = 0; y < 64; y++) { paint.set(img, 0, y, [250, 220, 120]); paint.set(img, 31, y, [120, 80, 20]); }
        paint.grain(img, 0.08, 37);
        return img;
    },
    /** Phantom's roof: dark red plates with a stripe down the middle. */
    "phantom_plate_b.bmp": () => {
        const red = paint.shade(gruntRed(), 0.8);
        const img = paint.picture(64, 64, red);
        for (let y = 0; y < 64; y += 21) paint.plate(img, 0, y, 64, Math.min(64, y + 21), red);
        paint.stripes(img, 28, 0, 36, 64, BLACK, YELLOW, 4);
        paint.rivets(img, 0, 0, 64, 64, 8, red);
        paint.wear(img, METAL, 60, 47);
        paint.grain(img, 0.08, 53);
        return img;
    },
};

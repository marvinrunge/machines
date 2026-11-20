"""The game's icon, made from the menus' own art: the chip every menu panel and button is (game/assets/menu/chip.png,
see tools/blender/menu-art.py and ChipArt.cs) round its green screen, a little lit, with a yellow M in the menus' font
(Saira SemiBold) on it, as the website's icon draws it.

    python tools/icon/make-icon.py

Writes game/assets/icon.png (256 px, the window's icon, application/config/icon) and game/assets/icon.ico (16 to 256
px, the Windows executable's and taskbar's icon, application/config/windows_native_icon and the export preset). The
small sizes are drawn from the large one; below 48 px the M is drawn bolder so it still reads."""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CHIP = os.path.join(ROOT, "game", "assets", "menu", "chip.png")
FONT = os.path.join(ROOT, "game", "assets", "fonts", "saira", "Saira.ttf")
OUT = os.path.join(ROOT, "game", "assets")

S = 256                       # chip.png's size; one unit here is one of its pixels
BODY = (40, 40, 216, 216)     # the chip's body (ChipArt: Inset 40)
SCREEN = (53, 53, 203, 203)   # its screen, Frame - 3 in from the body
CROP = (18, 22, 240, 244)     # the chip with its pins, square
SIZES = [256, 128, 64, 48, 32, 24, 16]

# MenuTheme / ChipArt colours.
BRIGHT = (255, 217, 92)
SCREEN_CORNERS = [(0.07, 0.2, 0.08), (0.05, 0.15, 0.06), (0.022, 0.075, 0.03), (0.04, 0.12, 0.05)]   # TL TR BR BL
LIGHT = [(0.0, (0.86, 1.0, 0.72, 1.0)), (0.3, (0.38, 0.98, 0.24, 1.0)), (0.75, (0.1, 0.55, 0.08, 0.85)), (1.0, (0.05, 0.3, 0.05, 0.6))]
LIT = 0.32


def lerp(a, b, t):
    return tuple(x + (y - x) * t for x, y in zip(a, b))


def screen():
    """ChipArt.DrawScreen: a gradient between its corners, a faint reflection from the top left, and the light."""
    x0, y0, x1, y1 = SCREEN
    w, h = x1 - x0, y1 - y0
    img = Image.new("RGBA", (w, h))
    px = img.load()
    for y in range(h):
        v = y / (h - 1)
        for x in range(w):
            u = x / (w - 1)
            tl, tr, br, bl = SCREEN_CORNERS
            c = lerp(lerp(tl, tr, u), lerp(bl, br, u), v)
            # The reflection: a triangle from the top left, 6 % at its corner fading to nothing.
            r = max(0.0, 1 - u / 0.6 - v / 0.9)
            c = lerp(c, (0.7, 1.0, 0.7), 0.06 * r)
            # The light: radial from the middle to the screen's edge (GradientTexture2D, FillTo 1, 0.5).
            d = min(1.0, ((u - 0.5) ** 2 + (v - 0.5) ** 2) ** 0.5 / 0.5)
            for (o0, c0), (o1, c1) in zip(LIGHT, LIGHT[1:]):
                if d <= o1:
                    lc = lerp(c0, c1, (d - o0) / (o1 - o0))
                    break
            c = lerp(c, lc[:3], lc[3] * LIT)
            px[x, y] = tuple(round(255 * min(1.0, k)) for k in c) + (255,)
    return img


def letter(weight, size):
    """The M in Saira at that weight, Bright, with a soft yellow glow under it, on a transparent chip-sized layer."""
    font = ImageFont.truetype(FONT, size)
    font.set_variation_by_axes([weight, 100])
    mask = Image.new("L", (S, S))
    draw = ImageDraw.Draw(mask)
    cx, cy = (SCREEN[0] + SCREEN[2]) / 2, (SCREEN[1] + SCREEN[3]) / 2
    l, t, r, b = draw.textbbox((0, 0), "M", font=font)
    draw.text((cx - (l + r) / 2, cy - (t + b) / 2), "M", font=font, fill=255)
    glow = Image.new("RGBA", (S, S), BRIGHT + (0,))
    glow.putalpha(mask.filter(ImageFilter.GaussianBlur(7)).point(lambda a: int(a * 0.55)))
    text = Image.new("RGBA", (S, S), BRIGHT + (0,))
    text.putalpha(mask)
    return Image.alpha_composite(glow, text)


def icon(weight, size):
    chip = Image.open(CHIP).convert("RGBA")
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(screen(), SCREEN[:2])
    img = Image.alpha_composite(img, chip)
    # The chip's screen is see-through in chip.png; the letter goes on the glass, under nothing.
    img = Image.alpha_composite(img, letter(weight, size))
    return img.crop(CROP)


def main():
    large = icon(600, 150)
    bold = icon(800, 160)
    images = []
    for n in SIZES:
        source = large if n >= 48 else bold
        images.append(source.resize((n, n), Image.LANCZOS))
    images[0].save(os.path.join(OUT, "icon.png"))
    images[0].save(os.path.join(OUT, "icon.ico"), sizes=[(n, n) for n in SIZES], append_images=images[1:])
    print("wrote", os.path.join(OUT, "icon.png"), "and icon.ico", SIZES)


if __name__ == "__main__":
    main()

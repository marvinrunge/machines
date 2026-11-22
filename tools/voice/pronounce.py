"""How names are said (pronounce.json): whole-word replacements for the text a speech engine gets.

    spoken("Begrenzer raus, L-7! Lauf!", "de") -> "Begrenzer raus, Ell sieben! Lauf!"

Used by google_tts.py and placeholders.py, so every German voice says Ike as "Eik" and L-7 as "Ell sieben";
subtitles and shown text keep "Ike" and "L-7".
"""
import json
import os
import re

TABLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pronounce.json")
_rules = {}


def rules(lang):
    if lang not in _rules:
        with open(TABLE, encoding="utf-8") as f:
            table = json.load(f).get(lang, {})
        keys = sorted(table, key=len, reverse=True)
        _rules[lang] = (re.compile(r"(?<![\w-])(%s)(?![\w])" % "|".join(re.escape(k) for k in keys)), table) \
            if keys else None
    return _rules[lang]


def spoken(text, lang):
    """The text as the speech engine should get it in lang."""
    r = rules(lang)
    return r[0].sub(lambda m: r[1][m.group(1)], text) if r else text


if __name__ == "__main__":
    import sys
    sys.stdout.reconfigure(encoding="utf-8")
    for t, lang in [("Ike? Ike. Gefällt mir.", "de"), ("Begrenzer raus, L-7! Lauf!", "de"), ("L-7 … L-7 … nimm an.", "de"),
                    ("Ikes Plan, L-7s Kern.", "de"), ("Mike, Spike, Ikea, XL-70", "de"), ("Limiter's off, L-7! Run!", "en"),
                    ("Ike? Ike. I like it.", "en"), ("L-7's core", "en")]:
        print("%s | %s -> %s" % (lang, t, spoken(t, lang)))

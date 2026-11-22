"""The game's own line table: game/assets/story/prologue.lines.json, made from lines.json.

    python tools/voice/runtime_lines.py [--check]

Per line: its id, its speaker (ship, auto, ike, bob, whisper, hud, l7), the channel it comes
through (pa: the ship's loudspeakers, radio: a Grunt over the radio, thought: the whisper, the
HUD's own lines and the Locator's own thoughts, which only the Locator hears or sees) and its English text. The German
is the translation of that text in game/lang/de.po (the subtitles translate as every string
does). --check only tells whether the table is up to date (exit code 1 if not).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(HERE, "lines.json")
TARGET = os.path.join(ROOT, "game", "assets", "story", "prologue.lines.json")

CHANNELS = {"ship": "pa", "auto": "pa", "ike": "radio", "bob": "radio", "whisper": "thought", "hud": "thought", "l7": "thought"}


def build():
    with open(SOURCE, encoding="utf-8") as f:
        source = json.load(f)
    lines = []
    for line in source["lines"]:
        speaker = line["speaker"]
        if speaker not in CHANNELS:
            sys.exit(f"{line['id']}: unknown speaker {speaker}")
        lines.append({"id": line["id"], "speaker": speaker, "channel": CHANNELS[speaker], "en": line["en"]})
    table = {
        "about": "The prologue's lines for the game (made by tools/voice/runtime_lines.py from tools/voice/lines.json; do not edit). "
                 "channel: pa (the ship's loudspeakers), radio (a Grunt over the radio), thought (the whisper and the HUD, the Locator's own). "
                 "The German is the translation of 'en' in game/lang/de.po.",
        "lines": lines,
    }
    return json.dumps(table, ensure_ascii=False, indent=1) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = build()
    old = open(TARGET, encoding="utf-8").read() if os.path.exists(TARGET) else None
    if args.check:
        print("up to date" if old == text else "out of date")
        sys.exit(0 if old == text else 1)
    if old != text:
        with open(TARGET, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    print(f"{TARGET}: {text.count(chr(10) + '  {')} lines")


if __name__ == "__main__":
    main()

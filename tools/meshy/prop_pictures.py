"""Makes the Gemini input picture of each prop in a Meshy plan (tools/meshy/props-prologue.json), one object on white,
through the story board server's Gemini route (tools/storyboard/server.py: GEMINI_API_KEY or gcloud's Vertex sign-in).

    python tools/meshy/prop_pictures.py [--plan FILE] [--out DIR] [--again] [name ...]

Each picture goes to <out>/<name>/input-raw.png (default out: renders/meshy/props) with the prompt and Gemini's answer in
picture.json beside it. A prop that has a picture is skipped unless --again is given; then the old picture is kept as
input-raw-v<n>.png. Whitening the backdrop is a separate step (tools/meshy/whiten.py, run in Blender), which writes
input.png and never touches the raw picture."""
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "storyboard"))
import server  # noqa: E402

KIND_VIEW = {
    "kit": "",
    "set": "",
    "exterior": "Show the whole ship complete, like a studio model photographed on white, not in space.",
}


def prompt_for(plan, prop):
    w, d, h = prop["size_m"]
    parts = [
        "The attached pictures are concept art of the spaceship where this object stands. Use them only for the mood, "
        "the materials, the red paint and the wear. Do not copy any robot, machine, vehicle, light, haze or background "
        "from them.",
        f"Make a clean 3D concept render of one single prop for a video game: {prop['description']}",
        f"Real size: about {w:g} m wide, {d:g} m deep and {h:g} m tall. Keep these proportions exactly: the height is "
        f"{h / w:.2g} times the width and the depth is {d / w:.2g} times the width.",
    ]
    if prop["kind"] == "kit":
        parts.append(plan["kit_rule"])
    if KIND_VIEW.get(prop["kind"]):
        parts.append(KIND_VIEW[prop["kind"]])
    style = plan["style"]
    if prop["kind"] == "kit" and plan.get("kit_style_swap"):
        # Hoses arching over a floor or across a tile's edge would break the grid; kit tiles keep them inside.
        style = style.replace(*plan["kit_style_swap"])
    parts += [
        plan["geometry"],
        style,
        "No robots, no machines, no vehicles, no people, no weapons unless the description asks for them. The object "
        "is complete and not cropped, centred with a wide white margin on every side, on a pure white (#FFFFFF) "
        "seamless background with no floor, no horizon, no scenery and at most a faint soft contact shadow. No text, "
        "no labels, no logos, no watermark.",
    ]
    return "\n\n".join(parts)


def main(argv):
    plan_path = REPO / "tools" / "meshy" / "props-prologue.json"
    out = REPO / "renders" / "meshy" / "props"
    again, names = False, []
    args = iter(argv)
    for arg in args:
        if arg == "--plan":
            plan_path = Path(next(args))
        elif arg == "--out":
            out = Path(next(args))
        elif arg == "--again":
            again = True
        else:
            names.append(arg)
    plan = json.loads(plan_path.read_text("utf-8"))
    props = [p for p in plan["props"] if not names or p["name"] in names]
    unknown = set(names) - {p["name"] for p in props}
    if unknown:
        sys.exit(f"not in the plan: {', '.join(sorted(unknown))}")
    g = plan["gemini"]
    calls = 0
    for prop in props:
        folder = out / prop["name"]
        raw = folder / "input-raw.png"
        if raw.is_file() and not again:
            print(f"{prop['name']}: has a picture, skipped", flush=True)
            continue
        folder.mkdir(parents=True, exist_ok=True)
        prompt = prompt_for(plan, prop)
        # server.generate saves into server.IMAGES; point it at the prop's own folder so nothing lands in the board's
        # pictures. References are still read from the board (picture_file looks them up by address).
        board_images, board_lookup = server.IMAGES, server.picture_file
        resolved = {r: board_lookup(r) for r in prop.get("refs", [])}
        refs_found = [r for r, path in resolved.items() if path]
        server.IMAGES, server.picture_file = folder, resolved.get
        started = time.time()
        calls += 1
        try:
            saved, text = server.generate(prompt, g["model"], g["aspect"], g.get("size"), refs_found)
        except RuntimeError as error:
            print(f"{prop['name']}: ERROR {error}", flush=True)
            continue
        finally:
            server.IMAGES, server.picture_file = board_images, board_lookup
        if raw.is_file():
            n = 1
            while (folder / f"input-raw-v{n}.png").is_file():
                n += 1
            raw.rename(folder / f"input-raw-v{n}.png")
        # Gemini 3 Pro sends its 1K thinking drafts before the finished picture: the last one is the answer.
        final = folder / saved[-1]
        if final.suffix.lower() != ".png":
            raw = folder / f"input-raw{final.suffix.lower()}"
        final.replace(raw)
        for draft in saved[:-1]:
            (folder / draft).unlink()
        log = folder / "picture.json"
        history = json.loads(log.read_text("utf-8")).get("history", []) if log.is_file() else []
        record = {"id": prop["id"], "name": prop["name"], "model": g["model"], "aspect": g["aspect"],
                  "size": g.get("size"), "refs": refs_found, "prompt": prompt, "text": text, "file": raw.name,
                  "at": datetime.now(timezone.utc).isoformat()}
        log.write_text(json.dumps({**record, "history": history + [record]}, ensure_ascii=False, indent=2) + "\n",
                       "utf-8")
        print(f"{prop['name']}: {raw.name} in {time.time() - started:.0f} s", flush=True)
    print(f"gemini calls: {calls}", flush=True)


if __name__ == "__main__":
    main(sys.argv[1:])

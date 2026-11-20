"""Turns the Gemini input picture of each prop in a Meshy plan into a textured mesh with Meshy Image-to-3D.

    python tools/meshy/meshy_gen.py [--plan FILE] [--out DIR] [--max-credits N] [--dry] [name ...]

For every prop of the plan (default tools/meshy/props-prologue.json), or only the named ones, it sends
<out>/<name>/input.png (default out: renders/meshy/props) as a data URI with the plan's "meshy" settings and the prop's
target_polycount, keeps up to five tasks in flight, and downloads model.glb and thumbnail.png beside the picture, with
Meshy's task answer in task.json. A prop that has a model.glb is skipped, so a run can be repeated after a failure.

Every task and its credits go to <out>/meshy-log.json (the AI model and the polycount are written down per run, as the
user asks). --max-credits stops sending new tasks once the run would spend more than that (default 560); failed tasks
are refunded by Meshy and are not counted. The key comes from the MESHY_API_KEY environment variable, or from the
user's registry environment on Windows, and is never printed or written anywhere."""
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
API = "https://api.meshy.ai/openapi/v1"
IN_FLIGHT = 5
POLL_SECONDS = 15
CREDITS_PER_RUN = 15  # T2.5 mesh 5 + 2K PBR texture 10
# Settings Meshy refuses with Smart Topology (remove_lighting) or ignores without auto_size (origin_at): never sent.
NOT_SENT = {"remove_lighting", "origin_at"}


def api_key():
    key = os.environ.get("MESHY_API_KEY")
    if not key and sys.platform == "win32":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as env:
                key = winreg.QueryValueEx(env, "MESHY_API_KEY")[0]
        except OSError:
            key = None
    if not key:
        sys.exit("MESHY_API_KEY is not set")
    return key


def call(method, path, body=None, key=None, timeout=120):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(API + path, data=data, method=method,
                                     headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=timeout) as answer:
                return json.loads(answer.read())
        except urllib.error.HTTPError as error:
            text = error.read().decode("utf-8", "replace")[:400]
            if error.code in (429, 500, 502, 503, 504) and attempt < 3:
                time.sleep(10 * (attempt + 1))
                continue
            raise RuntimeError(f"{method} {path}: HTTP {error.code} {text}") from None
        except (urllib.error.URLError, TimeoutError) as error:
            if attempt < 3:
                time.sleep(10 * (attempt + 1))
                continue
            raise RuntimeError(f"{method} {path}: {error}") from None


def download(url, target):
    with urllib.request.urlopen(url, timeout=300) as answer:
        target.write_bytes(answer.read())


def data_uri(path):
    mime = "image/png" if path.suffix.lower() == ".png" else "image/jpeg"
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode()


def now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def main(argv):
    plan_path = REPO / "tools" / "meshy" / "props-prologue.json"
    out = REPO / "renders" / "meshy" / "props"
    max_credits, dry, names = 560, False, []
    args = iter(argv)
    for arg in args:
        if arg == "--plan":
            plan_path = Path(next(args))
        elif arg == "--out":
            out = Path(next(args))
        elif arg == "--max-credits":
            max_credits = int(next(args))
        elif arg == "--dry":
            dry = True
        else:
            names.append(arg)
    plan = json.loads(plan_path.read_text("utf-8"))
    props = [p for p in plan["props"] if not names or p["name"] in names]
    unknown = set(names) - {p["name"] for p in props}
    if unknown:
        sys.exit(f"not in the plan: {', '.join(sorted(unknown))}")
    settings = {k: v for k, v in plan["meshy"].items() if k not in NOT_SENT}
    todo = []
    for prop in props:
        folder = out / prop["name"]
        if (folder / "model.glb").is_file():
            print(f"{prop['name']}: has a model.glb, skipped", flush=True)
        elif not (folder / "input.png").is_file():
            print(f"{prop['name']}: no input.png, skipped", flush=True)
        else:
            todo.append(prop)
    print(f"{len(todo)} to make with {settings.get('ai_model')} ({settings.get('model_type')}), "
          f"about {len(todo) * CREDITS_PER_RUN} credits: " +
          ", ".join(f"{p['name']} {p['target_polycount']}" for p in todo), flush=True)
    if dry or not todo:
        return
    key = api_key()
    log_path = out / "meshy-log.json"
    log = json.loads(log_path.read_text("utf-8")) if log_path.is_file() else {"runs": [], "tasks": []}
    balance = call("GET", "/balance", key=key).get("balance")
    run = {"started": now(), "balance_before": balance, "ai_model": settings.get("ai_model"),
           "model_type": settings.get("model_type"), "settings": settings, "max_credits": max_credits}
    log["runs"].append(run)
    print(f"balance {balance}", flush=True)

    def save():
        log_path.write_text(json.dumps(log, ensure_ascii=False, indent=2) + "\n", "utf-8")

    spent = 0  # credits of tasks sent this run, counted up front; failed ones are given back below
    queue, running = list(todo), {}
    while queue or running:
        while queue and len(running) < IN_FLIGHT:
            if spent + CREDITS_PER_RUN > max_credits or (balance is not None and balance - spent < CREDITS_PER_RUN):
                print(f"credit limit reached, {len(queue)} not sent: {', '.join(p['name'] for p in queue)}", flush=True)
                queue.clear()
                break
            prop = queue.pop(0)
            folder = out / prop["name"]
            body = {**settings, "image_url": data_uri(folder / "input.png"),
                    "target_polycount": prop["target_polycount"], "target_formats": ["glb"]}
            try:
                task_id = call("POST", "/image-to-3d", body, key=key)["result"]
            except RuntimeError as error:
                print(f"{prop['name']}: not sent: {error}", flush=True)
                continue
            spent += CREDITS_PER_RUN
            entry = {"name": prop["name"], "task_id": task_id, "ai_model": settings.get("ai_model"),
                     "model_type": settings.get("model_type"), "target_polycount": prop["target_polycount"],
                     "texture_resolution": settings.get("texture_resolution"), "sent": now(), "status": "PENDING"}
            log["tasks"].append(entry)
            running[task_id] = (prop, entry, time.time())
            save()
            print(f"{prop['name']}: sent {task_id}", flush=True)
        if not running:
            break
        time.sleep(POLL_SECONDS)
        for task_id in list(running):
            prop, entry, started = running[task_id]
            try:
                task = call("GET", f"/image-to-3d/{task_id}", key=key)
            except RuntimeError as error:
                print(f"{prop['name']}: poll failed ({error}), trying again", flush=True)
                continue
            status = task.get("status")
            if status in ("PENDING", "IN_PROGRESS"):
                continue
            del running[task_id]
            folder = out / prop["name"]
            entry.update(status=status, finished=now(), credits=task.get("consumed_credits"),
                         seconds=round(time.time() - started))
            if status == "SUCCEEDED":
                try:
                    download(task["model_urls"]["glb"], folder / "model.glb")
                    if task.get("thumbnail_url"):
                        download(task["thumbnail_url"], folder / "thumbnail.png")
                except (OSError, KeyError, urllib.error.URLError) as error:
                    entry["download_error"] = str(error)
                    print(f"{prop['name']}: download failed: {error}", flush=True)
                (folder / "task.json").write_text(json.dumps(task, indent=2) + "\n", "utf-8")
                print(f"{prop['name']}: done, {task.get('consumed_credits')} credits, "
                      f"{entry['seconds']} s", flush=True)
            else:
                spent -= CREDITS_PER_RUN  # refunded
                entry["error"] = (task.get("task_error") or {}).get("message")
                print(f"{prop['name']}: {status} {entry['error']}", flush=True)
            save()
    balance_after = call("GET", "/balance", key=key).get("balance")
    run.update(finished=now(), balance_after=balance_after,
               spent=None if balance is None or balance_after is None else balance - balance_after)
    save()
    print(f"balance {balance_after}, this run spent {run['spent']}", flush=True)


if __name__ == "__main__":
    main(sys.argv[1:])

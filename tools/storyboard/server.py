"""The story board: a small local page to plan the story mode. Acts and scenes, the assets they need, and pictures of
both made with Gemini's image models, kept in the repository (docs/story/board.json and docs/story/images/) so the
story can be prepared step by step and later built from it. The story text itself stays in docs/STORY.md; the page
shows it.

    python tools/storyboard/server.py            # http://127.0.0.1:8766

The Gemini key comes from the environment (GEMINI_API_KEY, or GOOGLE_API_KEY) and never reaches the page or the
repository: the page asks this server, the server asks Gemini. Without a key everything but making pictures works.
The server listens on this computer only."""
import base64
import json
import mimetypes
import os
import re
import sys
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
STORY = REPO / "docs" / "story"
BOARD = STORY / "board.json"
IMAGES = STORY / "images"
STORY_TEXT = REPO / "docs" / "STORY.md"
# Pictures the page may hand Gemini as references besides its own: the website's screenshots of the game.
SHOTS = REPO / "site" / "shots"
PAGE_FILES = {"/": "index.html", "/index.html": "index.html", "/app.js": "app.js", "/style.css": "style.css"}
PAGE_TYPES = {".html": "text/html", ".js": "text/javascript", ".css": "text/css"}

API = "https://generativelanguage.googleapis.com/v1beta"
DEFAULT_MODEL = "gemini-3.1-flash-image"
PORT = int(os.environ.get("STORYBOARD_PORT", "8766"))
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
PICTURE = (".png", ".jpg", ".jpeg", ".webp")

EMPTY_BOARD = {"version": 1, "style": {"prompt": "", "refs": []}, "acts": [], "scenes": [], "assets": []}


def key():
    return os.environ.get("GEMINI_API_KEY") or os.environ.get("GOOGLE_API_KEY")


def picture_file(url):
    """The file behind a picture's address on this page (/images/<name> or /refs/<name>), or None."""
    for prefix, folder in (("/images/", IMAGES), ("/refs/", SHOTS)):
        if url.startswith(prefix):
            name = url[len(prefix):]
            if SAFE_NAME.match(name) and name.lower().endswith(PICTURE):
                path = folder / name
                return path if path.is_file() else None
    return None


def gemini(path, body=None, timeout=60):
    request = urllib.request.Request(f"{API}/{path}", method="POST" if body is not None else "GET",
                                     data=json.dumps(body).encode() if body is not None else None,
                                     headers={"x-goog-api-key": key() or "", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        text = error.read().decode("utf-8", "replace")
        try:
            message = json.loads(text)["error"]["message"]
        except (ValueError, KeyError, TypeError):
            message = text[:500]
        raise RuntimeError(f"Gemini {error.code}: {message}") from None


def image_models():
    """The Gemini models that make pictures, newest names first; the default alone without a key."""
    if not key():
        return [DEFAULT_MODEL]
    found = gemini("models?pageSize=200").get("models", [])
    names = [m["name"].removeprefix("models/") for m in found
             if "generateContent" in m.get("supportedGenerationMethods", []) and "image" in m["name"]]
    return sorted(names, reverse=True) or [DEFAULT_MODEL]


def generate(prompt, model, aspect, size, refs):
    """Asks Gemini for a picture of the prompt, with the reference pictures before it; saves what comes back in
    docs/story/images and returns the saved names and any text the model added."""
    if not key():
        raise RuntimeError("Kein Gemini-Schlüssel: GEMINI_API_KEY setzen und den Server neu starten.")
    if not SAFE_NAME.match(model):
        raise RuntimeError("Unbekanntes Modell.")
    parts = []
    for url in refs:
        path = picture_file(url)
        if path is None:
            continue
        mime = mimetypes.guess_type(path.name)[0] or "image/png"
        parts.append({"inlineData": {"mimeType": mime, "data": base64.b64encode(path.read_bytes()).decode()}})
    parts.append({"text": prompt})
    image_config = {}
    if aspect:
        image_config["aspectRatio"] = aspect
    if size:
        image_config["imageSize"] = size
    config = {"responseModalities": ["TEXT", "IMAGE"]}
    if image_config:
        config["imageConfig"] = image_config
    answer = gemini(f"models/{model}:generateContent",
                    {"contents": [{"role": "user", "parts": parts}], "generationConfig": config}, timeout=240)
    IMAGES.mkdir(parents=True, exist_ok=True)
    saved, text = [], []
    stamp = time.strftime("%Y%m%d-%H%M%S")
    for candidate in answer.get("candidates", []):
        for part in candidate.get("content", {}).get("parts", []):
            data = part.get("inlineData") or part.get("inline_data")
            if data and data.get("data"):
                extension = mimetypes.guess_extension(data.get("mimeType", "image/png")) or ".png"
                extension = ".jpg" if extension == ".jpe" else extension
                name = f"{stamp}-{len(saved) + 1}{extension}"
                (IMAGES / name).write_bytes(base64.b64decode(data["data"]))
                saved.append(name)
            elif part.get("text") and not part.get("thought"):
                text.append(part["text"])
    if not saved:
        reason = (answer.get("promptFeedback") or {}).get("blockReason") or \
            next((c.get("finishReason") for c in answer.get("candidates", [])), None)
        raise RuntimeError("Gemini hat kein Bild geliefert" + (f" ({reason})" if reason else "") +
                           (": " + " ".join(text) if text else "."))
    return saved, " ".join(text).strip()


class Handler(BaseHTTPRequestHandler):
    server_version = "StoryBoard/1"

    def log_message(self, format, *args):
        sys.stderr.write("%s %s\n" % (self.command, self.path))

    def send(self, status, body, content_type="application/json; charset=utf-8"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        length = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(length) or b"null")

    def do_GET(self):
        path = self.path.split("?")[0]
        if path in PAGE_FILES:
            file = HERE / PAGE_FILES[path]
            return self.send(200, file.read_bytes(), PAGE_TYPES[file.suffix] + "; charset=utf-8")
        if path.startswith(("/images/", "/refs/")):
            file = picture_file(path)
            if file is None:
                return self.send(404, {"error": "Kein solches Bild."})
            return self.send(200, file.read_bytes(), mimetypes.guess_type(file.name)[0] or "application/octet-stream")
        if path == "/api/board":
            board = json.loads(BOARD.read_text("utf-8")) if BOARD.is_file() else EMPTY_BOARD
            return self.send(200, board)
        if path == "/api/story":
            return self.send(200, STORY_TEXT.read_text("utf-8") if STORY_TEXT.is_file() else "",
                             "text/markdown; charset=utf-8")
        if path == "/api/refs":
            shots = sorted(p.name for p in SHOTS.glob("*") if p.suffix.lower() in PICTURE) if SHOTS.is_dir() else []
            return self.send(200, [{"url": f"/refs/{name}", "label": f"Spiel: {name}"} for name in shots])
        if path == "/api/models":
            try:
                return self.send(200, {"models": image_models(), "default": DEFAULT_MODEL, "key": bool(key())})
            except (RuntimeError, OSError) as error:
                return self.send(200, {"models": [DEFAULT_MODEL], "default": DEFAULT_MODEL, "key": bool(key()),
                                       "error": str(error)})
        self.send(404, {"error": "Nicht gefunden."})

    def do_PUT(self):
        if self.path != "/api/board":
            return self.send(404, {"error": "Nicht gefunden."})
        try:
            board = self.body()
            if not isinstance(board, dict) or not isinstance(board.get("scenes"), list):
                raise ValueError("Kein Story-Board.")
        except ValueError as error:
            return self.send(400, {"error": str(error)})
        STORY.mkdir(parents=True, exist_ok=True)
        temporary = BOARD.with_suffix(".tmp")
        temporary.write_text(json.dumps(board, ensure_ascii=False, indent=2) + "\n", "utf-8")
        temporary.replace(BOARD)
        self.send(200, {"ok": True})

    def do_POST(self):
        if self.path != "/api/generate":
            return self.send(404, {"error": "Nicht gefunden."})
        try:
            request = self.body() or {}
            prompt = str(request.get("prompt") or "").strip()
            if not prompt:
                raise RuntimeError("Der Prompt ist leer.")
            saved, text = generate(prompt, str(request.get("model") or DEFAULT_MODEL), request.get("aspect"),
                                   request.get("size"), [str(r) for r in request.get("refs") or []][:8])
            self.send(200, {"images": saved, "text": text})
        except (RuntimeError, ValueError, OSError) as error:
            self.send(502, {"error": str(error)})

    def do_DELETE(self):
        if not self.path.startswith("/images/"):
            return self.send(404, {"error": "Nicht gefunden."})
        file = picture_file(self.path)
        if file is not None:
            file.unlink()
        self.send(200, {"ok": True})


if __name__ == "__main__":
    print(f"Story-Board auf http://127.0.0.1:{PORT}" + ("" if key() else " (ohne Gemini-Schlüssel)"), flush=True)
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()

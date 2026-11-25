"""Puts the story book into the user's Google Drive as a new version and keeps the old ones.

    python tools/story-book/publish.py --note "Was sich geändert hat"
    python tools/story-book/publish.py --version 0.1 --note "Erste Fassung"

Each run makes a new folder "Version X.Y" under DRIVE (Google Drive for desktop, drive G:) with:

    Machines-Story.epub   the book as an e-book (Google Play Books opens it straight from Drive)
    Machines-Story.md     the book in one file
    Kapitel/              the chapter files and the README
    Hörbuch/              the MP3s from tools/story-book/voice.py, the playlist and the complete file
    Änderungen.txt        version, date, commit and the note

and adds a line to Versionen.txt next to the version folders. Without --version the next number is the highest
existing one plus 0.1. An existing version folder is never overwritten. Run build_book.py and voice.py first; the
script stops if a chapter is newer than its MP3 (--skip-audio-check to publish anyway).
"""
import argparse, datetime, html, os, re, shutil, subprocess, sys, uuid, zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BOOK = os.path.join(ROOT, "docs", "story", "buch")
AUDIO = os.path.join(ROOT, "renders", "story-book", "audio")
DRIVE = r"G:\Meine Ablage\Machines Remake\Buch"
TITLE = "Machines – Keiner trägt alles"

CSS = """body { font-family: serif; line-height: 1.5; margin: 0 5%; }
h1 { font-size: 1.6em; text-align: center; margin: 2em 0 1.5em; }
p { margin: 0; text-indent: 1.2em; text-align: justify; }
h1 + p, p.first { text-indent: 0; }
p.break { text-align: center; text-indent: 0; margin: 1em 0; }
.title { text-align: center; margin-top: 30%; }
.title h1 { font-size: 2em; margin-bottom: 0.5em; }
.title p { text-indent: 0; text-align: center; }
"""


def chapters():
    return sorted(f for f in os.listdir(BOOK) if re.match(r"\d\d-.*\.md$", f))


def inline(text):
    text = html.escape(text, quote=False)
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    return re.sub(r"\*(.+?)\*", r"<em>\1</em>", text)


def xhtml(title, body):
    return ('<?xml version="1.0" encoding="utf-8"?>\n<!DOCTYPE html>\n'
            '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="de" xml:lang="de">\n'
            f'<head><meta charset="utf-8"/><title>{html.escape(title)}</title>'
            '<link rel="stylesheet" type="text/css" href="style.css"/></head>\n'
            f'<body>\n{body}\n</body>\n</html>\n')


def chapter_xhtml(md):
    title, out, after_break = None, [], False
    for block in re.split(r"\n\s*\n", md.strip()):
        block = " ".join(line.strip() for line in block.splitlines())
        if block.startswith("# "):
            title = block[2:].strip()
            out.append(f"<h1>{inline(title)}</h1>")
        elif block.startswith("#"):
            out.append(f"<h2>{inline(block.lstrip('#').strip())}</h2>")
        elif re.fullmatch(r"(\* ?){3}|-{3,}", block):
            out.append('<p class="break">* * *</p>')
            after_break = True
            continue
        else:
            cls = ' class="first"' if after_break else ""
            out.append(f"<p{cls}>{inline(block)}</p>")
        after_break = False
    return title, "\n".join(out)


def build_epub(path, version):
    book_id = "urn:uuid:" + str(uuid.uuid5(uuid.NAMESPACE_URL, "machines-story-" + version))
    items, spine, nav = [], [], []
    files = {"title.xhtml": xhtml(TITLE, f'<div class="title"><h1>{html.escape(TITLE)}</h1>'
                                         f'<p>Die Geschichte des Story-Modus im Machines-Remake</p>'
                                         f'<p>Version {version}</p></div>')}
    for i, f in enumerate(chapters()):
        title, body = chapter_xhtml(open(os.path.join(BOOK, f), encoding="utf-8").read())
        name = f"kapitel{i:02d}.xhtml"
        files[name] = xhtml(title, body)
        nav.append(f'<li><a href="{name}">{html.escape(title)}</a></li>')
    files["nav.xhtml"] = xhtml("Inhalt", '<nav epub:type="toc" id="toc"><h1>Inhalt</h1><ol>\n'
                               + "\n".join(nav) + "\n</ol></nav>")
    for name in files:
        props = ' properties="nav"' if name == "nav.xhtml" else ""
        iid = name.split(".")[0]
        items.append(f'<item id="{iid}" href="{name}" media-type="application/xhtml+xml"{props}/>')
        if name != "nav.xhtml":
            spine.append(f'<itemref idref="{iid}"/>')
    spine.insert(1, '<itemref idref="nav"/>')
    modified = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    opf = ('<?xml version="1.0" encoding="utf-8"?>\n'
           '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="de">\n'
           '<metadata xmlns:dc="http://purl.org/dc/elements/1.1/">\n'
           f'<dc:identifier id="bookid">{book_id}</dc:identifier>\n'
           f'<dc:title>{html.escape(TITLE)} (Version {version})</dc:title>\n'
           '<dc:language>de</dc:language>\n'
           f'<meta property="dcterms:modified">{modified}</meta>\n'
           '</metadata>\n<manifest>\n<item id="css" href="style.css" media-type="text/css"/>\n'
           + "\n".join(items) + '\n</manifest>\n<spine>\n' + "\n".join(spine) + '\n</spine>\n</package>\n')
    container = ('<?xml version="1.0" encoding="utf-8"?>\n'
                 '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">\n'
                 '<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>'
                 '</rootfiles>\n</container>\n')
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(zipfile.ZipInfo("mimetype"), "application/epub+zip", compress_type=zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml", container, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/content.opf", opf, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr("OEBPS/style.css", CSS, compress_type=zipfile.ZIP_DEFLATED)
        for name, text in files.items():
            z.writestr("OEBPS/" + name, text, compress_type=zipfile.ZIP_DEFLATED)


def versions():
    found = []
    for d in os.listdir(DRIVE) if os.path.isdir(DRIVE) else []:
        m = re.fullmatch(r"Version (\d+)\.(\d+)", d)
        if m:
            found.append((int(m.group(1)), int(m.group(2))))
    return sorted(found)


def stale_audio():
    stale = []
    for f in chapters():
        mp3 = os.path.join(AUDIO, f[:-3] + ".mp3")
        if not os.path.exists(mp3) or os.path.getmtime(mp3) < os.path.getmtime(os.path.join(BOOK, f)):
            stale.append(f)
    return stale


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", help="version number, e.g. 0.1 (default: highest + 0.1)")
    ap.add_argument("--note", default="", help="what changed, written to Änderungen.txt and Versionen.txt")
    ap.add_argument("--skip-audio-check", action="store_true")
    a = ap.parse_args()

    if not os.path.isdir(os.path.dirname(DRIVE)):
        sys.exit(f"Google Drive folder not found: {os.path.dirname(DRIVE)}")
    if a.version:
        version = a.version
    else:
        have = versions()
        version = f"{have[-1][0]}.{have[-1][1] + 1}" if have else "0.1"
    target = os.path.join(DRIVE, f"Version {version}")
    if os.path.exists(target):
        sys.exit(f"{target} already exists; old versions are never overwritten")
    stale = stale_audio()
    if stale and not a.skip_audio_check:
        sys.exit("audio older than the text, run voice.py first: " + ", ".join(stale))

    commit = subprocess.run(["git", "-C", ROOT, "log", "-1", "--format=%h %s"], capture_output=True,
                            text=True, encoding="utf-8").stdout.strip()
    dirty = subprocess.run(["git", "-C", ROOT, "status", "--porcelain", "--", "docs/story/buch"],
                           capture_output=True, text=True).stdout.strip()

    tmp = target + ".unfertig"
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(os.path.join(tmp, "Kapitel"))
    os.makedirs(os.path.join(tmp, "Hörbuch"))
    build_epub(os.path.join(tmp, "Machines-Story.epub"), version)
    shutil.copy2(os.path.join(BOOK, "Machines-Story.md"), tmp)
    for f in chapters() + ["README.md"]:
        shutil.copy2(os.path.join(BOOK, f), os.path.join(tmp, "Kapitel"))
    for f in sorted(os.listdir(AUDIO)):
        if f.endswith((".mp3", ".m3u")):
            shutil.copy2(os.path.join(AUDIO, f), os.path.join(tmp, "Hörbuch"))
    today = datetime.date.today().isoformat()
    with open(os.path.join(tmp, "Änderungen.txt"), "w", encoding="utf-8") as f:
        f.write(f"{TITLE}\nVersion {version}, {today}\n\nStand im Repository: {commit}"
                + (" (mit ungespeicherten Änderungen)" if dirty else "") + "\n\n" + (a.note or "-") + "\n")
    os.rename(tmp, target)
    with open(os.path.join(DRIVE, "Versionen.txt"), "a", encoding="utf-8") as f:
        f.write(f"Version {version} ({today}, {commit.split(' ')[0]}): {a.note or '-'}\n")
    print(f"published {target}")


if __name__ == "__main__":
    main()

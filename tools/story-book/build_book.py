"""Joins the chapters in docs/story/buch/NN-*.md into docs/story/buch/Machines-Story.md.

    python tools/story-book/build_book.py
"""
import os, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BOOK = os.path.join(ROOT, "docs", "story", "buch")

HEAD = """# Machines – Keiner trägt alles

*Die Geschichte des Story-Modus im Machines-Remake.*

"""


def main():
    files = sorted(f for f in os.listdir(BOOK) if re.match(r"\d\d-.*\.md$", f))
    toc, parts = ["## Inhalt", ""], []
    others = False
    for f in files:
        md = open(os.path.join(BOOK, f), encoding="utf-8").read().strip()
        title = re.search(r"^#\s+(.+)$", md, re.M).group(1).strip()
        if title.startswith("Andere Enden") and not others:
            others = True
            toc.append("- **Andere Enden**")
        anchor = re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-")
        toc.append(f"{'  ' if others else ''}- [{title}](#{anchor})")
        parts.append(re.sub(r"^# ", "## ", md, count=1, flags=re.M))  # chapter titles one level down
    text = HEAD + "\n".join(toc) + "\n\n---\n\n" + "\n\n---\n\n".join(parts) + "\n"
    out = os.path.join(BOOK, "Machines-Story.md")
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    words = len(re.findall(r"\w+", text))
    print(f"{out}: {len(files)} chapters, {words} words, {len(text)} characters")


if __name__ == "__main__":
    main()

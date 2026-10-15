"""Check every relative Markdown link in the repository resolves to an existing file.

Usage: python tools/check-docs.py [--root PATH]
Exit code 0 when no link is broken, 1 otherwise. Generated and vendored trees are skipped.
"""
import argparse
import pathlib
import re
import sys

SKIP_PARTS = {"artifacts", "Library", "Temp", "obj", "bin", "node_modules", "competitors",
              "SourceGenerator~", "SourceGenerators~", ".git"}
LINK = re.compile(r"\]\(([^)\s]+?)(?:#[^)]*)?\)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=pathlib.Path(__file__).resolve().parents[1])
    root = pathlib.Path(parser.parse_args().root).resolve()
    files = [p for p in root.rglob("*.md") if not (set(p.parts) & SKIP_PARTS)]
    broken, evidence = [], []
    for md in files:
        text = md.read_text(encoding="utf-8", errors="replace")
        for match in LINK.finditer(text):
            target = match.group(1)
            if re.match(r"^[a-z]+:", target) or target.startswith("#"):
                continue  # URL, mailto, or in-page anchor
            resolved = (md.parent / target.replace("%20", " ")).resolve()
            if resolved.exists():
                continue
            if "/artifacts/" in target or target.startswith("artifacts/"):
                evidence.append((md.relative_to(root), target))  # git-ignored local result files
            else:
                broken.append((md.relative_to(root), target))
    print(f"markdown files: {len(files)}; broken links: {len(broken)}; links to local evidence not in git: {len(evidence)}")
    for source, target in broken:
        print(f"  BROKEN {source} -> {target}")
    return 1 if broken else 0


if __name__ == "__main__":
    sys.exit(main())

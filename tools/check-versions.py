"""Check the framework's dependency versions against the Dependencies table in docs/RELEASE_GATES.md (issue #138).

Usage: python tools/check-versions.py [--newest]
Offline: every declared minimum (package.json, asmdef versionDefines) agrees with the others and with the table;
the development project resolves what its manifest asks for; git dependencies are pinned to a commit; every
installed-editor path names the project's editor; the source generator references the editor's Roslyn; a row
whose proven version is not the newest gives its reason. --newest also reads each dependency's newest stable
release from its source and fails when the table's newest column differs. Exit code 0 when every check passes.
"""
import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
PACKAGES = ROOT / "Nexus" / "Packages"
GATES = ROOT / "docs" / "RELEASE_GATES.md"
GENERATOR = PACKAGES / "com.nexus.core" / "SourceGenerator~" / "Nexus.SourceGenerator.csproj"
EDITOR_ROOT = pathlib.Path(r"C:\Program Files\Unity\Hub\Editor")
VERSION = re.compile(r"\d+\.\d+(?:\.\d+)?(?:[abfp]\d+|-[0-9A-Za-z.]+)?")
EDITOR_PATH = re.compile(r"(?:Hub[\\/]+Editor[\\/]+|<Unity )(\d{4}\.\d+\.\d+[abf]\d+)")
GITHUB = {"com.google.ads.mobile": "googleads/googleads-mobile-unity", "com.google.firebase": "firebase/firebase-unity-sdk",
          "com.google.external-dependency-manager": "googlesamples/unity-jar-resolver",
          "com.coplaydev.unity-mcp": "CoplayDev/unity-mcp", "VContainer": "hadashiA/VContainer",
          "Reflex": "gustavopsantos/Reflex", "Zenject": "modesttree/Zenject"}


def key(package):
    return "com.google.firebase" if package.startswith("com.google.firebase.") else package


def first_version(text):
    match = VERSION.search(text)
    return match.group(0) if match else None


def order(version):
    numbers, _, pre = version.partition("-")
    return [int(n) for n in re.findall(r"\d+", numbers)], 0 if pre else 1, [int(n) for n in re.findall(r"\d+", pre)]


def table():
    text = GATES.read_text(encoding="utf-8")
    section = text.split("\n## Dependencies", 1)[1].split("\n## ", 1)[0]
    rows = {}
    for line in section.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        name = re.match(r"`([^`]+)`", cells[0]) if len(cells) == 5 else None
        if name:
            rows[name.group(1)] = dict(zip(("minimum", "proven", "newest", "decision"), cells[1:]))
    return rows


def declared(problems):
    found = {}
    for package_json in sorted(PACKAGES.glob("com.nexus.*/package.json")):
        package = package_json.parent
        data = json.loads(package_json.read_text(encoding="utf-8"))
        entries = [("unity", data["unity"], package.name + "/package.json")]
        entries += [(key(k), v, package.name + "/package.json") for k, v in data.get("dependencies", {}).items()
                    if not k.startswith(("com.nexus.", "com.unity.modules."))]
        for asmdef in sorted(package.rglob("*.asmdef")):
            for define in json.loads(asmdef.read_text(encoding="utf-8-sig")).get("versionDefines", []):
                if not define["name"].startswith("com.nexus."):
                    entries.append((key(define["name"]), define["expression"], asmdef.relative_to(PACKAGES).as_posix()))
        for name, version, where in entries:
            seen = found.setdefault(name, (version, where))
            if seen[0] != version:
                problems.append(f"{name}: {version} in {where}, {seen[0]} in {seen[1]}")
    generator = re.search(r'"Microsoft\.CodeAnalysis\.CSharp" Version="([^"]+)"', GENERATOR.read_text(encoding="utf-8"))
    found["Microsoft.CodeAnalysis.CSharp"] = (generator.group(1), "SourceGenerator~/Nexus.SourceGenerator.csproj")
    return {name: version for name, (version, _) in found.items()}


def offline(rows, problems):
    editor = re.search(r"m_EditorVersion: (\S+)", (ROOT / "Nexus/ProjectSettings/ProjectVersion.txt").read_text()).group(1)
    minimums = declared(problems)
    for name, version in minimums.items():
        if name not in rows:
            problems.append(f"{name}: declared {version}, no row in the Dependencies table")
        elif first_version(rows[name]["minimum"]) != version:
            problems.append(f"{name}: the table's minimum is {rows[name]['minimum']!r}, the packages declare {version}")
    manifest = json.loads((PACKAGES / "manifest.json").read_text(encoding="utf-8"))["dependencies"]
    lock = json.loads((PACKAGES / "packages-lock.json").read_text(encoding="utf-8"))["dependencies"]
    resolved = {"unity": editor}
    for name, wanted in manifest.items():
        entry = lock.get(name, {})
        if name.startswith(("com.unity.modules.", "com.nexus.")) or entry.get("source") in ("embedded", "local"):
            continue
        if wanted != entry.get("version"):  # a git entry's version is its URL, so a re-pin the lock has not taken shows here
            problems.append(f"{name}: the development project's manifest asks for {wanted}, packages-lock.json resolves "
                            f"{entry.get('version')} ({entry.get('source')})")
        if entry.get("source") == "git":
            if not re.search(r"#[0-9a-f]{40}$", wanted):
                problems.append(f"{name}: the development project takes {wanted}, not a 40-character commit")
            resolved[name] = (entry.get("hash") or "")[:7]
            continue
        if entry.get("source") == "registry":
            resolved[name] = entry["version"]
            if name not in rows:
                problems.append(f"{name}: in the development project, no row in the Dependencies table")
    for name, version in resolved.items():
        if name in rows and version and version not in rows[name]["proven"]:
            problems.append(f"{name}: the development project runs {version}, the table's proven is {rows[name]['proven']!r}")
    for name, row in rows.items():
        minimum, proven, newest = (first_version(row[c]) for c in ("minimum", "proven", "newest"))
        if minimum and proven and order(minimum) > order(proven):
            problems.append(f"{name}: the minimum {minimum} is above the proven {proven}")
        if newest and proven and newest != proven and not row["decision"]:
            problems.append(f"{name}: proven {proven}, newest {newest}, and no decision")
    files = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.split("\n")
    for path in files:
        if not path or path.startswith("docs/archive/") or path.endswith(("CHANGELOG.md", ".dll", ".png", ".unity", ".asset")):
            continue
        try:
            text = (ROOT / path).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for match in EDITOR_PATH.finditer(text):
            if match.group(1) != editor:
                problems.append(f"{path}: names the editor {match.group(1)}, the project is on {editor}")
    roslyn = EDITOR_ROOT / editor / "Editor/Data/DotNetSdk"
    compilers = list(roslyn.glob("sdk/*/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll")) if roslyn.exists() else []
    if not compilers:
        print(f"  not checked: the Roslyn of {editor} (editor not installed at {EDITOR_ROOT})")
    else:
        ps = f"[Reflection.AssemblyName]::GetAssemblyName('{compilers[0]}').Version.ToString(3)"
        shipped = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True).stdout.strip()
        if shipped != minimums["Microsoft.CodeAnalysis.CSharp"]:
            problems.append(f"Microsoft.CodeAnalysis.CSharp: the generator references {minimums['Microsoft.CodeAnalysis.CSharp']}, "
                            f"{editor} compiles with {shipped}")
    return editor


def fetch(url):
    headers = {"User-Agent": "nexus-check-versions"}
    if os.environ.get("GITHUB_TOKEN") and "api.github.com" in url:
        headers["Authorization"] = "Bearer " + os.environ["GITHUB_TOKEN"]
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        return json.load(response)


def newest_of(name, row, editor):
    if row["newest"].startswith("set by the editor"):
        return None
    if name == "unity":  # the newest patch of Unity's Supported stream (#139)
        api = "https://services.api.unity.com/unity/editor/release/v1/releases?limit=25&order=RELEASE_DATE_DESC&stream=SUPPORTED"
        return max((r["version"] for r in fetch(api)["results"]), key=order)
    if name == "dotnet":
        channel = re.search(r"net(\d+\.\d+)", row["minimum"]).group(1)
        return fetch(f"https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/{channel}/releases.json")["latest-sdk"]
    if name in GITHUB:
        releases = fetch(f"https://api.github.com/repos/{GITHUB[name]}/releases?per_page=30")
        tags = [first_version(r["tag_name"]) for r in releases if not r["prerelease"] and not r["draft"]]
        return max((t for t in tags if t), key=order)
    if name.startswith("com.unity."):
        data = fetch(f"https://packages.unity.com/{name}")
        minor = order(".".join(editor.split(".")[:2]))
        allow_pre = "-" in (first_version(row["proven"]) or "")
        usable = [v for v, info in data["versions"].items()
                  if (allow_pre or "-" not in v) and order(info.get("unity", "0.0")) <= minor]
        return max(usable, key=order)
    raise ValueError("no source for this row")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--newest", action="store_true", help="also read the newest stable releases (network)")
    newest = parser.parse_args().newest
    rows, problems = table(), []
    editor = offline(rows, problems)
    if newest:
        for name, row in rows.items():
            try:
                latest = newest_of(name, row, editor)
            except Exception as error:  # a source that cannot be read leaves its row unchecked, and says so
                problems.append(f"{name}: newest not read ({error})")
                continue
            listed = first_version(row["newest"])
            print(f"  {name:42} proven {first_version(row['proven']) or '-':14} newest {latest or 'set by the editor'}")
            if latest and latest != listed:
                problems.append(f"{name}: the table's newest is {listed}, the source says {latest}")
    print(f"dependency rows: {len(rows)}; problems: {len(problems)}" + ("" if newest else " (offline; --newest reads the sources)"))
    for problem in problems:
        print(f"  {problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

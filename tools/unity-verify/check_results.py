"""Fail-closed validation of Unity NUnit XML and Nexus Windows build evidence."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def checked_counts(node):
    counts = {}
    for key in ("total", "passed", "failed", "inconclusive", "skipped"):
        value = node.get(key, "")
        if not re.fullmatch(r"[0-9]+", value):
            raise ValueError(f"missing or invalid {node.tag} count: {key}")
        counts[key] = int(value)
    if (counts["failed"] or counts["inconclusive"]
            or counts["total"] != sum(counts[k] for k in counts if k != "total")):
        raise ValueError(f"incomplete or unsuccessful {node.tag} counts: {counts}")
    cases = Counter(case.get("result") for case in node.iter("test-case"))
    if cases != Counter(Passed=counts["passed"], Skipped=counts["skipped"]):
        raise ValueError(f"test-case results disagree with {node.tag} counts: {dict(cases)}")
    return counts


def check_tests(path):
    root = ET.parse(path).getroot()
    result = root.get("result")
    if root.tag != "test-run" or result not in ("Passed", "Skipped:Ignored"):
        raise ValueError("expected a Passed or mixed Skipped:Ignored test-run root")
    counts = checked_counts(root)
    if counts["passed"] == 0 or (result == "Skipped:Ignored" and counts["skipped"] == 0):
        raise ValueError(f"zero passed tests or non-mixed Skipped:Ignored result: {counts}")
    for suite in root.iter("test-suite"):
        if suite.get("result") not in ("Passed", "Skipped"):
            raise ValueError("unsuccessful or incomplete test suite")
        suite_counts = checked_counts(suite)
        if suite.get("result") == "Skipped" and suite_counts["skipped"] == 0:
            raise ValueError("Skipped suite contains no skipped tests")
    # UTF 1.8 combines the aggregate label in the root, but keeps it separate below.
    allowed_states = {("Passed", ""), ("Skipped", "Ignored")}
    if result == "Passed":
        allowed_states.update({("Skipped", ""), ("Skipped", "Explicit")})
    for node in root.iter():
        if node.tag in ("test-suite", "test-case"):
            if (node.get("result"), node.get("label", "")) not in allowed_states:
                raise ValueError("unknown or inconsistent nested test state")
    print(f"{path.name}: result={result} passed={counts['passed']} failed=0 "
          f"inconclusive=0 skipped={counts['skipped']} total={counts['total']}")


def check_build(project, log, target, backend, started_ns, commit, report):
    marker = (
        rf"^\[NexusVerifyBuild\] {re.escape(target)} "
        rf"\({re.escape(backend)}, stripping [A-Za-z]+\) succeeded: "
        r"[1-9][0-9]* bytes, [0-9]+[.,][0-9]+s, warnings=[0-9]+\s*$"
    )
    if len(re.findall(marker, log.read_text(encoding="utf-8", errors="replace"),
                      re.MULTILINE)) != 1:
        raise ValueError(f"missing unique current {target}/{backend} success marker")
    folder = project / "builds" / target
    data = folder / f"{target}_Data"
    identity = project / "builds" / f"{target}.commit"
    files = [folder / f"{target}.exe", folder / "UnityPlayer.dll",
             data / "globalgamemanagers", identity]
    files += ([data / "Managed/Assembly-CSharp.dll"] if backend == "Mono2x"
              else [folder / "GameAssembly.dll",
                    data / "il2cpp_data/Metadata/global-metadata.dat"])
    records = []
    for path in files:
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f"missing or empty player output: {path}")
        # Unity may preserve template DLL/executable timestamps when copying them.
        # The serialized player data and commit sidecar must be written this stage.
        if path in (data / "globalgamemanagers", identity):
            if path.stat().st_mtime_ns < started_ns:
                raise ValueError(f"stale build output: {path}")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        records.append(dict(path=str(path), size=path.stat().st_size,
                            mtime_ns=path.stat().st_mtime_ns, sha256=digest))
    if identity.read_text(encoding="utf-8").strip() != commit:
        raise ValueError("build .commit differs from invocation identity")
    report.write_text(json.dumps(dict(target=target, backend=backend, commit=commit,
                                     started_ns=started_ns, files=records), indent=2) + "\n",
                      encoding="utf-8")
    print(f"{target}/{backend}: current marker, player data and commit validated")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    tests = commands.add_parser("tests")
    tests.add_argument("xml", type=Path)
    build = commands.add_parser("build")
    build.add_argument("project", type=Path)
    build.add_argument("log", type=Path)
    build.add_argument("target", choices=("standalone-mono", "standalone-il2cpp"))
    build.add_argument("backend", choices=("Mono2x", "IL2CPP"))
    build.add_argument("started_ns", type=int)
    build.add_argument("commit")
    build.add_argument("report", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "tests":
            check_tests(args.xml)
        else:
            check_build(args.project, args.log, args.target, args.backend,
                        args.started_ns, args.commit, args.report)
    except (OSError, ValueError, ET.ParseError) as error:
        print(f"Verification failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())

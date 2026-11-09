"""Runner regressions: real Bash processes, fake editors, isolated evidence."""
import os
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


HERE = Path(__file__).resolve().parent
BASH = os.environ.get("BASH_PATH", r"C:\Program Files\Git\bin\bash.exe")
RUNNER = Path(os.environ.get("UNITY_VERIFY_RUNNER", HERE / "verify-unity-build.sh"))
PASS_XML = (
    '<test-run result="Passed" total="1" passed="1" failed="0" '
    'inconclusive="0" skipped="0"><test-case result="Passed"/></test-run>'
)
MIXED_IGNORED_XML = (
    '<test-run result="Skipped:Ignored" total="2" passed="1" failed="0" '
    'inconclusive="0" skipped="1">'
    '<test-suite result="Skipped" label="Ignored" total="2" passed="1" failed="0" '
    'inconclusive="0" skipped="1"><test-case result="Passed"/>'
    '<test-suite result="Skipped" label="Ignored" total="1" passed="0" failed="0" '
    'inconclusive="0" skipped="1"><test-case result="Skipped" label="Ignored"/>'
    '</test-suite></test-suite></test-run>'
)
FAKE_EDITOR = r'''
import json
import os
from pathlib import Path
import sys

args = sys.argv[1:]
def option(name):
    return args[args.index(name) + 1]

scenario = os.environ.get("FAKE_SCENARIO", "positive")
project = Path(option("-projectPath"))
log = Path(option("-logFile"))
log.write_text("fixture editor log\n", encoding="utf-8")
with (project.parent / "calls.jsonl").open("a", encoding="utf-8") as calls:
    calls.write(json.dumps(args) + "\n")
if scenario == "exit_nonzero":
    sys.exit(7)
if "-runTests" in args:
    xml = os.environ["FAKE_XML"]
    if scenario != "missing_xml":
        Path(option("-testResults")).write_text(xml, encoding="utf-8")
    sys.exit(0)
method = option("-executeMethod")
if scenario == "exit_nonzero_build":
    sys.exit(7)
il2cpp = method.endswith("IL2CPP")
target = "standalone-il2cpp" if il2cpp else "standalone-mono"
backend = "IL2CPP" if il2cpp else "Mono2x"
folder = project / "builds" / target
data = folder / (target + "_Data")
if scenario == "empty_build":
    folder.mkdir(parents=True, exist_ok=True)
    sys.exit(0)
marker_target = "wrong-target" if scenario == "wrong_target" else target
marker_backend = "IL2CPP" if scenario == "wrong_backend" else backend
if scenario != "missing_marker":
    log.write_text(
        f"[NexusVerifyBuild] {marker_target} ({marker_backend}, stripping Minimal) "
        "succeeded: 123 bytes, 1.2s, warnings=0\n", encoding="utf-8")
    if scenario == "duplicate_marker":
        log.write_text(log.read_text() * 2, encoding="utf-8")
identity = project / "builds" / (target + ".commit")
files = [folder / (target + ".exe"), folder / "UnityPlayer.dll",
         data / "globalgamemanagers", identity]
files += ([folder / "GameAssembly.dll", data / "il2cpp_data/Metadata/global-metadata.dat"]
          if il2cpp else [data / "Managed/Assembly-CSharp.dll"])
for path in files:
    path.parent.mkdir(parents=True, exist_ok=True)
    if scenario == "stale_outputs" and path.exists():
        continue
    content = os.environ.get("FAKE_COMMIT", "a" * 40) if path == identity else "fixture player bytes"
    if path == identity and scenario == "wrong_commit":
        content = "b" * 40
    path.write_text(content, encoding="utf-8")
    if scenario == "stale_outputs" or (scenario == "stale_commit" and path == identity):
        os.utime(path, (1577836800, 1577836800))
    if scenario == "stale_data" and path == data / "globalgamemanagers":
        os.utime(path, (1577836800, 1577836800))
if scenario == "missing_exe":
    (folder / (target + ".exe")).unlink()
if scenario == "missing_data":
    (data / "globalgamemanagers").unlink()
if scenario == "empty_exe":
    (folder / (target + ".exe")).write_bytes(b"")
if scenario == "missing_backend" and il2cpp:
    (folder / "GameAssembly.dll").unlink()
'''


class RunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="unity-verify-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.tool = self.root / "tools/unity-verify"
        self.tool.mkdir(parents=True)
        shutil.copyfile(RUNNER, self.tool / "verify-unity-build.sh")
        if (HERE / "check_results.py").exists():
            shutil.copyfile(HERE / "check_results.py", self.tool / "check_results.py")
        self.project = self.root / "Nexus"
        (self.project / "ProjectSettings").mkdir(parents=True)
        (self.project / "ProjectSettings/ProjectVersion.txt").write_text(
            "m_EditorVersion: 6000.6.8f1\n", encoding="utf-8"
        )
        self.editor = self.root / "fake-editor.sh"
        self.editor.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        (self.bin / "git").write_text(
            '#!/usr/bin/env bash\nif [[ "$*" == *rev-parse* ]]; then '
            'echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; fi\n', encoding="utf-8"
        )
        self.env = dict(os.environ, UNITY_PATH=self.editor.as_posix(),
                        PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        PYTHONDONTWRITEBYTECODE="1")

    def run_runner(self):
        return subprocess.run(
            [BASH, "-c", 'export PATH="$(cd "$1" && pwd):$PATH"; exec bash "$2"',
             "fixture", self.bin.as_posix(), (self.tool / "verify-unity-build.sh").as_posix()],
            cwd=self.root, env=self.env, text=True, capture_output=True, timeout=30
        )

    def seed_stale(self):
        artifacts = self.tool / "artifacts"
        artifacts.mkdir(exist_ok=True)
        for mode in ("editmode", "playmode"):
            path = artifacts / f"{mode}-results.xml"
            path.write_text(PASS_XML, encoding="utf-8")
            os.utime(path, (1577836800, 1577836800))
        for target in ("standalone-mono", "standalone-il2cpp"):
            (self.project / "builds" / target).mkdir(parents=True)

    def producing_editor(self, scenario="positive", xml=PASS_XML):
        script = self.root / "fake_editor.py"
        script.write_text(FAKE_EDITOR, encoding="utf-8")
        self.editor.write_text(
            '#!/usr/bin/env bash\nexec python "$(dirname "$0")/fake_editor.py" "$@"\n',
            encoding="utf-8"
        )
        self.env.update(FAKE_SCENARIO=scenario, FAKE_XML=xml)

    def evidence(self):
        return list((self.tool / "artifacts").glob("run-*"))

    def test_noop_editor_cannot_accept_stale_results_and_empty_builds(self):
        self.seed_stale()
        result = self.run_runner()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            (self.tool / "artifacts/editmode-results.xml").read_text(), PASS_XML
        )

    def test_untracked_sources_are_named_in_the_build_identity(self):
        git = shutil.which("git")
        self.assertIsNotNone(git)
        git_command = [git, "-C", str(self.root)]
        subprocess.run(git_command + ["init", "--quiet"], check=True, capture_output=True)
        subprocess.run(git_command + ["add", "Nexus/ProjectSettings/ProjectVersion.txt"], check=True, capture_output=True)
        subprocess.run(git_command + ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                                      "commit", "--quiet", "-m", "fixture"], check=True, capture_output=True)
        head = subprocess.run(git_command + ["rev-parse", "HEAD"], check=True,
                              capture_output=True, text=True).stdout.strip()
        (self.bin / "git").write_text(
            '#!/usr/bin/env bash\nexec "' + Path(git).as_posix() + '" "$@"\n', encoding="utf-8"
        )
        source = self.project / "Packages/example/Runtime/Untracked.cs"
        source.parent.mkdir(parents=True)
        source.write_text("public class Untracked { }\n", encoding="utf-8")
        self.producing_editor()
        expected = head + " (Packages or Assets modified)"
        self.env["FAKE_COMMIT"] = expected
        result = self.run_runner()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.evidence()[0] / "source.commit").read_text().strip(), expected)

    def test_status_query_failure_is_not_a_clean_worktree(self):
        (self.bin / "git").write_text(
            '#!/usr/bin/env bash\n'
            'if [[ "$*" == *rev-parse* ]]; then echo aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;\n'
            'elif [[ "$*" == *status* ]]; then echo "status failed" >&2; exit 17; fi\n',
            encoding="utf-8"
        )
        self.producing_editor()
        result = self.run_runner()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse((self.root / "calls.jsonl").exists(), "no editor should start with unknown source identity")

    def test_missing_new_results_fail_even_when_old_reports_exist(self):
        self.seed_stale()
        self.producing_editor("missing_xml")
        result = self.run_runner()
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_untrustworthy_xml_reports_fail(self):
        reports = {
            "malformed": '<test-run result="Passed"',
            "wrong_root": PASS_XML.replace("test-run", "test-suite"),
            "failed_root_passed_child": PASS_XML.replace('result="Passed"', 'result="Failed"', 1),
            "zero": '<test-run result="Passed" total="0" passed="0" failed="0" inconclusive="0" skipped="0"/>',
            "skipped_only": '<test-run result="Passed" total="1" passed="0" failed="0" inconclusive="0" skipped="1"><test-case result="Skipped"/></test-run>',
            "inconclusive": '<test-run result="Passed" total="2" passed="1" failed="0" inconclusive="1" skipped="0"><test-case result="Passed"/><test-case result="Inconclusive"/></test-run>',
            "missing_count": PASS_XML.replace('inconclusive="0" ', ""),
            "negative_count": PASS_XML.replace('passed="1"', 'passed="-1"'),
            "mismatched_count": PASS_XML.replace('passed="1"', 'passed="2"'),
            "missing_cases": PASS_XML.replace('<test-case result="Passed"/>', ""),
            "failed_child": PASS_XML.replace('<test-case result="Passed"/>', '<test-case result="Failed"/>'),
            "inconclusive_suite": PASS_XML.replace('<test-case', '<test-suite result="Inconclusive"/><test-case'),
        }
        self.seed_stale()
        for name, xml in reports.items():
            with self.subTest(report=name):
                self.producing_editor(xml=xml)
                result = self.run_runner()
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("Verification failed:", result.stderr)

    def test_nonzero_editor_exit_keeps_log(self):
        self.producing_editor("exit_nonzero")
        result = self.run_runner()
        self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
        logs = [p / "editmode.log" for p in self.evidence()]
        self.assertEqual(len(logs), 1)
        self.assertIn("fixture editor log", logs[0].read_text())
        self.assertIn("fixture editor log", result.stdout)

    def test_incomplete_builds_fail(self):
        self.seed_stale()
        for scenario in ("empty_build", "missing_marker", "wrong_target", "wrong_backend",
                         "missing_exe", "empty_exe", "missing_data", "stale_outputs",
                         "stale_commit", "stale_data", "wrong_commit", "missing_backend",
                         "duplicate_marker"):
            with self.subTest(scenario=scenario):
                self.producing_editor(scenario)
                result = self.run_runner()
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("Verification failed:", result.stderr)
                self.assertGreaterEqual(
                    len((self.root / "calls.jsonl").read_text().splitlines()), 3
                )
                # Keep each stage's old outputs: a later invocation must not trust them.

    def test_nonzero_build_exit_keeps_current_log(self):
        self.producing_editor("exit_nonzero_build")
        result = self.run_runner()
        self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
        self.assertEqual(len(self.evidence()), 1)
        self.assertIn("fixture editor log",
                      (self.evidence()[0] / "standalone-mono-build.log").read_text())
        self.assertFalse((self.evidence()[0] / "standalone-mono-build.json").exists())

    def test_valid_reports_and_players_pass_without_quit_on_tests(self):
        self.producing_editor()
        result = self.run_runner()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        calls = [json.loads(line) for line in (self.root / "calls.jsonl").read_text().splitlines()]
        self.assertEqual(len(calls), 4)
        for call in calls[:2]:
            self.assertIn("-runTests", call)
            self.assertNotIn("-quit", call)
        for call in calls[2:]:
            self.assertIn("-quit", call)
        self.assertEqual(len(self.evidence()), 1)
        for target in ("standalone-mono", "standalone-il2cpp"):
            report = json.loads((self.evidence()[0] / f"{target}-build.json").read_text())
            self.assertEqual(report["commit"], "a" * 40)
            self.assertTrue(all(len(item["sha256"]) == 64 for item in report["files"]))

    def test_some_legitimate_skips_are_allowed(self):
        xml = ('<test-run result="Passed" total="2" passed="1" failed="0" '
               'inconclusive="0" skipped="1"><test-case result="Passed"/>'
               '<test-case result="Skipped" label="Ignored"/></test-run>')
        for label in ("Ignored", "Explicit", ""):
            with self.subTest(label=label):
                self.producing_editor(xml=xml.replace('label="Ignored"', f'label="{label}"'))
                result = self.run_runner()
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("skipped=1 total=2", result.stdout)

    def test_passed_root_does_not_hide_unknown_nested_labels(self):
        for selector in (".//test-case[@result='Passed']", ".//test-case[@result='Skipped']",
                         "./test-suite"):
            with self.subTest(selector=selector):
                root = ET.fromstring(MIXED_IGNORED_XML)
                root.set("result", "Passed")
                root.find(selector).set("label", "Unknown")
                self.producing_editor(xml=ET.tostring(root, encoding="unicode"))
                result = self.run_runner()
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("Verification failed:", result.stderr)

    def test_unity_mixed_ignored_aggregate_and_skipped_child_suite_are_allowed(self):
        self.producing_editor(xml=MIXED_IGNORED_XML)
        result = self.run_runner()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("result=Skipped:Ignored passed=1 failed=0 inconclusive=0 skipped=1 total=2",
                      result.stdout)

    def test_mixed_ignored_aggregate_does_not_hide_invalid_results(self):
        variants = [
            ("plain_skipped_root", ".", {"result": "Skipped"}),
            ("unknown_root", ".", {"result": "Skipped:Unknown"}),
            ("failed_root", ".", {"result": "Failed"}),
            ("inconclusive_root", ".", {"result": "Inconclusive"}),
            ("wrong_total", ".", {"total": "3"}),
            ("failed_count", ".", {"failed": "1", "total": "3"}),
            ("inconclusive_count", ".", {"inconclusive": "1", "total": "3"}),
            ("failed_case", ".//test-case", {"result": "Failed"}),
            ("unknown_case", ".//test-case", {"result": "Unknown"}),
            ("failed_suite", "./test-suite", {"result": "Failed"}),
            ("unknown_suite", "./test-suite", {"result": "Unknown"}),
            ("wrong_suite_counts", "./test-suite", {"passed": "2", "total": "3"}),
            ("unknown_skip_label", ".//test-suite/test-suite", {"label": "Unknown"}),
            ("unknown_case_label", ".//test-case[@result='Skipped']", {"label": "Unknown"}),
        ]
        reports = {}
        for name, selector, attributes in variants:
            root = ET.fromstring(MIXED_IGNORED_XML)
            root.find(selector).attrib.update(attributes)
            reports[name] = ET.tostring(root, encoding="unicode")
        for name, passed, skipped, cases in (
                ("skipped_only", 0, 1, '<test-case result="Skipped" label="Ignored"/>'),
                ("zero", 0, 0, ""),
                ("no_skips", 1, 0, '<test-case result="Passed"/>')):
            reports[name] = (
                f'<test-run result="Skipped:Ignored" total="{passed + skipped}" '
                f'passed="{passed}" failed="0" inconclusive="0" skipped="{skipped}">'
                f'{cases}</test-run>'
            )
        for name, xml in reports.items():
            with self.subTest(report=name):
                self.producing_editor(xml=xml)
                result = self.run_runner()
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("Verification failed:", result.stderr)

    def test_each_invocation_uses_distinct_evidence_and_preserves_old_files(self):
        self.seed_stale()
        self.producing_editor()
        for _ in range(2):
            result = self.run_runner()
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(self.evidence()), 2)
        self.assertEqual((self.tool / "artifacts/editmode-results.xml").read_text(), PASS_XML)

    def test_requested_evidence_directory_cannot_be_reused(self):
        evidence = self.root / "requested-evidence"
        self.env["UNITY_VERIFY_EVIDENCE_DIR"] = evidence.as_posix()
        self.producing_editor()
        first = self.run_runner()
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        second = self.run_runner()
        self.assertNotEqual(second.returncode, 0, second.stdout + second.stderr)
        self.assertEqual(len((self.root / "calls.jsonl").read_text().splitlines()), 4)
        self.assertTrue((evidence / "standalone-mono-build.json").exists())


if __name__ == "__main__":
    unittest.main()

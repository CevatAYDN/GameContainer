"""The lightweight audit must report text candidates, not semantic proof."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("nexus-code-audit.ps1")


class CodeAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nexus-text-audit-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.tool = self.root / "tools/nexus-code-audit.ps1"
        self.tool.parent.mkdir()
        shutil.copyfile(SCRIPT, self.tool)
        subprocess.run(["git", "init", "--quiet", str(self.root)], check=True,
                       capture_output=True, text=True)

    def source(self, name, text, tracked=True):
        path = self.root / "Nexus/Packages/example/Runtime" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        if tracked:
            subprocess.run(["git", "-C", str(self.root), "add", str(path)],
                           check=True, capture_output=True, text=True)

    def run_audit(self, tool=None):
        return subprocess.run(["pwsh", "-NoProfile", "-File", str(tool or self.tool)],
                              cwd=self.root, capture_output=True, text=True, timeout=30)

    def test_comments_and_strings_are_labelled_as_text_not_methods_or_calls(self):
        self.source("Documented.cs",
                    '// async void Example()\n'
                    'class Data { const string Hint = "Thread.Sleep(10)"; }\n'
                    '// public struct ExampleSignal { }\n')
        result = self.run_audit()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Async void text matches: 1", result.stdout)
        self.assertIn("Thread.Sleep text matches: 1", result.stdout)
        self.assertIn("Mutable signal text matches: 1", result.stdout)
        self.assertIn("comments and strings may match", result.stdout)
        self.assertIn("not a semantic correctness verdict", result.stdout)
        self.assertNotIn("Async Void Methods:", result.stdout)
        self.assertNotIn("Main-Thread Sleep Calls:", result.stdout)
        self.assertNotIn("FAIL  Thread.Sleep blocking call", result.stdout)

    def test_untracked_files_are_explicitly_out_of_scope_and_zero_is_not_a_pass_claim(self):
        self.source("Tracked.cs", "class Tracked { }\n")
        self.source("Untracked.cs", "class Untracked { void Run() { Thread.Sleep(10); } }\n", tracked=False)
        result = self.run_audit()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("1 tracked files", result.stdout)
        self.assertIn("Thread.Sleep text matches: 0", result.stdout)
        self.assertIn("Untracked and ignored files are not scanned", result.stdout)
        self.assertIn("Zero matches do not prove", result.stdout)

    def test_git_failure_is_not_an_empty_success(self):
        tool = self.root / "without-git/tools/nexus-code-audit.ps1"
        tool.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPT, tool)
        # A separate path outside the fixture repository prevents parent Git discovery.
        with tempfile.TemporaryDirectory(prefix="nexus-no-git-") as outside:
            isolated = Path(outside) / "tools/nexus-code-audit.ps1"
            isolated.parent.mkdir()
            shutil.copyfile(tool, isolated)
            result = self.run_audit(isolated)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("git ls-files failed", result.stdout)


if __name__ == "__main__":
    unittest.main()

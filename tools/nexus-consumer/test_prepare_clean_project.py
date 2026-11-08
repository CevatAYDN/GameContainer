"""Tooling checks in disposable folders; no Unity, Git changes or game data."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("prepare_clean_project.py")
PACKAGES = ("com.nexus.core", "com.nexus.ads", "com.nexus.ads.bridge.nexus", "com.nexus.firebase")


class PreparationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="nexus-consumer-tool-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / "evidence"
        self.archives = {}
        for name in PACKAGES:
            archive = self.root / (name + ".tgz")
            content = json.dumps({"name": name, "version": "0.1.0"}).encode()
            with tarfile.open(archive, "w:gz") as packed:
                entry = tarfile.TarInfo("package/package.json")
                entry.size = len(content)
                packed.addfile(entry, io.BytesIO(content))
            self.archives[name] = archive

    def prepare(self, *extra):
        return subprocess.run(
            [sys.executable, "-I", "-B", str(SCRIPT), "--archive",
             str(self.archives["com.nexus.core"]), "--output", str(self.output), *map(str, extra)],
            capture_output=True, text=True, timeout=30,
        )

    def manifest(self):
        return json.loads((self.output / "CleanConsumer/Packages/manifest.json").read_text())

    def test_default_keeps_core_only_without_testables_and_legacy_identity(self):
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = self.manifest()
        self.assertNotIn("testables", manifest)
        self.assertEqual(["com.nexus.core"], [key for key in manifest["dependencies"] if key.startswith("com.nexus.")])
        self.assertEqual("file:" + self.archives["com.nexus.core"].as_posix(), manifest["dependencies"]["com.nexus.core"])
        identity = json.loads((self.output / "consumer-preparation.json").read_text())
        self.assertEqual({"project", "archive", "sha256"}, set(identity))
        self.assertEqual(hashlib.sha256(self.archives["com.nexus.core"].read_bytes()).hexdigest(), identity["sha256"])
        self.assertFalse((self.output / "CleanConsumer/Assets/Smoke/Tests/AdsBridge").exists())

    def test_performance_lists_only_core_as_testable(self):
        result = self.prepare("--testables")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["com.nexus.core"], self.manifest()["testables"])

    def test_bundle_installs_and_lists_all_archives_without_sdk_dependencies(self):
        result = self.prepare("--ads-archive", self.archives["com.nexus.ads"],
                              "--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"],
                              "--firebase-archive", self.archives["com.nexus.firebase"])
        self.assertEqual(0, result.returncode, result.stderr)
        manifest = self.manifest()
        self.assertEqual(list(PACKAGES), manifest["testables"])
        for name, archive in self.archives.items():
            self.assertEqual("file:" + archive.as_posix(), manifest["dependencies"][name])
        self.assertFalse(any(key.startswith("com.nexus.") and key not in PACKAGES for key in manifest["dependencies"]))
        self.assertFalse(any(key.startswith("com.google.firebase.") for key in manifest["dependencies"]))
        identity = json.loads((self.output / "consumer-preparation.json").read_text())
        self.assertEqual(list(PACKAGES), [item["package"] for item in identity["packages"]])
        for item in identity["packages"]:
            path = self.archives[item["package"]]
            self.assertEqual(str(path), item["archive"])
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), item["sha256"])
        tests = self.output / "CleanConsumer/Assets/Smoke/Tests/AdsBridge"
        assembly = json.loads((tests / "NexusIntegrationSmoke.AdsBridge.Tests.asmdef").read_text())
        self.assertIn("Nexus.Ads.Bridge.Nexus", assembly["references"])
        source = (tests / "AdsBridgeConsumerTests.cs").read_text()
        self.assertIn("NexusAdsBridge.Connect(null, factory)", source)
        self.assertIn("NexusAdsBridge.Attach(service, factory, null", source)
        self.assertIn('"com.nexus.firebase"', source)
        self.assertNotIn("Reflection", source)

    def test_firebase_adapter_manifest_does_not_require_an_sdk(self):
        manifest_path = SCRIPT.resolve().parents[2] / "Nexus/Packages/com.nexus.firebase/package.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        self.assertEqual({"com.nexus.core"}, set(manifest["dependencies"]))

    def test_partial_or_missing_bundle_refuses_before_writing(self):
        for index, extra in enumerate((
            ("--ads-archive", self.archives["com.nexus.ads"]),
            ("--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"]),
            ("--firebase-archive", self.archives["com.nexus.firebase"]),
            ("--ads-archive", self.archives["com.nexus.ads"],
             "--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"]),
            ("--ads-archive", self.archives["com.nexus.ads"],
             "--firebase-archive", self.archives["com.nexus.firebase"]),
            ("--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"],
             "--firebase-archive", self.archives["com.nexus.firebase"]),
            ("--ads-archive", self.root / "missing.tgz",
             "--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"],
             "--firebase-archive", self.archives["com.nexus.firebase"]),
            ("--ads-archive", self.archives["com.nexus.ads"],
             "--ads-bridge-archive", self.archives["com.nexus.ads.bridge.nexus"],
             "--firebase-archive", self.root / "missing.tgz"),
        )):
            with self.subTest(extra=extra):
                self.output = self.root / ("evidence-" + str(index))
                result = self.prepare(*extra)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(self.output.exists())
                self.assertTrue("must be supplied together" in result.stderr or "Archive not found" in result.stderr)

    def test_existing_consumer_is_not_overwritten(self):
        self.assertEqual(0, self.prepare().returncode)
        before = (self.output / "consumer-preparation.json").read_bytes()
        result = self.prepare("--testables")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Refusing to overwrite", result.stderr)
        self.assertEqual(before, (self.output / "consumer-preparation.json").read_bytes())
        self.assertNotIn("testables", self.manifest())


if __name__ == "__main__":
    unittest.main()

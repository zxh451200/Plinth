"""Synthetic fixtures test the tooling, not the application's build or licenses."""
import base64
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest.mock import patch
import zipfile

import release_checks
import third_party


def synthetic_report():
    return {
        "schema_version": 1, "coverage": "Synthetic tooling fixture only", "scope": "fixture",
        "distribution_inventory_complete": False, "license_review_approved": False,
        "inputs": {"assets": [{"path": "inputs/assets/1/project.assets.json", "sha256": "0" * 64}],
                   "publish_deps": [], "supplemental": None},
        "nuget_packages": [], "supplemental_components": [], "uncovered_publish_packages": [],
        "reconciled_publish_packages": [],
        "excluded_restored_packages": [], "pending_scope_checks": [], "pending_materials": [],
    }


class ReleaseToolTests(unittest.TestCase):
    def test_missing_lock_is_blocked(self):
        for base in ("src", "tests", "tools", "samples"):
            with self.subTest(base=base), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                project = root / base / "App" / "App.csproj"
                project.parent.mkdir(parents=True)
                project.write_text("<Project />")
                with patch.object(release_checks, "ROOT", root):
                    with self.assertRaisesRegex(ValueError, "Missing"):
                        release_checks.locks()

    def test_checksum_and_zip_content(self):
        with tempfile.TemporaryDirectory() as folder:
            artifact = Path(folder) / "candidate.zip"
            with zipfile.ZipFile(artifact, "w") as archive:
                archive.writestr("coverage-report.json", json.dumps(synthetic_report()))
                archive.writestr("Plinth.App.exe", b"synthetic executable fixture")
                archive.writestr("plinth.install.lock", "synthetic installation marker")
                archive.writestr("THIRD_PARTY_NOTICES.md", "synthetic notice fixture")
                archive.writestr("sbom.cdx.json", json.dumps({
                    "bomFormat": "CycloneDX", "components": [{"name": "fixture"}]}))
            checksum = Path(str(artifact) + ".sha256")
            checksum.write_text(hashlib.sha256(artifact.read_bytes()).hexdigest() + "  " + artifact.name)
            release_checks.package(artifact)
            checksum.write_text("0" * 64 + "  " + artifact.name)
            with self.assertRaisesRegex(ValueError, "Checksum"):
                release_checks.package(artifact)

    def test_missing_empty_corrupt_and_wrong_shape_reports_are_blocked(self):
        invalid = [None, b"", b" ", b"{", b"\xff", b"null", b"[]", b"{}"]
        for field, value in (("schema_version", True), ("schema_version", 2),
                             ("inputs", []), ("nuget_packages", {}),
                             ("nuget_packages", [None]), ("supplemental_components", [None]),
                             ("reconciled_publish_packages", {})):
            report = synthetic_report()
            report[field] = value
            invalid.append(json.dumps(report).encode())
        for path in ("/home/private/asset.json", "C:/Users/private/asset.json", "../asset.json"):
            report = synthetic_report()
            report["inputs"]["assets"][0]["path"] = path
            invalid.append(json.dumps(report).encode())
        with tempfile.TemporaryDirectory() as folder:
            artifact = Path(folder) / "candidate.zip"
            for raw in invalid:
                with self.subTest(raw=raw):
                    with zipfile.ZipFile(artifact, "w") as archive:
                        for name in ("Plinth.App.exe", "plinth.install.lock", "THIRD_PARTY_NOTICES.md"):
                            archive.writestr(name, "synthetic fixture")
                        archive.writestr("sbom.cdx.json", json.dumps({
                            "bomFormat": "CycloneDX", "components": [{"name": "fixture"}]}))
                        if raw is not None:
                            archive.writestr("coverage-report.json", raw)
                    Path(str(artifact) + ".sha256").write_text(
                        hashlib.sha256(artifact.read_bytes()).hexdigest() + "  " + artifact.name)
                    with self.assertRaises((ValueError, UnicodeError)):
                        release_checks.package(artifact)

    def test_notice_generation_and_rejection(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            package = root / "cache/fixture/1.0.0"
            package.mkdir(parents=True)
            nuspec = package / "fixture.nuspec"
            template = ('<package><metadata><id>Fixture</id><version>1.0.0</version>'
                        '<license type="expression">{}</license></metadata></package>')
            nuspec.write_text(template.format("MIT"))
            (package / "NOTICE").write_text("synthetic copyright fixture")
            assets = root / "project.assets.json"
            assets.write_text(json.dumps({
                "packageFolders": {str(root / "cache"): {}},
                "libraries": {"Fixture/1.0.0": {
                    "type": "package", "path": "fixture/1.0.0",
                    "sha512": base64.b64encode(bytes(64)).decode()}}}))
            output = root / "out"
            with patch.object(third_party, "SPDX_CACHE", root / "spdx-cache"), \
                    patch("urllib.request.urlopen", return_value=io.BytesIO(b"synthetic SPDX text fixture")):
                third_party.generate([assets], output)
            notice = (output / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8-sig")
            self.assertIn("synthetic copyright fixture", notice)
            self.assertIn(f"License text source: {third_party.SPDX_SOURCE}MIT.txt", notice)
            self.assertIn("License text SHA256: " + hashlib.sha256(
                b"synthetic SPDX text fixture").hexdigest(), notice)
            sbom = json.loads((output / "sbom.cdx.json").read_text(encoding="utf-8-sig"))
            self.assertEqual(sbom["components"][0]["purl"], "pkg:nuget/Fixture@1.0.0")
            # This synthetic metadata verifies policy behavior, not Npgsql package contents.
            nuspec.write_text(template.format("PostgreSQL"))
            with patch.object(third_party, "SPDX_CACHE", root / "spdx-cache"), \
                    patch("urllib.request.urlopen", return_value=io.BytesIO(b"synthetic PostgreSQL license fixture")) as fetch:
                third_party.generate([assets], output)
            fetch.assert_called_once_with(third_party.SPDX_SOURCE + "PostgreSQL.txt", timeout=30)
            sbom = json.loads((output / "sbom.cdx.json").read_text(encoding="utf-8-sig"))
            self.assertEqual(sbom["components"][0]["licenses"], [{"license": {"id": "PostgreSQL"}}])
            self.assertIn("synthetic PostgreSQL license fixture", (output / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8-sig"))
            for expression in ("Unknown-License", "MIT AND GPL-3.0-only", "(MIT OR Apache-2.0)"):
                nuspec.write_text(template.format(expression))
                with self.assertRaises(ValueError):
                    third_party.generate([assets], output)

    def test_package_path_traversal_is_blocked(self):
        with self.assertRaises(ValueError):
            third_party.inside(Path("/cache"), "../outside")

    def test_zip_without_installation_marker_is_blocked(self):
        with tempfile.TemporaryDirectory() as folder:
            artifact = Path(folder) / "missing-marker.zip"
            with zipfile.ZipFile(artifact, "w") as archive:
                archive.writestr("Plinth.App.exe", b"synthetic fixture")
            Path(str(artifact) + ".sha256").write_text(
                hashlib.sha256(artifact.read_bytes()).hexdigest() + "  " + artifact.name)
            with self.assertRaisesRegex(ValueError, "plinth.install.lock"):
                release_checks.package(artifact)


class SpdxTextTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(dir=third_party.ROOT)
        self.addCleanup(temporary.cleanup)
        self.cache = Path(temporary.name)
        cache_patch = patch.object(third_party, "SPDX_CACHE", self.cache)
        cache_patch.start()
        self.addCleanup(cache_patch.stop)
        network_patch = patch("third_party.urllib.request.urlopen",
                              side_effect=urllib.error.URLError("offline"))
        self.network = network_patch.start()
        self.addCleanup(network_patch.stop)
        # These markers exercise tooling only; they are never compliance evidence.
        self.text = "SYNTHETIC SPDX TEST FIXTURE ONLY\n"

    def download(self, text):
        self.network.side_effect = None
        self.network.return_value = io.BytesIO(text.encode("utf-8"))
        return third_party.fetch_spdx_text("MIT")

    def test_pinned_download_then_offline_cache(self):
        self.assertEqual(self.download(self.text), self.text)
        self.network.assert_called_once_with(third_party.SPDX_SOURCE + "MIT.txt", timeout=30)
        self.assertEqual(len(list(self.cache.glob("*/MIT.txt"))), 1)
        self.assertFalse((self.cache / "MIT.txt").exists())
        self.network.reset_mock()
        self.network.side_effect = urllib.error.URLError("offline")
        self.assertEqual(third_party.fetch_spdx_text("MIT"), self.text)
        self.network.assert_not_called()

    def test_network_failure_does_not_fall_back_or_cache(self):
        with self.assertRaisesRegex(ValueError, "SPDX text unavailable: MIT") as failure:
            third_party.fetch_spdx_text("MIT")
        self.assertIn(third_party.SPDX_SOURCE + "MIT.txt", str(failure.exception))
        self.network.assert_called_once_with(third_party.SPDX_SOURCE + "MIT.txt", timeout=30)
        self.assertEqual(list(self.cache.rglob("*.txt")), [])

    def test_legacy_cache_cannot_satisfy_offline_request(self):
        legacy = self.cache / "MIT.txt"
        legacy.write_text("SYNTHETIC LEGACY CACHE ONLY", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "SPDX text unavailable"):
            third_party.fetch_spdx_text("MIT")
        self.network.assert_called_once_with(third_party.SPDX_SOURCE + "MIT.txt", timeout=30)
        self.network.reset_mock()
        self.assertEqual(self.download(self.text), self.text)
        self.assertEqual(legacy.read_text(encoding="utf-8"), "SYNTHETIC LEGACY CACHE ONLY")

    def test_changed_pinned_version_cannot_reuse_previous_cache(self):
        self.download(self.text)
        previous_source = third_party.SPDX_SOURCE
        other_source = previous_source.replace("v3.26.0", "v3.25.0")
        self.assertNotEqual(previous_source, other_source)
        self.network.reset_mock()
        self.network.side_effect = urllib.error.URLError("offline")
        with patch.object(third_party, "SPDX_SOURCE", other_source):
            with self.assertRaisesRegex(ValueError, "SPDX text unavailable"):
                third_party.fetch_spdx_text("MIT")
            self.network.assert_called_once_with(other_source + "MIT.txt", timeout=30)
            self.assertEqual(self.download("SYNTHETIC OTHER VERSION ONLY"),
                             "SYNTHETIC OTHER VERSION ONLY")
        self.network.reset_mock()
        self.network.side_effect = urllib.error.URLError("offline")
        self.assertEqual(third_party.fetch_spdx_text("MIT"), self.text)
        self.network.assert_not_called()
        self.assertEqual(len(list(self.cache.glob("*/MIT.txt"))), 2)

    def test_blank_or_invalid_upstream_text_is_not_cached(self):
        for raw in (b"", b" \r\n", b"\xff"):
            with self.subTest(raw=raw):
                self.network.side_effect = None
                self.network.return_value = io.BytesIO(raw)
                with self.assertRaisesRegex(ValueError, "SPDX text unavailable"):
                    third_party.fetch_spdx_text("MIT")
                self.assertEqual(list(self.cache.rglob("*.txt")), [])

    def test_blank_cache_requires_upstream(self):
        self.download(self.text)
        cached, = self.cache.glob("*/MIT.txt")
        for blank in ("", " \n"):
            with self.subTest(blank=blank):
                cached.write_text(blank, encoding="utf-8")
                self.network.side_effect = urllib.error.URLError("offline")
                with self.assertRaisesRegex(ValueError, "SPDX text unavailable"):
                    third_party.fetch_spdx_text("MIT")
        self.assertEqual(self.download(self.text), self.text)


if __name__ == "__main__":
    unittest.main()

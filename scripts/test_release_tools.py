"""Synthetic fixtures test the tooling, not the application's build or licenses."""
import base64
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import release_checks
import third_party


class ReleaseToolTests(unittest.TestCase):
    def test_missing_lock_is_blocked(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "src/App").mkdir(parents=True)
            (root / "src/App/App.csproj").write_text("<Project />")
            with patch.object(release_checks, "ROOT", root):
                with self.assertRaisesRegex(ValueError, "Missing"):
                    release_checks.locks()

    def test_checksum_and_zip_content(self):
        with tempfile.TemporaryDirectory() as folder:
            artifact = Path(folder) / "candidate.zip"
            with zipfile.ZipFile(artifact, "w") as archive:
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
            with patch("urllib.request.urlopen", return_value=io.BytesIO(b"synthetic SPDX text fixture")):
                third_party.generate([assets], output)
            self.assertIn("synthetic copyright fixture", (output / "THIRD_PARTY_NOTICES.md").read_text())
            sbom = json.loads((output / "sbom.cdx.json").read_text())
            self.assertEqual(sbom["components"][0]["purl"], "pkg:nuget/Fixture@1.0.0")
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


if __name__ == "__main__":
    unittest.main()

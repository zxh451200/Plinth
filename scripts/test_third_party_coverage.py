"""Offline synthetic fixtures verify tooling, never actual release compliance."""
import base64
import hashlib
import json
from pathlib import Path
import tempfile
import zipfile
import shutil
import unittest
from unittest.mock import patch
from xml.sax.saxutils import escape

import third_party
import release_checks
import installer_manifest


class CoverageTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(dir=third_party.ROOT)
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.package = self.root / "cache/fixture/1.0"
        self.package.mkdir(parents=True)
        self.nuspec = self.package / "fixture.nuspec"
        self.assets = self.root / "project.assets.json"
        self.assets.write_text(json.dumps({
            "packageFolders": {str(self.root / "cache"): {}},
            "libraries": {"Fixture/1.0": {"type": "package", "path": "fixture/1.0",
                          "sha512": base64.b64encode(b"x" * 64).decode()}}}), encoding="utf-8")
        self.output = self.root / "out"
        self.write_nuspec()
        network = patch("third_party.urllib.request.urlopen", side_effect=AssertionError("offline test"))
        self.network = network.start()
        self.addCleanup(network.stop)
        fetch = patch.object(third_party, "fetch_spdx_text", return_value="SYNTHETIC SPDX TEMPLATE")
        fetch.start()
        self.addCleanup(fetch.stop)

    def write_nuspec(self, copyright_xml="", extra=""):
        self.nuspec.write_text(
            '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
            '<metadata><id>Fixture</id><version>1.0</version><authors>NOT A COPYRIGHT</authors>'
            '<license type="expression">MIT</license>' + copyright_xml + extra +
            '</metadata></package>', encoding="utf-8")

    def generate(self, supplemental=None, publish=None):
        third_party.generate([self.assets], self.output, supplemental, publish)
        return json.loads((self.output / "coverage-report.json").read_text())

    def test_multiline_unicode_attribution_preserved_with_evidence(self):
        text = "  Copyright © 2026 Fixture & 合作者\nAll rights reserved.  "
        self.write_nuspec("<copyright>" + escape(text) + "</copyright>")
        bundled = self.package / "NOTICE"
        bundled.write_bytes(b"SYNTHETIC NOTICE\r\n")
        report = self.generate()
        sbom = json.loads((self.output / "sbom.cdx.json").read_text())
        self.assertEqual(sbom["components"][0]["copyright"], text)
        self.assertIn(text, (self.output / "THIRD_PARTY_NOTICES.md").read_text())
        entry = report["nuget_packages"][0]
        self.assertEqual(entry["copyright"], {"status": "present", "text": text})
        self.assertEqual(entry["bom-ref"], sbom["components"][0]["bom-ref"])
        self.assertEqual(entry["nuspec"]["sha256"], hashlib.sha256(self.nuspec.read_bytes()).hexdigest())
        self.assertEqual(entry["bundled_texts"][0]["sha256"], hashlib.sha256(bundled.read_bytes()).hexdigest())
        self.assertEqual(report["inputs"]["assets"][0]["sha256"], hashlib.sha256(self.assets.read_bytes()).hexdigest())
        self.assertEqual(entry["license"]["source"], third_party.SPDX_SOURCE + "MIT.txt")
        original = (self.output / "coverage-report.json").read_bytes()
        self.generate()
        self.assertEqual((self.output / "coverage-report.json").read_bytes(), original)
        self.network.assert_not_called()

    def test_public_report_paths_and_packaged_references(self):
        # Same filename in different project roots must retain distinct source mappings.
        second = self.root / "private-user/workspace/other/obj/project.assets.json"
        second.parent.mkdir(parents=True)
        second.write_bytes(self.assets.read_bytes() + b"\r\n")
        bundled = self.package / "NOTICE"
        bundled.write_bytes(b"\xef\xbb\xbfSYNTHETIC NOTICE\r\n")
        third_party.generate([self.assets, second], self.output)
        report = json.loads((self.output / "coverage-report.json").read_text())
        release_checks.coverage_report(report)
        inputs = report["inputs"]["assets"]
        self.assertEqual([e["path"] for e in inputs], [
            "inputs/assets/1/project.assets.json", "inputs/assets/2/project.assets.json"])
        self.assertEqual([e["sha256"] for e in inputs], [
            hashlib.sha256(p.read_bytes()).hexdigest() for p in (self.assets, second)])
        package = report["nuget_packages"][0]
        self.assertEqual(package["assets"], [e["path"] for e in inputs])
        self.assertEqual(package["nuspec"]["path"], "nuget/Fixture/1.0/fixture.nuspec")
        self.assertEqual(package["bundled_texts"][0]["path"], "nuget/Fixture/1.0/NOTICE")
        self.assertEqual(package["bundled_texts"][0]["sha256"], hashlib.sha256(bundled.read_bytes()).hexdigest())
        publish = self.root / "publish"
        publish.mkdir()
        for name in ("coverage-report.json", "THIRD_PARTY_NOTICES.md", "sbom.cdx.json"):
            shutil.copyfile(self.output / name, publish / name)
            text = (publish / name).read_text()
            for private in (str(self.root), str(third_party.ROOT), str(self.package), "private-user"):
                self.assertNotIn(private, text)
        (publish / "Plinth.App.exe").write_bytes(b"synthetic executable")
        (publish / "plinth.install.lock").write_text("synthetic marker")
        self.assertIn('Delete "$INSTDIR\\coverage-report.json"', installer_manifest.generate(publish))
        artifact = self.root / "candidate.zip"
        with zipfile.ZipFile(artifact, "w") as archive:
            for file in publish.iterdir():
                archive.write(file, file.name)
        Path(str(artifact) + ".sha256").write_text(
            hashlib.sha256(artifact.read_bytes()).hexdigest() + "  " + artifact.name)
        release_checks.package(artifact)
        with zipfile.ZipFile(artifact) as archive:
            sbom = json.loads(archive.read("sbom.cdx.json"))
            refs = [p["value"] for p in sbom["metadata"]["properties"]
                    if p["name"] == "plinth:coverage-report"]
            self.assertEqual(refs, ["coverage-report.json"])
            self.assertIn("Evidence index and pending materials: " + refs[0],
                          archive.read("THIRD_PARTY_NOTICES.md").decode())
            self.assertEqual(json.loads(archive.read(refs[0])), report)

    def test_missing_empty_whitespace_never_invent_attribution(self):
        for xml, status in (("", "missing"), ("<copyright/>", "empty"),
                            ("<copyright> \n </copyright>", "empty")):
            with self.subTest(status=status, xml=xml):
                self.write_nuspec(xml)
                report = self.generate()
                component = json.loads((self.output / "sbom.cdx.json").read_text())["components"][0]
                self.assertNotIn("copyright", component)
                entry = report["nuget_packages"][0]
                self.assertEqual(entry["copyright"]["status"], status)
                self.assertIn("Locate upstream copyright", entry["pending_materials"][0])
                self.assertNotIn("NOT A COPYRIGHT", (self.output / "THIRD_PARTY_NOTICES.md").read_text())
                self.assertFalse(report["distribution_inventory_complete"])
                self.assertFalse(report["license_review_approved"])

    def test_nested_extension_cannot_supply_package_copyright(self):
        self.write_nuspec(extra="<extension><copyright>WRONG</copyright></extension>")
        self.assertEqual(self.generate()["nuget_packages"][0]["copyright"]["status"], "missing")

    def test_duplicate_copyright_is_blocked(self):
        self.write_nuspec("<copyright>A</copyright><copyright>B</copyright>")
        with self.assertRaisesRegex(ValueError, "Duplicate nuspec metadata field"):
            self.generate()
        self.assertFalse(self.output.exists())

    def test_publish_gap_and_supplement_do_not_imply_coverage(self):
        publish = self.root / "publish"
        publish.mkdir()
        runtime = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0"
        deps = publish / "Fixture.deps.json"
        deps.write_text(json.dumps({"runtimeTarget": {"name": "net10.0/win-x64"},
            "targets": {"net10.0/win-x64": {runtime: {"native": {"runtime.dll": {}}}}},
            "libraries": {"Fixture/1.0": {"type": "package"}, runtime: {"type": "runtimepack"}}}))
        evidence = self.root / "LICENSE"
        evidence.write_bytes(b"SYNTHETIC SUPPLEMENTAL ONLY")
        manifest = self.root / "inventory.json"
        manifest.write_text(json.dumps({"schema_version": 1, "components": [{
            "id": "runtime-fixture", "name": "Runtime fixture", "version": "10.0.0",
            "category": "runtime", "source": "https://example.invalid/runtime",
            "license": "Synthetic declaration", "texts": [{"kind": "LICENSE", "path": "LICENSE",
            "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()}]}]}))
        report = self.generate(manifest, publish)
        self.assertEqual(report["uncovered_publish_packages"][0]["package"], runtime)
        self.assertTrue(report["uncovered_publish_packages"][0]["pending_materials"])
        self.assertEqual(report["inputs"]["publish_deps"][0]["sha256"], hashlib.sha256(deps.read_bytes()).hexdigest())
        self.assertEqual(report["inputs"]["supplemental"]["sha256"], hashlib.sha256(manifest.read_bytes()).hexdigest())
        self.assertEqual(report["inputs"]["publish_deps"][0]["path"], "publish/Fixture.deps.json")
        self.assertEqual(report["inputs"]["supplemental"]["path"], "inputs/supplemental/inventory.json")
        for file in self.output.iterdir():
            self.assertNotIn(str(self.root), file.read_text())
        release_checks.coverage_report(report)
        extra = report["supplemental_components"][0]
        self.assertEqual(extra["texts"][0]["sha256"], hashlib.sha256(evidence.read_bytes()).hexdigest())
        runtime_check = report["pending_scope_checks"][0]
        self.assertEqual(runtime_check["status"], "not-established")
        self.assertEqual(runtime_check["supplied_component_refs"], [extra["bom-ref"]])
        self.assertFalse(report["distribution_inventory_complete"])
        self.assertFalse(report["license_review_approved"])

    def test_excluded_restored_packages_are_traceable(self):
        data = json.loads(self.assets.read_text())
        data["libraries"]["BuildOnly/1.0"] = dict(data["libraries"]["Fixture/1.0"])
        self.assets.write_text(json.dumps(data))
        publish = self.root / "publish"
        publish.mkdir()
        (publish / "App.deps.json").write_text(json.dumps({"libraries": {"Fixture/1.0": {"type": "package"}}}))
        report = self.generate(publish=publish)
        self.assertEqual(report["excluded_restored_packages"], ["BuildOnly/1.0"])
        self.assertEqual(len(report["nuget_packages"]), 1)

    def test_unknown_publish_package_still_blocks(self):
        publish = self.root / "publish"
        publish.mkdir()
        (publish / "App.deps.json").write_text(json.dumps({"libraries": {"Missing/1.0": {"type": "package"}}}))
        with self.assertRaisesRegex(ValueError, "Publish dependencies missing"):
            self.generate(publish=publish)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()

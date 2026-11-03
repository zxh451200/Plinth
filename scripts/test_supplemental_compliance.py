"""Offline synthetic evidence tests; not evidence about actual shipped dependencies."""
import base64
import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from supplemental_compliance import load_supplemental
import third_party

ROOT = Path(__file__).resolve().parents[1]


class SupplementalTests(unittest.TestCase):
    def setUp(self):
        # Keep all temporary writes inside the authorized workspace.
        self.temp = tempfile.TemporaryDirectory(dir=ROOT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.evidence = self.root / "evidence"
        self.evidence.mkdir()
        self.raw = b"SYNTHETIC TEST LICENSE ONLY\r\nCopyright test fixture\r\n"
        (self.evidence / "LICENSE").write_bytes(self.raw)
        (self.evidence / "NOTICE").write_text("Synthetic notice\n", encoding="utf-8")
        self.entry = {
            "id": "fixture-runtime", "name": "Fixture Runtime", "version": "1.2.3",
            "category": "runtime", "source": "https://example.invalid/runtime/1.2.3",
            "license": "Fixture custom license",
            "texts": [{"kind": "LICENSE", "path": "LICENSE",
                       "sha256": hashlib.sha256(self.raw).hexdigest()}],
        }
        self.manifest = self.evidence / "inventory.json"
        self.write_inventory([self.entry])

    def write_inventory(self, entries):
        self.manifest.write_text(json.dumps({"schema_version": 1, "components": entries}),
                                 encoding="utf-8")

    def load(self):
        self.write_inventory([self.entry])
        return load_supplemental(self.manifest)

    def assets(self):
        package = self.root / "packages" / "fixture" / "1.0"
        package.mkdir(parents=True)
        (package / "fixture.nuspec").write_text(
            '<package><metadata><id>Fixture</id><version>1.0</version>'
            '<license type="expression">MIT</license></metadata></package>', encoding="utf-8")
        assets = self.root / "project.assets.json"
        assets.write_text(json.dumps({
            "packageFolders": {str(self.root / "packages"): {}},
            "libraries": {"Fixture/1.0": {"type": "package", "path": "fixture/1.0",
                                         "sha512": base64.b64encode(b"x" * 64).decode()}}
        }), encoding="utf-8")
        return assets

    def generate(self, supplemental=True):
        assets = self.assets()
        output = self.root / "output"
        with patch("third_party.urllib.request.urlopen",
                   return_value=io.BytesIO(b"Synthetic SPDX fixture text\n")):
            third_party.generate([assets], output, self.manifest if supplemental else None)
        return output

    def test_merge_contains_actual_text_and_exact_bytes(self):
        self.entry["texts"].append({"kind": "NOTICE", "path": "NOTICE",
            "sha256": hashlib.sha256((self.evidence / "NOTICE").read_bytes()).hexdigest()})
        self.write_inventory([self.entry])
        output = self.generate()
        notice = (output / "THIRD_PARTY_NOTICES.md").read_text()
        self.assertIn("SYNTHETIC TEST LICENSE ONLY", notice)
        self.assertIn("Synthetic notice", notice)
        self.assertIn("Not a complete distribution inventory", notice)
        sbom = json.loads((output / "sbom.cdx.json").read_text())
        self.assertEqual(len(sbom["components"]), 2)
        extra = sbom["components"][1]
        self.assertEqual(extra["licenses"][0]["license"]["name"], "Fixture custom license")
        self.assertNotIn("id", extra["licenses"][0]["license"])
        self.assertEqual(base64.b64decode(extra["licenses"][0]["license"]["text"]["content"]),
                         self.raw)
        evidence = [json.loads(p["value"]) for p in extra["properties"]
                    if p["name"] == "plinth:supplemental-evidence"]
        self.assertEqual([e["kind"] for e in evidence], ["LICENSE", "NOTICE"])
        for item in evidence:
            self.assertEqual(hashlib.sha256(base64.b64decode(item["content"])).hexdigest(),
                             item["sha256"])

    def test_no_supplement_preserves_candidate(self):
        output = self.generate(False)
        self.assertIn("NuGet-only candidate", (output / "THIRD_PARTY_NOTICES.md").read_text())
        self.assertEqual(len(json.loads((output / "sbom.cdx.json").read_text())["components"]), 1)

    def test_reject_missing_identity_version_source_license(self):
        for field in ("id", "name", "version", "source", "license"):
            original = self.entry[field]
            for value in ("", "   ", None):
                with self.subTest(field=field, value=value):
                    self.entry[field] = value
                    with self.assertRaises(ValueError):
                        self.load()
            self.entry[field] = original

    def test_reject_invalid_source_category_id(self):
        for field, value in (("source", "file:///tmp/license"), ("source", "https:///missing"),
                             ("category", "guess"), ("id", "../escape")):
            with self.subTest(field=field):
                original = self.entry[field]
                self.entry[field] = value
                with self.assertRaises(ValueError):
                    self.load()
                self.entry[field] = original

    def test_reject_paths(self):
        for value in ("../outside", "/etc/passwd", "C:/LICENSE", r"..\LICENSE",
                      "//server/share/LICENSE", ".", "absent"):
            with self.subTest(path=value):
                self.entry["texts"][0]["path"] = value
                with self.assertRaises((OSError, ValueError)):
                    self.load()

    def test_reject_symlink_escape(self):
        outside = self.root / "outside"
        outside.write_bytes(self.raw)
        (self.evidence / "linked").symlink_to(outside)
        self.entry["texts"][0]["path"] = "linked"
        with self.assertRaisesRegex(ValueError, "escapes root"):
            self.load()

    def test_reject_missing_license_body(self):
        for texts in ([], [{"kind": "NOTICE", "path": "LICENSE",
                            "sha256": hashlib.sha256(self.raw).hexdigest()}]):
            self.entry["texts"] = texts
            with self.assertRaisesRegex(ValueError, "LICENSE"):
                self.load()

    def test_reject_blank_or_invalid_utf8_body(self):
        for raw in (b"", b" \r\n", b"\xff"):
            (self.evidence / "LICENSE").write_bytes(raw)
            self.entry["texts"][0]["sha256"] = hashlib.sha256(raw).hexdigest()
            with self.assertRaises(ValueError):
                self.load()

    def test_reject_hash(self):
        for digest in ("0" * 64, "", "bad", None):
            self.entry["texts"][0]["sha256"] = digest
            with self.assertRaisesRegex(ValueError, "SHA256"):
                self.load()

    def test_reject_duplicate_ids_or_identity(self):
        for field, value in (("name", "Other"), ("id", "other-id")):
            other = copy.deepcopy(self.entry)
            other[field] = value
            self.write_inventory([self.entry, other])
            with self.assertRaisesRegex(ValueError, "Duplicate"):
                load_supplemental(self.manifest)

    def test_reject_duplicate_paths(self):
        self.entry["texts"].append(copy.deepcopy(self.entry["texts"][0]))
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.load()

    def test_reject_surrounding_identity_whitespace(self):
        for field in ("name", "version"):
            original = self.entry[field]
            for value in (" " + original, original + " "):
                with self.subTest(field=field, value=value):
                    self.entry[field] = value
                    with self.assertRaisesRegex(ValueError, "whitespace"):
                        self.load()
            self.entry[field] = original

    def test_reject_duplicate_json_fields_at_all_levels(self):
        data = json.dumps({"schema_version": 1, "components": [self.entry]})
        for original, duplicate in (
                ('"schema_version": 1', '"schema_version": 2, "schema_version": 1'),
                ('"version": "1.2.3"', '"version": "other", "version": "1.2.3"'),
                ('"kind": "LICENSE"', '"kind": "NOTICE", "kind": "LICENSE"')):
            with self.subTest(field=original):
                self.manifest.write_text(data.replace(original, duplicate), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Duplicate supplemental JSON field"):
                    load_supplemental(self.manifest)

    def test_reject_nuget_identity_collision_without_output(self):
        self.entry.update(name="fixture", version="1.0")
        self.write_inventory([self.entry])
        with self.assertRaisesRegex(ValueError, "Duplicate NuGet"):
            self.generate()
        self.assertFalse((self.root / "output").exists())

    def test_schema_and_unknown_fields_fail(self):
        for data in ([], {"schema_version": 2, "components": [self.entry]},
                     {"schema_version": 1, "components": []}):
            self.manifest.write_text(json.dumps(data))
            with self.assertRaises(ValueError):
                load_supplemental(self.manifest)
        self.entry["typo"] = True
        with self.assertRaises(ValueError):
            self.load()

    def test_cli_success_and_failure(self):
        tool = ROOT / "scripts" / "supplemental_compliance.py"
        success = subprocess.run([sys.executable, str(tool), "--validate", str(self.manifest)],
                                 capture_output=True, text=True)
        self.assertEqual(success.returncode, 0, success.stderr)
        self.entry["texts"][0]["sha256"] = "0" * 64
        self.write_inventory([self.entry])
        for command in (
                [str(tool), "--validate", str(self.manifest)],
                [str(ROOT / "scripts" / "third_party.py"), "--assets", "absent",
                 "--output", str(self.root / "failed"),
                 "--supplemental-compliance-file", str(self.manifest)]):
            result = subprocess.run([sys.executable] + command, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("BLOCKED:", result.stderr)
            self.assertIn("SHA256", result.stderr)
        self.assertFalse((self.root / "failed").exists())

    def test_powershell_option_forwarding_static_only(self):
        publish = (ROOT / "scripts" / "publish.ps1").read_text()
        acceptance = (ROOT / "scripts" / "acceptance.ps1").read_text()
        self.assertIn("[string]$SupplementalComplianceFile", publish)
        self.assertIn("@('--supplemental-compliance-file', $SupplementalComplianceFile)", publish)
        self.assertIn('supplemental_compliance.py" --validate $SupplementalComplianceFile', publish)
        self.assertLess(publish.index("Supplemental compliance validation failed"),
                        publish.index("dotnet restore"))
        self.assertLess(publish.index("Resolve-Path -LiteralPath $SupplementalComplianceFile"),
                        publish.index("Push-Location"))
        self.assertIn("[string]$SupplementalComplianceFile", acceptance)
        self.assertIn("$supplementalOptions.SupplementalComplianceFile = (Resolve-Path", acceptance)
        self.assertIn("-PythonExecutable $python @supplementalOptions", acceptance)
        self.assertIn("SupplementalComplianceFile requires -Package", acceptance)
        copy_step = publish.index('Copy-Item "$complianceDir/THIRD_PARTY_NOTICES.md", "$complianceDir/sbom.cdx.json" -Destination $publishDir')
        self.assertLess(copy_step, publish.index("Compress-Archive"))
        self.assertLess(copy_step, publish.index("scripts/installer_manifest.py"))
        self.assertIn('"/DPUBLISH_DIR=$publishDir"', publish)


if __name__ == "__main__":
    unittest.main()

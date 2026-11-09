"""Offline synthetic evidence tests; not evidence about actual shipped dependencies."""
import base64
import copy
import hashlib
import io
import json
import shutil
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
        cache_patch = patch.object(third_party, "SPDX_CACHE", self.root / "spdx-cache")
        cache_patch.start()
        self.addCleanup(cache_patch.stop)
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
        notice = (output / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8-sig")
        self.assertIn("SYNTHETIC TEST LICENSE ONLY", notice)
        self.assertIn("Synthetic notice", notice)
        self.assertIn("Not a complete distribution inventory", notice)
        sbom = json.loads((output / "sbom.cdx.json").read_text(encoding="utf-8-sig"))
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
        self.assertIn("NuGet-only candidate", (output / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8-sig"))
        self.assertEqual(len(json.loads((output / "sbom.cdx.json").read_text(encoding="utf-8-sig"))["components"]), 1)

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

    def test_publish_guards_xlsx_payload_against_flag(self):
        publish = (ROOT / "scripts" / "publish.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("XLSX enabled but ClosedXML.dll is missing from publish", publish)
        self.assertIn("ClosedXML.dll must not be published when XLSX is disabled", publish)

    def test_installer_removes_legacy_closedxml_without_shipping_it(self):
        nsi = (ROOT / "installer" / "plinth.nsi").read_text(encoding="utf-8-sig")
        self.assertIn('!if /FileExists "${PUBLISH_DIR}\\ClosedXML.dll"', nsi)
        self.assertIn('Delete "$INSTDIR\\ClosedXML.dll"', nsi)
        self.assertIn('Delete "$INSTDIR\\ExcelNumberFormat.dll"', nsi)

    def test_powershell_option_forwarding_static_only(self):
        publish = (ROOT / "scripts" / "publish.ps1").read_text(encoding="utf-8-sig")
        acceptance = (ROOT / "scripts" / "acceptance.ps1").read_text(encoding="utf-8-sig")
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
        copy_step = publish.index('Copy-Item "$complianceDir/THIRD_PARTY_NOTICES.md", "$complianceDir/sbom.cdx.json", "$complianceDir/coverage-report.json" -Destination $publishDir')
        self.assertLess(copy_step, publish.index("Compress-Archive"))
        self.assertLess(copy_step, publish.index("scripts/installer_manifest.py"))
        self.assertIn('"/DPUBLISH_DIR=$publishDir"', publish)


class PublishInventoryTests(unittest.TestCase):
    assets = SupplementalTests.assets
    write_inventory = SupplementalTests.write_inventory
    # All identities, hashes and license bodies here are synthetic fixtures only.
    runtime = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.0"
    target = ".NETCoreApp,Version=v10.0/win-x64"

    def setUp(self):
        SupplementalTests.setUp(self)
        self.asset = self.assets()
        self.publish = self.root / "publish"
        self.publish.mkdir()
        self.output = self.root / "publish-output"
        self.network_patch = patch("third_party.urllib.request.urlopen",
                                   side_effect=AssertionError("offline test attempted network"))
        self.network = self.network_patch.start()
        self.addCleanup(self.network_patch.stop)
        self.text_patch = patch.object(third_party, "fetch_spdx_text",
                                       return_value="SYNTHETIC SPDX TEST ONLY")
        self.text_patch.start()
        self.addCleanup(self.text_patch.stop)

    def deps(self, keys, filename="App.deps.json", target=None, entries=None, types=None):
        target = self.target if target is None else target
        types = types or {}
        libraries = {key: {"type": types.get(key, "runtimepack" if
                     key.casefold().startswith("runtimepack.") else "package")}
                     for key in keys}
        data = {"libraries": libraries,
                "runtimeTarget": {"name": target},
                "targets": {target: entries if entries is not None else {
                    key: {"runtime": {"fixture.dll": {}}} for key in keys}}}
        path = self.publish / filename
        path.write_text(json.dumps(data), encoding="utf-8-sig")
        return path

    def run_publish(self, supplemental=False, assets=None):
        stdout = io.StringIO()
        with patch("sys.stdout", stdout):
            third_party.generate(assets or [self.asset], self.output,
                                 self.manifest if supplemental else None, self.publish)
        sbom = json.loads((self.output / "sbom.cdx.json").read_text(encoding="utf-8-sig"))
        notice = (self.output / "THIRD_PARTY_NOTICES.md").read_text(encoding="utf-8-sig")
        return sbom, notice, stdout.getvalue()

    def test_release_filter_and_case_insensitive_multi_assets_deduplication(self):
        data = json.loads(self.asset.read_text(encoding="utf-8-sig"))
        # An excluded Debug-only package must not reach license policy evaluation.
        data["libraries"]["DebugOnly/1.0"] = dict(data["libraries"]["Fixture/1.0"])
        self.asset.write_text(json.dumps(data))
        other = self.root / "other.assets.json"
        other.write_text(json.dumps({**data, "libraries": {
            "FIXTURE/1.0": data["libraries"]["Fixture/1.0"]}}))
        self.deps(["fIxTuRe/1.0"])
        sbom, notice, _ = self.run_publish(assets=[self.asset, other])
        self.assertEqual([c["name"] for c in sbom["components"]], ["Fixture"])
        self.assertNotIn("DebugOnly", notice)
        self.assertNotIn("UNCOVERED", notice)

    def test_missing_ordinary_package_blocks_before_output_or_license_fetch(self):
        self.deps(["Fixture/1.0"])
        self.deps(["Missing/2.0", "Other/3.0"], filename="Plugin.deps.json")
        with patch.object(third_party, "fetch_spdx_text") as fetch:
            with self.assertRaisesRegex(ValueError, "missing from restored assets") as error:
                self.run_publish()
        for identity in ("Missing/2.0", "Other/3.0", "Plugin.deps.json"):
            self.assertIn(identity, str(error.exception))
        fetch.assert_not_called()
        self.assertFalse(self.output.exists())

    def test_supplemental_identity_cannot_replace_ordinary_assets(self):
        self.entry.update(name="Missing", version="2.0")
        self.write_inventory([self.entry])
        self.deps(["Fixture/1.0", "Missing/2.0"])
        with self.assertRaisesRegex(ValueError, "Missing/2.0"):
            self.run_publish(supplemental=True)
        self.assertFalse(self.output.exists())

    def test_runtime_gap_is_traceable_in_all_outputs_without_fake_component(self):
        path = self.deps(["Fixture/1.0", self.runtime])
        self.assertEqual(json.loads(path.read_text(encoding="utf-8-sig"))[
            "libraries"][self.runtime]["type"], "runtimepack")
        sbom, notice, stdout = self.run_publish(supplemental=True)
        gaps = [json.loads(p["value"]) for p in sbom["metadata"]["properties"]
                if p["name"] == "plinth:uncovered-publish-package"]
        self.assertEqual(len(gaps), 1)
        self.assertEqual(gaps[0]["package"], self.runtime)
        self.assertEqual(gaps[0]["deps"], "App.deps.json")
        self.assertEqual(gaps[0]["target"], self.target)
        self.assertIn("absent from restored assets", gaps[0]["reason"])
        self.assertEqual(len(sbom["components"]), 2)
        for text in (notice, stdout):
            self.assertIn("UNCOVERED: " + self.runtime, text)
            self.assertIn("App.deps.json", text)
            self.assertIn(self.target, text)
        self.assertFalse(any(c["name"].startswith("runtimepack.") for c in sbom["components"]))

    def runtime_evidence(self, *, name=None, category="runtime", version="10.0.0", notice=True):
        self.entry.update(name=name or self.runtime.rsplit("/", 1)[0], version=version,
                          category=category, license="MIT")
        if notice:
            self.entry["texts"].append({
                "kind": "NOTICE", "path": "NOTICE",
                "sha256": hashlib.sha256((self.evidence / "NOTICE").read_bytes()).hexdigest()})
        self.write_inventory([self.entry])
        return self.entry

    def prepare_runtime_pack(self, *, declaration="MIT", license_bytes=None,
                             notice_bytes=None, publish_file=True):
        package_id = "Microsoft.NETCore.App.Runtime.win-x64"
        pack = self.root / "packages" / package_id.lower() / "10.0.0"
        pack.mkdir(parents=True, exist_ok=True)
        (pack / (package_id.lower() + ".nuspec")).write_text(
            f'<package><metadata><id>{package_id}</id><version>10.0.0</version>'
            f'<license type="expression">{declaration}</license></metadata></package>',
            encoding="utf-8")
        (pack / "LICENSE.TXT").write_bytes(
            self.raw if license_bytes is None else license_bytes)
        (pack / "THIRD-PARTY-NOTICES.TXT").write_bytes(
            (self.evidence / "NOTICE").read_bytes() if notice_bytes is None else notice_bytes)
        if publish_file:
            (self.publish / "fixture.dll").write_bytes(b"synthetic runtime fixture")
        return pack

    def test_runtime_gap_reconciled_only_by_exact_explicit_evidence(self):
        self.runtime_evidence()
        self.prepare_runtime_pack()
        self.deps(["Fixture/1.0", self.runtime])
        sbom, notice, stdout = self.run_publish(supplemental=True)
        reconciled = [json.loads(p["value"]) for p in sbom["metadata"]["properties"]
                      if p["name"] == "plinth:reconciled-publish-package"]
        self.assertEqual([entry["package"] for entry in reconciled], [self.runtime])
        self.assertEqual(reconciled[0]["evidence"], ["urn:plinth:supplemental:fixture-runtime"])
        self.assertEqual(reconciled[0]["license"], "MIT")
        self.assertEqual(len(reconciled[0]["bundled_texts"]), 2)
        self.assertEqual([item["path"] for item in reconciled[0]["contents"]], ["fixture.dll"])
        self.assertIn("not a license/attribution review approval", reconciled[0]["reason"])
        self.assertFalse(any(p["name"] == "plinth:uncovered-publish-package"
                             for p in sbom["metadata"]["properties"]))
        report = json.loads((self.output / "coverage-report.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(report["uncovered_publish_packages"], [])
        self.assertEqual(len(report["reconciled_publish_packages"]), 1)
        self.assertFalse(report["distribution_inventory_complete"])
        self.assertFalse(report["license_review_approved"])
        self.assertIn("Reconciled SDK runtime packs: 1", report["coverage"])
        self.assertTrue(any(c["name"] == self.runtime.rsplit("/", 1)[0]
                            for c in sbom["components"]))
        for text in (notice, stdout):
            self.assertIn("RECONCILED: " + self.runtime, text)

    def test_runtime_reconciliation_fails_closed_without_exact_evidence(self):
        base = copy.deepcopy(self.entry)
        for label, overrides, notice in (
                ("missing notice", {}, False),
                ("wrong category", {"category": "native"}, True),
                ("wrong version", {"version": "10.0.1"}, True),
                ("wrong name", {"name": "runtimepack.Microsoft.NETCore.App.Runtime.win-arm64"}, True)):
            with self.subTest(label=label):
                self.entry = copy.deepcopy(base)
                self.runtime_evidence(notice=notice, **overrides)
                self.deps(["Fixture/1.0", self.runtime])
                sbom, notice_text, _ = self.run_publish(supplemental=True)
                self.assertIn("UNCOVERED: " + self.runtime, notice_text)
                self.assertFalse(any(p["name"] == "plinth:reconciled-publish-package"
                                     for p in sbom["metadata"]["properties"]))
                report = json.loads((self.output / "coverage-report.json").read_text(encoding="utf-8-sig"))
                self.assertEqual(report["reconciled_publish_packages"], [])
                self.assertEqual(len(report["uncovered_publish_packages"]), 1)

    def test_runtime_reconciliation_rejects_pack_or_publish_mismatch(self):
        base = copy.deepcopy(self.entry)
        for label, options in (
                ("missing pack", None),
                ("license mismatch", {"declaration": "Apache-2.0"}),
                ("license bytes mismatch", {"license_bytes": b"different license\n"}),
                ("notice bytes mismatch", {"notice_bytes": b"different notice\n"}),
                ("missing publish file", {"publish_file": False})):
            with self.subTest(label=label):
                self.entry = copy.deepcopy(base)
                self.runtime_evidence()
                pack = self.root / "packages/microsoft.netcore.app.runtime.win-x64/10.0.0"
                if pack.exists():
                    shutil.rmtree(pack)
                (self.publish / "fixture.dll").unlink(missing_ok=True)
                if options is not None:
                    self.prepare_runtime_pack(**options)
                self.deps(["Fixture/1.0", self.runtime])
                sbom, notice_text, _ = self.run_publish(supplemental=True)
                self.assertIn("UNCOVERED: " + self.runtime, notice_text)
                self.assertFalse(any(p["name"] == "plinth:reconciled-publish-package"
                                     for p in sbom["metadata"]["properties"]))
                report = json.loads((self.output / "coverage-report.json").read_text(encoding="utf-8-sig"))
                self.assertEqual(report["reconciled_publish_packages"], [])
                self.assertEqual(len(report["uncovered_publish_packages"]), 1)

    def test_runtime_boundaries_fail_closed(self):
        for key, target, entries in (
            (self.runtime.replace("runtimepack.", ""), self.target, None),
            (self.runtime.replace("NETCore", "Unreviewed"), self.target, None),
            (self.runtime + ".extra", self.target, {}),
            (self.runtime.replace("win-x64", "linux-x64"), self.target, None),
            (self.runtime, ".NETCoreApp,Version=v10.0", None),
            (self.runtime, self.target, {}),
            (self.runtime, self.target, {self.runtime: {"runtime": {}}}),
        ):
            with self.subTest(key=key, target=target, entries=entries):
                self.deps(["Fixture/1.0", key], target=target, entries=entries,
                          types={key: "runtimepack"})
                with self.assertRaisesRegex(ValueError, "missing from restored assets"):
                    self.run_publish()
                self.assertFalse(self.output.exists())

    def test_package_cannot_claim_runtimepack_exception(self):
        for key in (self.runtime, self.runtime.replace("NETCore", "WindowsDesktop")):
            with self.subTest(key=key):
                self.deps(["Fixture/1.0", key], types={key: "package"})
                with patch.object(third_party, "fetch_spdx_text") as fetch:
                    with self.assertRaisesRegex(ValueError, "missing from restored assets"):
                        self.run_publish()
                fetch.assert_not_called()
                self.assertFalse(self.output.exists())

    def test_runtime_entries_in_inactive_target_do_not_qualify(self):
        path = self.deps(["Fixture/1.0", self.runtime], entries={})
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        data["targets"][".NETCoreApp,Version=v10.0/other-rid"] = {
            self.runtime: {"runtime": {"fixture.dll": {}}}}
        path.write_text(json.dumps(data), encoding="utf-8-sig")
        with self.assertRaisesRegex(ValueError, "missing from restored assets"):
            self.run_publish()
        self.assertFalse(self.output.exists())

    def test_runtime_case_and_windowsdesktop_native_target(self):
        key = self.runtime.replace("NETCore", "WindowsDesktop").upper()
        self.deps(["Fixture/1.0", key], entries={
            key.lower(): {"native": {"fixture.dll": {}}}})
        sbom, notice, _ = self.run_publish()
        self.assertEqual(len(sbom["components"]), 1)
        self.assertIn("UNCOVERED: " + key, notice)

    def test_runtime_with_real_assets_uses_normal_license_validation(self):
        data = json.loads(self.asset.read_text(encoding="utf-8-sig"))
        data["libraries"][self.runtime] = dict(data["libraries"]["Fixture/1.0"])
        self.asset.write_text(json.dumps(data))
        self.deps(["Fixture/1.0", self.runtime])
        # Pointing at an unrelated restored package cannot fabricate coverage.
        with self.assertRaisesRegex(ValueError, "NuSpec identity mismatch"):
            self.run_publish()
        self.assertFalse(self.output.exists())

    def test_runtime_with_matching_restored_identity_has_no_gap(self):
        data = json.loads(self.asset.read_text(encoding="utf-8-sig"))
        library = data["libraries"].pop("Fixture/1.0")
        data["libraries"][self.runtime] = library
        self.asset.write_text(json.dumps(data))
        name, version = self.runtime.rsplit("/", 1)
        nuspec = self.root / "packages/fixture/1.0/fixture.nuspec"
        nuspec.write_text(
            f'<package><metadata><id>{name}</id><version>{version}</version>'
            '<license type="expression">MIT</license></metadata></package>')
        self.deps([self.runtime])
        sbom, notice, _ = self.run_publish()
        self.assertEqual(sbom["components"][0]["name"], name)
        self.assertEqual(sbom["components"][0]["version"], version)
        self.assertEqual(sbom["components"][0]["licenses"], [{"license": {"id": "MIT"}}])
        self.assertNotIn("UNCOVERED", notice)
        self.assertFalse(any(p["name"] == "plinth:uncovered-publish-package"
                             for p in sbom["metadata"]["properties"]))

    def test_missing_restored_directory_is_not_a_runtime_exception(self):
        data = json.loads(self.asset.read_text(encoding="utf-8-sig"))
        data["libraries"][self.runtime] = {
            "type": "package", "path": "missing/10.0.0", "sha512": ""}
        self.asset.write_text(json.dumps(data))
        self.deps(["Fixture/1.0", self.runtime])
        with self.assertRaisesRegex(ValueError, "Missing restored package"):
            self.run_publish()

    def test_empty_or_missing_deps_blocks(self):
        for write_deps in (False, True):
            if write_deps:
                self.deps([])
            with self.assertRaisesRegex(ValueError, "No packaged dependencies"):
                self.run_publish()

    def test_runtime_only_publish_keeps_explicit_gap(self):
        self.deps([self.runtime])
        sbom, notice, _ = self.run_publish()
        self.assertEqual(sbom["components"], [])
        self.assertIn("UNCOVERED: " + self.runtime, notice)

    def test_cli_missing_package_is_blocked_offline(self):
        self.deps(["Fixture/1.0", "Missing/2.0"])
        result = subprocess.run([
            sys.executable, str(ROOT / "scripts/third_party.py"),
            "--assets", str(self.asset), "--publish-directory", str(self.publish),
            "--output", str(self.output)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn("BLOCKED: Publish dependencies missing from restored assets", result.stderr)
        self.assertIn("Missing/2.0", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()

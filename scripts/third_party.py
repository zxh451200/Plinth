"""Generate notices/SBOM from restored NuGet assets and optional explicit evidence."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import urllib.request
import xml.etree.ElementTree as ET
from supplemental_compliance import load_supplemental

ROOT = Path(__file__).resolve().parents[1]
ALLOWED = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD",
           "OFL-1.1", "Unlicense", "CC0-1.0", "PostgreSQL"}
# PostgreSQL policy reviewed against the pinned Npgsql 9.0.3 / EFCore.PG 9.0.4
# upstream LICENSE and NuGet nuspec; sources and limits: docs/RELEASE_COMPLIANCE.md.
# Generation still requires each restored package's actual declaration and text.
# Manually reviewed file-licensed packages, pinned to exact version. The bundled LICENSE
# is read verbatim: Avalonia's ANGLE natives ship the standard BSD-3-Clause text.
EXPLICIT_FILE_LICENSES = {
    ("Avalonia.Angle.Windows.Natives", "2.1.27548.20260419"): "BSD-3-Clause",
}
SPDX_SOURCE = "https://raw.githubusercontent.com/spdx/license-list-data/v3.26.0/text/"
SPDX_CACHE = Path.home() / ".cache" / "plinth-spdx"


def fetch_spdx_text(license_id):
    """Use only the pinned upstream or its isolated cache; fail if neither is available."""
    # The URL includes the pinned release. Changing it must never reuse another
    # release's text, or the legacy unversioned <SPDX_CACHE>/<license_id>.txt.
    source_key = hashlib.sha256(SPDX_SOURCE.encode("utf-8")).hexdigest()
    cache_directory = SPDX_CACHE / source_key
    cache_directory.mkdir(parents=True, exist_ok=True)
    cached = cache_directory / f"{license_id}.txt"
    if cached.is_file():
        text = cached.read_text(encoding="utf-8")
        if text.strip():
            return text
    url = SPDX_SOURCE + license_id + ".txt"
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            text = response.read().decode("utf-8")
        if not text.strip():
            raise ValueError("empty text")
    except (OSError, ValueError) as error:
        raise ValueError(f"SPDX text unavailable: {license_id} ({url}: {error})") from error
    cached.write_text(text, encoding="utf-8")
    return text


def inside(base, relative):
    result = (base / relative).resolve()
    if base.resolve() not in result.parents:
        raise ValueError(f"Package path escapes root: {relative}")
    return result


def publish_inventory(packages, publish_directory):
    """Check all publish packages before filtering; SDK exceptions are coverage gaps.

    Only recognize SDK-generated desktop runtimepack identities with a matching
    active RID target and runtime/native entries. This never approves a license.
    """
    restored = {f"{name}/{version}".casefold() for name, version in packages}
    shipped, missing, gaps = set(), [], []
    for deps in sorted(Path(publish_directory).glob("*.deps.json")):
        data = json.loads(deps.read_text(encoding="utf-8-sig"))
        target_name = data.get("runtimeTarget", {}).get("name", "")
        rid = target_name.rsplit("/", 1)[1] if "/" in target_name else ""
        target = {key.casefold(): value for key, value in
                  data.get("targets", {}).get(target_name, {}).items()}
        for key, library in data.get("libraries", {}).items():
            library_type = library.get("type")
            if library_type not in {"package", "runtimepack"}:
                continue
            shipped.add(key.casefold())
            if key.casefold() in restored:
                continue
            name, version = key.rsplit("/", 1)
            runtime_name = re.fullmatch(
                r"runtimepack\.Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.([A-Za-z0-9.-]+)",
                name, re.I)
            entry = target.get(key.casefold(), {})
            if (library_type == "runtimepack" and runtime_name and version and rid
                    and runtime_name[1].casefold() == rid.casefold()
                    and (entry.get("runtime") or entry.get("native"))):
                gaps.append({
                    "package": key, "deps": deps.name, "target": target_name,
                    "reason": "SDK runtime pack present in publish deps but absent from restored assets; "
                              "license/native contents not covered by the NuGet inventory. "
                              "Supplemental evidence is listed separately; no automatic coverage inferred.",
                })
            else:
                missing.append(f"{key} ({deps.name})")
    if not shipped:
        raise ValueError("No packaged dependencies found in publish output")
    if missing:
        raise ValueError("Publish dependencies missing from restored assets: " + "; ".join(sorted(missing)))
    return ({key: value for key, value in packages.items()
             if f"{key[0]}/{key[1]}".casefold() in shipped}, gaps)


def runtimepack_evidence(location, component, package_id, version):
    """Match supplied texts to the pack's nuspec declaration and bundled files."""
    nuspecs = list(location.glob("*.nuspec"))
    if len(nuspecs) != 1:
        return None, "Expected one runtime pack nuspec"
    metadata = ET.parse(nuspecs[0]).getroot()
    metadata_nodes = [node for node in metadata
                      if node.tag.rsplit("}", 1)[-1] == "metadata"]
    if len(metadata_nodes) != 1:
        return None, "Expected one runtime pack nuspec metadata element"
    fields = {}
    for node in metadata_nodes[0]:
        field = node.tag.rsplit("}", 1)[-1]
        if field in fields:
            return None, f"Duplicate runtime pack nuspec field: {field}"
        fields[field] = node
    declared_name, declared_version = fields.get("id"), fields.get("version")
    if (declared_name is None or declared_version is None
            or not (declared_name.text or "").strip()
            or not (declared_version.text or "").strip()
            or declared_name.text.casefold() != package_id.casefold()
            or declared_version.text.casefold() != version.casefold()):
        return None, "Runtime pack nuspec identity does not match the published identity"
    license_node = fields.get("license")
    if (license_node is None or license_node.attrib.get("type") != "expression"
            or not (license_node.text or "").strip()):
        return None, "Runtime pack nuspec has no license expression"
    declaration = license_node.text.strip()
    supplied = component["licenses"][0]["license"]["name"]
    if declaration.casefold() != supplied.casefold():
        return None, (f"Runtime pack nuspec license {declaration!r} does not match "
                      f"supplied declaration {supplied!r}")
    supplied_hashes = set()
    for prop in component["properties"]:
        if prop["name"] == "plinth:supplemental-evidence":
            evidence = json.loads(prop["value"])
            supplied_hashes.add((evidence["sha256"], evidence["kind"]))
    bundled = []
    for file in sorted(location.iterdir()):
        if not file.is_file():
            continue
        if re.match(r"^(license|licence)([._-]|$)", file.name, re.I):
            kind = "LICENSE"
        elif re.match(r"^(notice)([._-]|$)|^third[- ]party[- ]notices", file.name, re.I):
            kind = "NOTICE"
        else:
            continue
        digest = hashlib.sha256(file.read_bytes()).hexdigest()
        if (digest, kind) not in supplied_hashes:
            return None, (f"Bundled runtime pack {kind} {file.name} is not the supplied "
                          "evidence (hash or kind mismatch)")
        bundled.append({"kind": kind, "path": file.name, "sha256": digest})
    if not any(item["kind"] == "LICENSE" for item in bundled):
        return None, "Runtime pack has no bundled LICENSE file"
    if not any(item["kind"] == "NOTICE" for item in bundled):
        return None, "Runtime pack has no bundled NOTICE file"
    return {"declaration": declaration, "texts": bundled}, None


def published_contents(gap, publish_directory):
    """Hash every runtime/native file the runtime pack contributes to publish."""
    try:
        data = json.loads((Path(publish_directory) / gap["deps"]).read_text(encoding="utf-8-sig"))
        target = {key.casefold(): value for key, value in
                  data["targets"][gap["target"]].items()}
        entry = target[gap["package"].casefold()]
    except (OSError, KeyError, TypeError, ValueError) as error:
        return None, f"Could not read runtime pack deps entry: {error}"
    names = sorted(set(entry.get("runtime", {})) | set(entry.get("native", {})))
    if not names:
        return None, "Runtime pack deps entry lists no runtime/native files"
    contents = []
    for name in names:
        try:
            file = inside(Path(publish_directory), name)
        except ValueError:
            return None, f"Published runtime pack file escapes publish directory: {name}"
        if not file.is_file() or file.stat().st_size == 0:
            return None, f"Published runtime pack file is missing or empty: {name}"
        contents.append({"path": name,
                         "sha256": hashlib.sha256(file.read_bytes()).hexdigest()})
    return contents, None


def reconcile_runtime_gaps(gaps, supplemental_components, package_folders, publish_directory):
    """Reconcile runtime-pack gaps against package and published-file evidence.

    Only exact identity/version, category runtime, LICENSE and NOTICE texts, the
    pack's nuspec declaration, bundled license/notice bytes and every published
    runtime/native file reconcile a gap. Anything else remains uncovered.
    """
    index = {}
    for component in supplemental_components:
        properties = component["properties"]
        category = next(p["value"] for p in properties
                        if p["name"] == "plinth:supplemental-category")
        kinds = {json.loads(p["value"])["kind"] for p in properties
                 if p["name"] == "plinth:supplemental-evidence"}
        index[(component["name"].casefold(), component["version"].casefold())] = (
            component, category, kinds)
    reconciled, uncovered = [], []
    for gap in gaps:
        name, version = gap["package"].rsplit("/", 1)
        match = index.get((name.casefold(), version.casefold()))
        if not (match and match[1] == "runtime" and {"LICENSE", "NOTICE"} <= match[2]):
            uncovered.append(gap)
            continue
        component = match[0]
        package_id = re.sub(r"^runtimepack\.", "", name, flags=re.I)
        location = next((folder / package_id.lower() / version for folder in package_folders
                         if (folder / package_id.lower() / version).is_dir()), None)
        if location is None:
            uncovered.append(dict(gap, reason=gap["reason"] + (
                " Runtime pack package directory was not found for evidence reconciliation.")))
            continue
        evidence, error = runtimepack_evidence(location, component, package_id, version)
        if error:
            uncovered.append(dict(gap, reason=gap["reason"] + " " + error + "."))
            continue
        contents, error = published_contents(gap, publish_directory)
        if error:
            uncovered.append(dict(gap, reason=gap["reason"] + " " + error + "."))
            continue
        reconciled.append(dict(
            gap, evidence=[component["bom-ref"]], license=evidence["declaration"],
            bundled_texts=evidence["texts"], contents=contents, reason=(
                "SDK runtime pack reconciled against the package nuspec, bundled "
                f"LICENSE/NOTICE and {len(contents)} published runtime/native files; "
                "not a license/attribution review approval.")))
    return reconciled, uncovered


def file_evidence(path, logical_path):
    """Identify original evidence bytes; text hashes are labelled separately."""
    path = Path(path)
    return {"path": logical_path,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def generate(assets, output, supplemental_file=None, publish_directory=None):
    assets = [Path(asset) for asset in assets]
    # Input ordinals refer to the supplied assets order, not host directory names.
    # Hashes identify original bytes (including any BOM/newlines), never rewritten inputs.
    asset_paths = [f"inputs/assets/{index}/project.assets.json"
                   for index in range(1, len(assets) + 1)]
    supplemental_notices, supplemental_components = (
        load_supplemental(supplemental_file) if supplemental_file is not None else ([], []))
    inputs = {"assets": [file_evidence(asset, logical) for asset, logical in zip(assets, asset_paths)],
              "publish_deps": [file_evidence(path, f"publish/{path.name}") for path in sorted(
                  Path(publish_directory).glob("*.deps.json"))] if publish_directory is not None else [],
              "supplemental": file_evidence(supplemental_file, "inputs/supplemental/inventory.json") if supplemental_file is not None else None}
    package_sources = {}
    packages = {}
    asset_identities = {}
    package_folders = set()
    for asset, asset_path in zip(assets, asset_paths):
        data = json.loads(asset.read_text(encoding="utf-8-sig"))
        folders = [Path(p) for p in data["packageFolders"]]
        package_folders.update(folders)
        for key, library in data["libraries"].items():
            if library["type"] != "package":
                continue
            name, version = key.rsplit("/", 1)
            location = next((inside(folder, library["path"]) for folder in folders
                             if inside(folder, library["path"]).is_dir()), None)
            if location is None:
                raise ValueError(f"Missing restored package: {key}")
            identity = (name.casefold(), version.casefold())
            original = asset_identities.setdefault(identity, (name, version))
            packages[original] = (location, library.get("sha512"))
            package_sources.setdefault(original, []).append(asset_path)
    if not packages:
        raise ValueError("No restored package inventory")
    # Only components actually copied to the publish output ship to users. project.assets.json
    # also lists Debug-only packages that Release publish excludes.
    restored_identities = set(packages)
    gaps, reconciled_gaps = [], []
    if publish_directory is not None:
        packages, gaps = publish_inventory(packages, publish_directory)
        reconciled_gaps, gaps = reconcile_runtime_gaps(
            gaps, supplemental_components, package_folders, publish_directory)
    coverage = (
        "Coverage: restored NuGet inventory plus explicitly supplied supplemental components only. "
        "Not a complete distribution inventory or a compliance/review approval. "
        "SDK runtime packs without matching explicit evidence, and unlisted native code, "
        "fonts/icons and NSIS remain outside coverage."
        if supplemental_file is not None else
        "Coverage: NuGet-only candidate; no supplemental evidence supplied. "
        "SDK runtime packs, native code, fonts/icons and NSIS remain outside coverage. "
        "Not a complete distribution inventory or a compliance/review approval.")
    if gaps:
        coverage += f" Explicit publish runtime coverage gaps: {len(gaps)} (see plinth:uncovered-publish-package)."
    if reconciled_gaps:
        coverage += (f" Reconciled SDK runtime packs: {len(reconciled_gaps)} with explicit supplemental "
                     "evidence (see plinth:reconciled-publish-package); not a license review approval.")
    gap_lines = [f"UNCOVERED: {gap['package']} (deps: {gap['deps']}; target: {gap['target']}): "
                 f"{gap['reason']}" for gap in gaps]
    reconciled_lines = [f"RECONCILED: {gap['package']} (deps: {gap['deps']}; target: {gap['target']}; "
                        f"evidence: {', '.join(gap['evidence'])}): {gap['reason']}"
                        for gap in reconciled_gaps]
    notices = ["# Third-party notices", "",
               "Generated from project.assets.json and restored .nuspec files.",
               "Scope: NuGet packages (including build/test dependencies if supplied).",
               coverage, "Evidence index and pending materials: coverage-report.json", ""]
    if gaps:
        notices.extend(["## Uncovered publish SDK runtime packs", "", *gap_lines, ""])
    if reconciled_gaps:
        notices.extend(["## Reconciled publish SDK runtime packs", "", *reconciled_lines, ""])
    components = []
    package_reports = []
    text_cache = {}
    for (name, version), (location, integrity) in sorted(packages.items()):
        nuspecs = list(location.glob("*.nuspec"))
        if len(nuspecs) != 1:
            raise ValueError(f"Expected one nuspec: {name}/{version}")
        metadata = ET.parse(nuspecs[0]).getroot()
        # Read direct metadata children only: dependency/extension elements must
        # never override the package's own identity or attribution.
        metadata_nodes = [node for node in metadata
                          if node.tag.rsplit("}", 1)[-1] == "metadata"]
        if len(metadata_nodes) != 1:
            raise ValueError(f"Expected one nuspec metadata element: {name}/{version}")
        fields = {}
        for node in metadata_nodes[0]:
            field = node.tag.rsplit("}", 1)[-1]
            if field in fields:
                raise ValueError(f"Duplicate nuspec metadata field: {name}/{version}: {field}")
            fields[field] = node
        copyright_node = fields.get("copyright")
        copyright_text = copyright_node.text if copyright_node is not None else None
        copyright_state = ("missing" if copyright_node is None else
                           "empty" if not (copyright_text or "").strip() else "present")
        declared_name = fields.get("id")
        declared_version = fields.get("version")
        if declared_name is None or declared_version is None:
            raise ValueError(f"Missing package identity: {name}/{version}")
        if declared_name.text.lower() != name.lower() or declared_version.text.lower() != version.lower():
            raise ValueError(f"NuSpec identity mismatch: {name}/{version}")
        license_node = fields.get("license")
        if license_node is None or not license_node.text:
            raise ValueError(f"No explicit NuGet license metadata: {name}/{version}")
        declaration = license_node.text.strip()
        if license_node.attrib.get("type") != "expression":
            selected = EXPLICIT_FILE_LICENSES.get((declared_name.text, declared_version.text))
            if selected is None or selected not in ALLOWED:
                raise ValueError(f"File/legacy license requires explicit manual policy review: {name}/{version}: {declaration}")
            license_file = inside(location, declaration)
            if not license_file.is_file():
                raise ValueError(f"Missing bundled license text: {name}/{version}: {declaration}")
            text = license_file.read_text(encoding="utf-8-sig")
            if not text.strip():
                raise ValueError(f"Empty bundled license text: {name}/{version}")
            text_source = f"bundled:{license_file.relative_to(location).as_posix()}"
        else:
            # Only simple identifiers or OR alternatives; never silently approve AND/WITH.
            if not re.fullmatch(r"[A-Za-z0-9.+-]+(?: OR [A-Za-z0-9.+-]+)*", declaration):
                raise ValueError(f"Complex license expression requires manual review: {name}/{version}: {declaration}")
            choices = re.split(r"\s+OR\s+", declaration.strip("() "))
            selected = next((c.strip("() ") for c in choices if c.strip("() ") in ALLOWED), None)
            if not selected:
                raise ValueError(f"Unapproved license expression: {name}/{version}: {declaration}")
            if selected not in text_cache:
                text_cache[selected] = fetch_spdx_text(selected)
            text = text_cache[selected]
            text_source = f"{SPDX_SOURCE}{selected}.txt"
        notices.extend([f"## {name} {version}", "",
                        f"Declared: `{declaration}`; selected: `{selected}`.",
                        f"License text source: {text_source}",
                        f"License text SHA256: {hashlib.sha256(text.encode()).hexdigest()}", "",
                        text, ""])
        if copyright_state == "present":
            notices.extend(["### NuSpec copyright", "", copyright_text, ""])
        else:
            notices.extend([f"NuSpec copyright: {copyright_state}; attribution requires evidence review.", ""])
        package_root = f"nuget/{name}/{version}"
        bundled_evidence = []
        # Preserve package-specific copyright / native component notices verbatim.
        for file in sorted(location.rglob("*")):
            if file.is_file() and re.match(r"^(license|licence|notice|copyright)([._-]|$)", file.name, re.I):
                bundled_evidence.append(file_evidence(file, f"{package_root}/{file.relative_to(location).as_posix()}"))
                content = file.read_text(encoding="utf-8-sig")  # invalid text fails visibly
                notices.extend([f"### Bundled text: {file.relative_to(location)}", "", content, ""])
        component = {
            "type": "library", "name": name, "version": version,
            "bom-ref": f"pkg:nuget/{name}@{version}", "purl": f"pkg:nuget/{name}@{version}",
            "licenses": [{"license": {"id": selected}}],
            "properties": [{"name": "plinth:declared-license", "value": declaration}],
        }
        if copyright_state == "present":
            component["copyright"] = copyright_text
        package_reports.append({
            "bom-ref": component["bom-ref"], "name": name, "version": version,
            "assets": sorted(set(package_sources[(name, version)])),
            "nuspec": file_evidence(nuspecs[0], f"{package_root}/{nuspecs[0].relative_to(location).as_posix()}"),
            "copyright": {"status": copyright_state, "text": copyright_text},
            "license": {"declared": declaration, "selected": selected,
                        "source": text_source, "text_sha256": hashlib.sha256(text.encode()).hexdigest()},
            "bundled_texts": bundled_evidence,
            "pending_materials": (["Locate upstream copyright/attribution evidence and check bundled texts; "
                                   "missing nuspec copyright does not establish a license violation."]
                                  if copyright_state != "present" else []) + [
                "Review package-specific attribution and bundled/native notices against shipped contents; "
                "SPDX template text alone does not establish completeness."],
        })
        if not integrity:
            raise ValueError(f"Missing restored package SHA512: {name}/{version}")
        raw_hash = base64.b64decode(integrity[7:] if integrity.startswith("sha512-") else integrity,
                                   validate=True)
        if len(raw_hash) != 64:
            raise ValueError(f"Invalid package SHA512: {name}/{version}")
        component["hashes"] = [{"alg": "SHA-512", "content": raw_hash.hex()}]
        components.append(component)
    identities = {(name.casefold(), version.casefold()) for name, version in packages}
    for component in supplemental_components:
        if (component["name"].casefold(), component["version"].casefold()) in identities:
            raise ValueError(f"Duplicate NuGet/supplemental identity: {component['name']}/{component['version']}")
    notices.extend(supplemental_notices)
    nuget_count = len(components)
    components.extend(supplemental_components)
    supplemental_reports = []
    for component in supplemental_components:
        properties = component["properties"]
        supplemental_reports.append({
            "bom-ref": component["bom-ref"], "name": component["name"], "version": component["version"],
            "category": next(p["value"] for p in properties if p["name"] == "plinth:supplemental-category"),
            "source": component["externalReferences"][0]["url"],
            "texts": [{key: value for key, value in json.loads(p["value"]).items()
                       if key in {"kind", "path", "sha256"}}
                      for p in properties if p["name"] == "plinth:supplemental-evidence"],
        })
    report = {
        "schema_version": 1, "coverage": coverage,
        "distribution_inventory_complete": False, "license_review_approved": False,
        "inputs": inputs,
        "evidence_paths": {
            "inputs/assets": "Supplied assets in command-line order (1-based); SHA256 of original bytes.",
            "inputs/supplemental": "Explicit supplemental inventory; text paths relative to its directory.",
            "publish": "Paths relative to the publish directory.",
            "nuget": "NuGet package name/version followed by path relative to the restored package root.",
        },
        "scope": "publish deps package identities" if publish_directory is not None else "restored assets (may include build/test packages)",
        "excluded_restored_packages": [f"{name}/{version}" for name, version in sorted(restored_identities - set(packages))],
        "nuget_packages": package_reports, "supplemental_components": supplemental_reports,
        "uncovered_publish_packages": [dict(gap, pending_materials=[
            "Supply exact SDK runtime version/RID distribution inventory, upstream LICENSE/NOTICE "
            "and native component attribution; manually reconcile supplemental evidence."])
            for gap in gaps],
        "reconciled_publish_packages": [dict(gap, pending_materials=[
            "Human license/attribution review remains required; reconciliation only links "
            "explicitly supplied evidence."])
            for gap in reconciled_gaps],
        # Categories are checks still to perform, never fabricated inventory items.
        "pending_scope_checks": [{
            "category": category, "status": "not-established",
            "supplied_component_refs": [entry["bom-ref"] for entry in supplemental_reports
                                        if entry["category"] == category],
            "pending_materials": "Reconcile actual distribution contents and exact versions with upstream "
                                 "LICENSE/NOTICE/attribution evidence, or record evidenced non-applicability.",
        } for category in ("runtime", "native", "font", "icon", "nsis")],
        "pending_materials": (["Supply publish output deps.json to restrict restored packages to shipped package identities."]
                              if publish_directory is None else []) + [
            "Reconcile complete release/installer contents; deps.json is not a full file or native component inventory.",
            "Record human license/attribution review; generation and evidence hashes are not approval."],
    }
    output.mkdir(parents=True, exist_ok=True)
    (output / "coverage-report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    (output / "THIRD_PARTY_NOTICES.md").write_text("\n".join(notices), encoding="utf-8")
    (output / "sbom.cdx.json").write_text(json.dumps({
        "bomFormat": "CycloneDX", "specVersion": "1.5", "version": 1,
        "metadata": {"properties": [{"name": "plinth:coverage", "value": coverage},
                                     {"name": "plinth:coverage-report", "value": "coverage-report.json"}] + [
            {"name": "plinth:reconciled-publish-package", "value": json.dumps(gap, sort_keys=True)}
            for gap in reconciled_gaps] + [
            {"name": "plinth:uncovered-publish-package", "value": json.dumps(gap, sort_keys=True)}
            for gap in gaps]},
        "components": components,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Generated notices/SBOM for {nuget_count} restored NuGet packages "
          f"and {len(supplemental_components)} supplemental components")
    print(coverage)
    for line in reconciled_lines:
        print(line)
    for line in gap_lines:
        print(line)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", action="append", type=Path,
                        help="Repeat for multiple projects; defaults to src/tests obj assets")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--supplemental-compliance-file", type=Path,
                        help="Explicit supplemental inventory JSON; text paths relative to its directory")
    parser.add_argument("--publish-directory", type=Path,
                        help="Restrict the inventory to packages present in the publish deps.json")
    args = parser.parse_args()
    assets = args.assets or sorted(
        path for base in ("src", "tests") for path in (ROOT / base).rglob("obj/project.assets.json"))
    try:
        if not assets:
            raise ValueError("No project.assets.json; perform a real locked restore first")
        generate(assets, args.output, args.supplemental_compliance_file, args.publish_directory)
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        parser.exit(1, f"BLOCKED: {error}\n")

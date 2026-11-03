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
           "OFL-1.1", "Unlicense", "CC0-1.0"}
# Manually reviewed file-licensed packages, pinned to exact version. The bundled LICENSE
# is read verbatim: Avalonia's ANGLE natives ship the standard BSD-3-Clause text.
EXPLICIT_FILE_LICENSES = {
    ("Avalonia.Angle.Windows.Natives", "2.1.27548.20260419"): "BSD-3-Clause",
}
SPDX_SOURCE = "https://raw.githubusercontent.com/spdx/license-list-data/v3.26.0/text/"
SPDX_FALLBACK = "https://spdx.org/licenses/"
SPDX_CACHE = Path.home() / ".cache" / "plinth-spdx"


def fetch_spdx_text(license_id):
    """Fetch SPDX text with a local cache and a second source; network flakiness must not block packaging."""
    SPDX_CACHE.mkdir(parents=True, exist_ok=True)
    cached = SPDX_CACHE / f"{license_id}.txt"
    if cached.is_file() and cached.stat().st_size > 0:
        return cached.read_text(encoding="utf-8")
    errors = []
    for url in (SPDX_SOURCE + license_id + ".txt", SPDX_FALLBACK + license_id + ".txt"):
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                text = response.read().decode("utf-8")
            if text.strip():
                cached.write_text(text, encoding="utf-8")
                return text
            errors.append(f"{url}: empty")
        except (OSError, ValueError) as error:
            errors.append(f"{url}: {error}")
    raise ValueError(f"SPDX text unavailable: {license_id} ({'; '.join(errors)})")


def inside(base, relative):
    result = (base / relative).resolve()
    if base.resolve() not in result.parents:
        raise ValueError(f"Package path escapes root: {relative}")
    return result


def generate(assets, output, supplemental_file=None, publish_directory=None):
    supplemental_notices, supplemental_components = (
        load_supplemental(supplemental_file) if supplemental_file is not None else ([], []))
    packages = {}
    for asset in assets:
        data = json.loads(asset.read_text(encoding="utf-8-sig"))
        folders = [Path(p) for p in data["packageFolders"]]
        for key, library in data["libraries"].items():
            if library["type"] != "package":
                continue
            name, version = key.rsplit("/", 1)
            location = next((inside(folder, library["path"]) for folder in folders
                             if inside(folder, library["path"]).is_dir()), None)
            if location is None:
                raise ValueError(f"Missing restored package: {key}")
            packages[(name, version)] = (location, library.get("sha512"))
    if not packages:
        raise ValueError("No restored package inventory")
    # Only components actually copied to the publish output ship to users. project.assets.json
    # also lists Debug-only packages that Release publish excludes.
    if publish_directory is not None:
        shipped = set()
        for deps in Path(publish_directory).glob("*.deps.json"):
            data = json.loads(deps.read_text(encoding="utf-8-sig"))
            for key, library in data.get("libraries", {}).items():
                if library.get("type") == "package":
                    shipped.add(key.casefold())
        if not shipped:
            raise ValueError("No packaged dependencies found in publish output")
        packages = {key: value for key, value in packages.items()
                    if f"{key[0]}/{key[1]}".casefold() in shipped}
        if not packages:
            raise ValueError("Publish dependency filter removed every package")
    coverage = (
        "Coverage: restored NuGet inventory plus explicitly supplied supplemental components only. "
        "Not a complete distribution inventory or a compliance/review approval. "
        "Unlisted SDK runtime packs, native code, fonts/icons and NSIS remain outside coverage."
        if supplemental_file is not None else
        "Coverage: NuGet-only candidate; no supplemental evidence supplied. "
        "SDK runtime packs, native code, fonts/icons and NSIS remain outside coverage. "
        "Not a complete distribution inventory or a compliance/review approval.")
    notices = ["# Third-party notices", "",
               "Generated from project.assets.json and restored .nuspec files.",
               "Scope: NuGet packages (including build/test dependencies if supplied).",
               coverage, ""]
    components = []
    text_cache = {}
    for (name, version), (location, integrity) in sorted(packages.items()):
        nuspecs = list(location.glob("*.nuspec"))
        if len(nuspecs) != 1:
            raise ValueError(f"Expected one nuspec: {name}/{version}")
        metadata = ET.parse(nuspecs[0]).getroot()
        fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata.iter()}
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
        # Preserve package-specific copyright / native component notices verbatim.
        for file in sorted(location.rglob("*")):
            if file.is_file() and re.match(r"^(license|licence|notice|copyright)([._-]|$)", file.name, re.I):
                content = file.read_text(encoding="utf-8-sig")  # invalid text fails visibly
                notices.extend([f"### Bundled text: {file.relative_to(location)}", "", content, ""])
        component = {
            "type": "library", "name": name, "version": version,
            "bom-ref": f"pkg:nuget/{name}@{version}", "purl": f"pkg:nuget/{name}@{version}",
            "licenses": [{"license": {"id": selected}}],
            "properties": [{"name": "plinth:declared-license", "value": declaration}],
        }
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
    output.mkdir(parents=True, exist_ok=True)
    (output / "THIRD_PARTY_NOTICES.md").write_text("\n".join(notices), encoding="utf-8")
    (output / "sbom.cdx.json").write_text(json.dumps({
        "bomFormat": "CycloneDX", "specVersion": "1.5", "version": 1,
        "metadata": {"properties": [{"name": "plinth:coverage", "value": coverage}]},
        "components": components,
    }, indent=2) + "\n", encoding="utf-8")
    print(f"Generated notices/SBOM for {nuget_count} restored NuGet packages "
          f"and {len(supplemental_components)} supplemental components")
    print(coverage)


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

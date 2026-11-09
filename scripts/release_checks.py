"""Fail closed on missing real locks; verify distributable content and checksums."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def locks():
    projects = sorted(p for base in ("src", "tests", "tools", "samples")
                      for p in (ROOT / base).rglob("*.csproj"))
    if not projects:
        raise ValueError("No projects found")
    for project in projects:
        lock = project.parent / "packages.lock.json"
        if not lock.is_file():
            raise ValueError(f"Missing {lock}; run a real dotnet restore and commit generated locks")
        data = json.loads(lock.read_text(encoding="utf-8-sig"))
        if not data.get("dependencies"):
            raise ValueError(f"Empty dependency lock: {lock}")
    print(f"Validated locks for {len(projects)} projects; locked restore must verify hashes")


def coverage_report(data):
    """Validate the public evidence index, without claiming license approval."""
    def require(condition, label):
        if not condition:
            raise ValueError(f"Invalid coverage-report.json: {label}")

    def evidence(item):
        require(isinstance(item, dict), "evidence object")
        path, digest = item.get("path"), item.get("sha256")
        require(isinstance(path, str) and bool(path) and not PurePosixPath(path).is_absolute()
                and not PureWindowsPath(path).drive and "\\" not in path
                and not any(part in {"", ".", ".."} for part in path.split("/")), "logical evidence path")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest), "evidence SHA256")

    require(isinstance(data, dict), "object required")
    require(type(data.get("schema_version")) is int and data["schema_version"] == 1, "schema_version")
    for field in ("coverage", "scope"):
        require(isinstance(data.get(field), str) and bool(data[field].strip()), field)
    for field in ("distribution_inventory_complete", "license_review_approved"):
        require(type(data.get(field)) is bool, field)
    for field in ("nuget_packages", "supplemental_components", "uncovered_publish_packages",
                  "reconciled_publish_packages", "excluded_restored_packages",
                  "pending_scope_checks", "pending_materials"):
        require(isinstance(data.get(field), list), field)
    inputs = data.get("inputs")
    require(isinstance(inputs, dict), "inputs")
    for field in ("assets", "publish_deps"):
        require(isinstance(inputs.get(field), list), f"inputs.{field}")
        for item in inputs[field]:
            evidence(item)
    require(bool(inputs["assets"]), "assets inventory")
    require("supplemental" in inputs, "inputs.supplemental")
    if inputs["supplemental"] is not None:
        evidence(inputs["supplemental"])
    assets = {item["path"] for item in inputs["assets"]}
    for entry in data["nuget_packages"]:
        require(isinstance(entry, dict), "NuGet entry")
        for field in ("bom-ref", "name", "version"):
            require(isinstance(entry.get(field), str) and bool(entry[field]), f"NuGet {field}")
        require(isinstance(entry.get("assets"), list) and bool(entry["assets"]), "NuGet assets")
        require(all(isinstance(ref, str) and ref in assets for ref in entry["assets"]), "unresolved assets reference")
        evidence(entry.get("nuspec"))
        require(isinstance(entry.get("bundled_texts"), list), "bundled_texts")
        for item in entry["bundled_texts"]:
            evidence(item)
    for entry in data["supplemental_components"]:
        require(isinstance(entry, dict) and isinstance(entry.get("bom-ref"), str)
                and isinstance(entry.get("texts"), list) and bool(entry["texts"]), "supplemental entry")
        for item in entry["texts"]:
            evidence(item)
    for field in ("uncovered_publish_packages", "reconciled_publish_packages", "pending_scope_checks"):
        require(all(isinstance(entry, dict) for entry in data[field]), field)
    for entry in data["reconciled_publish_packages"]:
        require(isinstance(entry.get("contents"), list) and bool(entry["contents"]),
                "reconciled contents")
        for item in entry["contents"]:
            evidence(item)


def package(path):
    path = Path(path).resolve()
    expected = Path(str(path) + ".sha256").read_text(encoding="utf-8").strip().split()
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if expected != [digest, path.name]:
        raise ValueError(f"Checksum mismatch: {path}")
    if path.suffix == ".zip":
        with zipfile.ZipFile(path) as archive:
            if archive.testzip() is not None:
                raise ValueError("Corrupt ZIP")
            names = set(archive.namelist())
            for required in ("Plinth.App.exe", "plinth.install.lock", "THIRD_PARTY_NOTICES.md", "sbom.cdx.json", "coverage-report.json"):
                if required not in names:
                    raise ValueError(f"Missing ZIP entry: {required}")
                if archive.getinfo(required).file_size == 0:
                    raise ValueError(f"Empty ZIP entry: {required}")
            coverage_report(json.loads(archive.read("coverage-report.json")))
            sbom = json.loads(archive.read("sbom.cdx.json"))
            if sbom.get("bomFormat") != "CycloneDX" or not sbom.get("components"):
                raise ValueError("Missing CycloneDX component inventory")
    print(f"Verified {path.name}: SHA256 {digest}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("check", choices=["locks", "package"])
    parser.add_argument("path", nargs="?")
    args = parser.parse_args()
    try:
        if args.check == "locks":
            locks()
        else:
            if not args.path:
                parser.error("package requires an artifact path")
            package(args.path)
    except (ValueError, OSError, KeyError, zipfile.BadZipFile) as error:
        parser.exit(1, f"BLOCKED: {error}\n")

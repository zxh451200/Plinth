"""Fail closed on missing real locks; verify distributable content and checksums."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def locks():
    projects = sorted(p for base in ("src", "tests") for p in (ROOT / base).rglob("*.csproj"))
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
            for required in ("Plinth.App.exe", "plinth.install.lock", "THIRD_PARTY_NOTICES.md", "sbom.cdx.json"):
                if required not in names:
                    raise ValueError(f"Missing ZIP entry: {required}")
                if archive.getinfo(required).file_size == 0:
                    raise ValueError(f"Empty ZIP entry: {required}")
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

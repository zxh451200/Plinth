"""Validate explicit supplemental evidence; never infer licenses or review outcomes."""
import argparse
import base64
import hashlib
import json
from pathlib import Path, PureWindowsPath
import re
from urllib.parse import urlsplit


def nonempty(value, label):
    if not isinstance(value, str) or not value.strip() or any(ord(c) < 32 for c in value):
        raise ValueError(f"Expected nonempty single-line {label}")
    if value != value.strip():
        raise ValueError(f"Unexpected surrounding whitespace in {label}")
    return value


def unique_fields(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate supplemental JSON field: {key}")
        result[key] = value
    return result


def load_supplemental(path):
    """Return notice lines and CycloneDX components, with verified evidence embedded."""
    path = Path(path).resolve(strict=True)
    data = json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_fields)
    if not isinstance(data, dict) or set(data) != {"schema_version", "components"}:
        raise ValueError("Supplemental inventory requires schema_version and components only")
    if type(data["schema_version"]) is not int or data["schema_version"] != 1:
        raise ValueError("Unsupported supplemental schema_version")
    entries = data["components"]
    if not isinstance(entries, list) or not entries:
        raise ValueError("Supplemental components must be a nonempty list")
    notices, components = [], []
    identities, refs = set(), set()
    for entry in entries:
        required = {"id", "name", "version", "category", "source", "license", "texts"}
        if not isinstance(entry, dict) or set(entry) != required:
            raise ValueError("Supplemental component fields must be: " + ", ".join(sorted(required)))
        for field in required - {"texts"}:
            nonempty(entry[field], field)
        ref = entry["id"]
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,199}", ref):
            raise ValueError(f"Invalid supplemental id: {ref}")
        identity = (entry["name"].casefold(), entry["version"].casefold())
        if ref.casefold() in refs or identity in identities:
            raise ValueError(f"Duplicate supplemental identity: {ref}")
        refs.add(ref.casefold())
        identities.add(identity)
        if entry["category"] not in {"runtime", "native", "font", "icon", "nsis"}:
            raise ValueError(f"Invalid supplemental category: {ref}")
        source = urlsplit(entry["source"])
        if source.scheme not in {"http", "https"} or not source.netloc or not source.hostname:
            raise ValueError(f"Expected HTTP(S) source URL: {ref}")
        texts = entry["texts"]
        if not isinstance(texts, list) or not texts:
            raise ValueError(f"Missing supplemental LICENSE texts: {ref}")
        notices.extend([f"## Supplemental: {entry['name']} {entry['version']}", "",
                        f"Identity: `{ref}`; category: `{entry['category']}`.",
                        f"Source: {entry['source']}",
                        f"Supplied license declaration: {entry['license']}", ""])
        properties = [
            {"name": "plinth:supplemental-category", "value": entry["category"]},
            {"name": "plinth:supplemental-id", "value": ref},
        ]
        license_texts, kinds, seen_paths = [], set(), set()
        for text in texts:
            if not isinstance(text, dict) or set(text) != {"kind", "path", "sha256"}:
                raise ValueError(f"Invalid supplemental text fields: {ref}")
            kind = text["kind"]
            if not isinstance(kind, str) or kind not in {"LICENSE", "NOTICE"}:
                raise ValueError(f"Invalid text kind: {ref}")
            relative = nonempty(text["path"], "text path")
            # Use the same portable boundary on Linux and Windows, including symlinks.
            if (Path(relative).is_absolute() or PureWindowsPath(relative).drive
                    or "\\" in relative or ".." in Path(relative).parts):
                raise ValueError(f"Supplemental text path escapes root: {relative}")
            file = (path.parent / relative).resolve(strict=True)
            if path.parent not in file.parents or not file.is_file():
                raise ValueError(f"Supplemental text path escapes root or is not a file: {relative}")
            if str(file).casefold() in seen_paths:
                raise ValueError(f"Duplicate supplemental text path: {relative}")
            seen_paths.add(str(file).casefold())
            raw = file.read_bytes()
            content = raw.decode("utf-8-sig")
            if not content.strip():
                raise ValueError(f"Empty supplemental text: {relative}")
            digest = text["sha256"]
            if (not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest)
                    or hashlib.sha256(raw).hexdigest() != digest.lower()):
                raise ValueError(f"Supplemental SHA256 mismatch: {relative}")
            kinds.add(kind)
            notices.extend([f"### {kind}: {relative}", "",
                            f"SHA256 (original bytes): {digest.lower()}", "", content, ""])
            evidence = {"kind": kind, "path": relative, "sha256": digest.lower(),
                        "encoding": "base64", "content": base64.b64encode(raw).decode("ascii")}
            properties.append({"name": "plinth:supplemental-evidence",
                               "value": json.dumps(evidence, sort_keys=True)})
            if kind == "LICENSE":
                license_texts.append({"contentType": "text/plain", "encoding": "base64",
                                      "content": evidence["content"]})
        if "LICENSE" not in kinds:
            raise ValueError(f"Missing supplemental LICENSE texts: {ref}")
        components.append({
            "type": "file" if entry["category"] in {"font", "icon"} else "library",
            "bom-ref": f"urn:plinth:supplemental:{ref}",
            "name": entry["name"], "version": entry["version"],
            "externalReferences": [{"type": "distribution", "url": entry["source"]}],
            # Names preserve supplied declarations without claiming SPDX validation.
            "licenses": [{"license": {"name": entry["license"], "text": text}}
                         for text in license_texts],
            "properties": properties,
        })
    return notices, components


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--validate", required=True, type=Path)
    args = parser.parse_args()
    try:
        _, components = load_supplemental(args.validate)
        print(f"Validated evidence for {len(components)} supplemental components; no review approval implied")
    except (OSError, ValueError) as error:
        parser.exit(1, f"BLOCKED: {error}\n")

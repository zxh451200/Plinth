"""Check resource and package wiring without claiming to compile C# or XAML."""

from __future__ import annotations

from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET
from module_contracts import check_migration_entities, check_modules


def has_interpolated_log(text: str) -> bool:
    """Narrow guard for direct logging calls; not a C# parser or taint analysis."""
    return bool(re.search(
        r"\b(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)|"
        r"Verbose|Debug|Information|Warning|Error|Fatal)\s*\("
        r'[^;]*?\$@?"',
        text,
    ))


def check(root: Path) -> list[str]:
    errors: list[str] = []
    app = root / "src" / "Plinth.App"
    resources: dict[str, dict[str, str]] = {}
    for name in ("Strings.resx", "Strings.zh-CN.resx"):
        path = app / "Resources" / name
        try:
            entries = ET.parse(path).getroot().findall("data")
            values: dict[str, str] = {}
            for entry in entries:
                key = entry.attrib["name"]
                if key in values:
                    errors.append(f"{path}: duplicate resource {key}")
                values[key] = entry.findtext("value", "")
            resources[name] = values
        except (OSError, ET.ParseError, KeyError) as ex:
            errors.append(f"{path}: {ex}")
    if len(resources) == 2:
        english, chinese = resources.values()
        for key in sorted(english.keys() ^ chinese.keys()):
            errors.append(f"Resource exists in only one language: {key}")
        for key in english.keys() & chinese.keys():
            if sorted(re.findall(r"\{\d+(?:[^{}]*)\}", english[key])) != sorted(
                re.findall(r"\{\d+(?:[^{}]*)\}", chinese[key])
            ):
                errors.append(f"Resource placeholders differ: {key}")

    defined = resources.get("Strings.resx", {})
    for path in app.rglob("*"):
        if not path.is_file() or {"bin", "obj"} & set(path.parts):
            continue
        if path.suffix not in (".cs", ".axaml"):
            continue
        text = path.read_text(encoding="utf-8-sig")
        code = "\n".join(
            line for line in text.splitlines()
            if not line.lstrip().startswith("//")
        ) if path.suffix == ".cs" else re.sub(r"<!--.*?-->", "", text, flags=re.S)
        if path.suffix == ".cs" and has_interpolated_log(code):
            errors.append(f"{path.relative_to(root)}: interpolated logging can disclose business text; use constant templates and approved properties")
        keys = re.findall(r'Loc\.(?:Get|Format)\("([^"]+)"', code)
        keys += re.findall(r"\{loc:T\s+([^}\s]+)", code)
        for key in sorted(set(keys) - defined.keys()):
            errors.append(f"{path.relative_to(root)}: undefined resource {key}")
        if path.suffix == ".axaml":
            try:
                ET.fromstring(text)
            except ET.ParseError as ex:
                errors.append(f"{path.relative_to(root)}: invalid XML: {ex}")

    try:
        central = ET.parse(root / "Directory.Packages.props").getroot()
        versions = {
            node.attrib["Include"]
            for node in central.iter("PackageVersion")
            if "Include" in node.attrib
        }
        for path in root.rglob("*.csproj"):
            if {"bin", "obj"} & set(path.parts):
                continue
            project = ET.parse(path).getroot()
            project_versions = versions | {
                node.attrib["Include"]
                for node in project.iter("PackageVersion")
                if "Include" in node.attrib
            }
            for reference in project.iter("PackageReference"):
                package = reference.attrib.get("Include")
                if package and package not in project_versions:
                    errors.append(f"{path.relative_to(root)}: no central version for {package}")
    except (OSError, ET.ParseError) as ex:
        errors.append(f"Package XML: {ex}")
    errors.extend(check_modules(root))
    errors.extend(check_migration_entities(root))
    return errors


if __name__ == "__main__":
    problems = check(Path(__file__).resolve().parents[1])
    for problem in problems:
        print(problem, file=sys.stderr)
    print(f"Source contracts: {len(problems)} error(s). This does not replace dotnet build/test.")
    sys.exit(bool(problems))

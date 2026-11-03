"""Offline source checks. These checks do not compile C# or execute Avalonia."""
from pathlib import Path
import ast
import collections
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "Plinth.App"


def main():
    resources = []
    for name in ("Strings.resx", "Strings.zh-CN.resx"):
        entries = ET.parse(SRC / "Resources" / name).getroot().findall("data")
        counts = collections.Counter(e.attrib["name"] for e in entries)
        assert all(n == 1 for n in counts.values()), f"Duplicate resource keys: {name}"
        resources.append({e.attrib["name"]: e.findtext("value", "") for e in entries})
    english, chinese = resources
    assert english.keys() == chinese.keys(), (
        f"Missing Chinese: {sorted(english.keys() - chinese.keys())}; "
        f"missing English: {sorted(chinese.keys() - english.keys())}"
    )
    for key, value in english.items():
        assert sorted(re.findall(r"\{\d+\}", value)) == sorted(
            re.findall(r"\{\d+\}", chinese[key])
        ), f"Placeholder mismatch: {key}"
    used = set()
    xml_count = 0
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(p in ("obj", "bin", ".git") for p in path.parts):
            continue
        if path.suffix in (".axaml", ".resx", ".csproj", ".props", ".config"):
            ET.parse(path)
            xml_count += 1
        if path.suffix == ".py":
            ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
        if SRC in path.parents and path.suffix in (".cs", ".axaml"):
            text = path.read_text(encoding="utf-8")
            used.update(re.findall(r'Loc\.(?:Get|Format)\("([^"]+)"', text))
            used.update(re.findall(r"\{loc:T ([\w.]+)\}", text))
            # Include indirect keys passed through switch expressions and shared prompts.
            used.update(k for k in re.findall(r'"([\w.]+)"', text)
                        if k.startswith(("tasks.status.", "diagnostics.", "shell.unsaved.")))
    assert not used - english.keys(), f"Missing resources: {sorted(used - english.keys())}"
    versions = {e.attrib["Include"]: e.attrib["Version"] for e in
                ET.parse(ROOT / "Directory.Packages.props").getroot().iter("PackageVersion")}
    for project in ROOT.rglob("*.csproj"):
        for package in ET.parse(project).getroot().iter("PackageReference"):
            assert package.attrib["Include"] in versions
            assert "Version" not in package.attrib and "VersionOverride" not in package.attrib
    print(f"PASS: {len(english)} bilingual keys, {len(used)} referenced keys, "
          f"{xml_count} XML files, Python AST and central package references.")
    print("LIMIT: no C# compilation, NuGet verification, IdP or desktop UI execution.")


if __name__ == "__main__":
    main()

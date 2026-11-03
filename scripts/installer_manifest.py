"""Generate an explicit NSIS uninstall list from the actual publish directory."""
from pathlib import Path
import argparse
import re


def is_link(path: Path) -> bool:
    # Windows junctions are reparse points even when is_symlink() is false.
    return path.is_symlink() or bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400)


def generate(publish: Path) -> str:
    files = []
    directories = set()
    if not publish.is_dir() or is_link(publish):
        raise ValueError("Publish must be a real directory")
    for path in sorted(publish.rglob("*")):
        relative = path.relative_to(publish)
        if is_link(path) or any(not re.fullmatch(r"[A-Za-z0-9_. -]+", part) or
                                    part in (".", "..") for part in relative.parts):
            raise ValueError(f"Unsafe publish path: {relative}")
        if path.is_dir():
            directories.add(relative)
            continue
        if not path.is_file():
            raise ValueError(f"Unsupported publish entry: {relative}")
        if path.name.lower() in ("plinth.install.lock", "uninstall.exe"):
            continue
        if path.suffix.lower() in (".db", ".sqlite", ".sqlite3", ".zip") or path.name.lower() in (
                "settings.json", "workspace-preferences.json", "deployment.json"):
            raise ValueError(f"User data or deployment configuration in publish: {relative}")
        files.append(relative)
    if Path("Plinth.App.exe") not in files:
        raise ValueError("Missing Plinth.App.exe")
    lines = ["; Generated from this publish directory. No recursive directory deletion.",
             "!macro CheckPublishedPaths FailureLabel"]
    # Check directories and destination files before either installation writes or uninstallation.
    for path in sorted(directories | set(files) | {Path("Uninstall.exe"), Path("plinth.install.lock")}, key=str):
        name = str(path).replace("/", "\\")
        lines += [
            f'  System::Call \'kernel32::GetFileAttributesW(w "$INSTDIR\\{name}") i.r0\'',
            '  ${If} $0 != -1',
            '    IntOp $0 $0 & 0x400',
            '    ${If} $0 != 0',
            '      Goto ${FailureLabel}',
            '    ${EndIf}',
            '  ${EndIf}',
        ]
    lines += ["!macroend", "!macro RemovePublishedFiles",
              "  !insertmacro CheckPublishedPaths uninstall_failed_locked"]
    lines += [f'  Delete "$INSTDIR\\{str(path).replace(chr(47), chr(92))}"' for path in files]
    lines += ['  IfErrors uninstall_failed_locked']
    lines += [f'  RMDir "$INSTDIR\\{str(path).replace(chr(47), chr(92))}"'
              for path in sorted(directories, key=lambda p: (-len(p.parts), str(p)))]
    # Nonempty directories are expected when users have added files.
    lines += ['  ClearErrors', '!macroend']
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    arguments.output.write_text(generate(arguments.publish), encoding="utf-8")

"""Narrow checks for the reference modules, not a C# parser or an authorization proof.

Checks direct module references, explicit EF mappings and local service write paths.
Runtime authorization tests remain necessary for aliases, reflection and external calls.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re


def masked(text: str) -> str:
    """Preserve offsets/newlines while hiding C# comments and literal contents."""
    pattern = r'//[^\n]*|/\*.*?\*/|""".*?"""|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
    return re.sub(pattern, lambda match: re.sub(r"[^\n]", " ", match.group()), text, flags=re.S)


def closing(code: str, start: int, left: str, right: str) -> int:
    depth = 0
    for index in range(start, len(code)):
        if code[index] == left:
            depth += 1
        elif code[index] == right:
            depth -= 1
            if depth == 0:
                return index
    return len(code) - 1


@dataclass
class Method:
    name: str
    public: bool
    start: int
    end: int


def methods(code: str) -> list[Method]:
    result = []
    signature = re.compile(
        r"\b(public|private|internal|protected)\s+"
        r"(?:(?:static|async|virtual|override|sealed|partial)\s+)*"
        r"(?!(?:class|record|struct|interface|enum)\b)[\w.<>,?\[\]]+\s+(\w+)\s*\("
    )
    for match in signature.finditer(code):
        end_parameters = closing(code, match.end() - 1, "(", ")")
        start = end_parameters + 1
        while start < len(code) and code[start].isspace():
            start += 1
        if code.startswith("{", start):
            end = closing(code, start, "{", "}") + 1
        elif code.startswith("=>", start):
            cursor = start + 2
            while cursor < len(code):
                if code[cursor] in "({[":
                    cursor = closing(code, cursor, code[cursor], {"(": ")", "{": "}", "[": "]"}[code[cursor]])
                elif code[cursor] == ";":
                    break
                cursor += 1
            end = cursor + 1
        else:
            continue
        result.append(Method(match[2], match[1] == "public", start, end))
    return result


def arguments(code: str, start: int, end: int) -> list[tuple[int, int]]:
    result = []
    cursor = begin = start
    while cursor < end:
        if code[cursor] in "({[":
            cursor = closing(code, cursor, code[cursor], {"(": ")", "{": "}", "[": "]"}[code[cursor]])
        elif code[cursor] == ",":
            result.append((begin, cursor))
            begin = cursor + 1
        cursor += 1
    return result + [(begin, end)]


def service_errors(text: str, module: str) -> list[tuple[int, str]]:
    code = masked(text)
    declarations = methods(code)
    receivers = set(re.findall(r"\b(?:SensitiveOperationService|AuthorizedOperation)\??\s+(\w+)", code))
    protected = []
    errors = []
    for match in re.finditer(r"\b(\w+)\.Execute(?:Confirmed)?Async(?:<[^;()]+>)?\s*\(", code):
        if match[1] not in receivers:
            continue
        end = closing(code, match.end() - 1, "(", ")")
        args = arguments(code, match.end(), end)
        literal = re.match(r'\s*"([a-z][a-z0-9]*:[a-z][a-z0-9._-]*)"\s*$', text[slice(*args[0])])
        if not literal or not literal[1].startswith(module.lower() + ":"):
            errors.append((match.start(), "permission must be a literal owned by this module"))
            continue
        # Only the action delegate is authorized, not other arguments or sibling writes.
        if len(args) >= 4 and "=>" in code[slice(*args[3])]:
            protected.append(args[3])

    def guarded(offset: int) -> bool:
        return any(start <= offset < end for start, end in protected)

    writes = list(re.finditer(
        r"\.\s*(?:SaveChanges(?:Async)?|Execute(?:Delete|Update)(?:Async)?|"
        r"ExecuteSql(?:Raw|Interpolated)(?:Async)?|WriteFileAsync|WriteErrorReportAsync)\s*\(", code))
    by_name: dict[str, list[Method]] = {}
    for method in declarations:
        by_name.setdefault(method.name, []).append(method)

    def unsafe_path(method: Method, visited: set[int]) -> int | None:
        if method.start in visited:
            return None
        visited = visited | {method.start}
        for write in writes:
            if method.start <= write.start() < method.end and not guarded(write.start()):
                return write.start()
        for call in re.finditer(r"(?<![\w.])(?:this\.)?(\w+)\s*\(", code[method.start:method.end]):
            offset = method.start + call.start()
            if guarded(offset):
                continue
            for target in by_name.get(call[1], []):
                bad = unsafe_path(target, visited)
                if bad is not None:
                    return bad
        return None

    for method in declarations:
        if method.public:
            bad = unsafe_path(method, set())
            if bad is not None:
                errors.append((bad, f"{method.name} reaches a write outside an authorized action"))
    return sorted(set(errors))


def check_migration_entities(root: Path) -> list[str]:
    app = root / "src/Plinth.App"
    migrations = app / "Migrations"
    snapshot = migrations / "AppDbContextModelSnapshot.cs"
    snapshot_text = snapshot.read_text(encoding="utf-8-sig") if snapshot.is_file() else ""
    target_text = "\n".join(path.read_text(encoding="utf-8-sig")
                            for path in migrations.glob("*.cs") if path != snapshot)
    errors = []
    for path in (app / "Modules").rglob("*.cs"):
        if {"bin", "obj"} & set(path.parts):
            continue
        code = masked(path.read_text(encoding="utf-8-sig"))
        namespace = re.search(r"\bnamespace\s+([\w.]+)", code)
        if not namespace:
            continue
        for entity in re.finditer(r"\bclass\s+(\w+)\s*:\s*AuditableEntity\b", code):
            name = namespace[1] + "." + entity[1]
            declaration = re.compile(rf'\.Entity\s*\(\s*"{re.escape(name)}"')
            for label, text in (("snapshot", snapshot_text), ("migration target", target_text)):
                if not declaration.search(text):
                    errors.append(f"{path.relative_to(root).as_posix()}: {name} is missing from the {label}")
    return errors


def check_modules(root: Path) -> list[str]:
    modules = root / "src/Plinth.App/Modules"
    errors = []
    portability_patterns = (
        ("provider dependency",
         r"\b(?P<symbol>Microsoft\s*\.\s*Data\s*\.\s*Sqlite|SqliteException|SqliteConnection)\b"),
        ("nonportable EF call",
         r"\.\s*(?P<symbol>UseSqlite|UseNpgsql|UseMySql|UseSqlServer|UseCollation|HasColumnType|"
         r"FromSql\w*|ExecuteSql\w*|SqlQuery\w*)\b\s*(?:<[^;{}()]+>)?\s*\("),
    )
    for path in sorted(modules.rglob("*.cs")):
        relative = path.relative_to(modules)
        if {"bin", "obj"} & set(relative.parts):
            continue
        code = masked(path.read_text(encoding="utf-8-sig"))
        for label, pattern in portability_patterns:
            for match in re.finditer(pattern, code):
                line = code.count("\n", 0, match.start("symbol")) + 1
                symbol = re.sub(r"\s+", "", match["symbol"])
                errors.append(f"{path.relative_to(root).as_posix()}:{line}: {label}: {symbol}")
    for directory in sorted(path for path in modules.glob("*") if path.is_dir()):
        sources = {}
        for path in sorted(directory.rglob("*")):
            if not path.is_file() or {"bin", "obj"} & set(path.parts):
                continue
            if path.suffix not in (".cs", ".axaml"):
                continue
            text = path.read_text(encoding="utf-8-sig")
            code = masked(text) if path.suffix == ".cs" else re.sub(r"<!--.*?-->", "", text, flags=re.S)
            for reference in re.finditer(r"\bPlinth\.App\.Modules\.(\w+)\b", code):
                if reference[1] != directory.name:
                    line = code.count("\n", 0, reference.start()) + 1
                    errors.append(f"{path.relative_to(root).as_posix()}:{line}: cross-module reference to {reference[1]}")
            if path.suffix == ".cs":
                sources[path] = (text, code)
            if path.suffix == ".cs" and "Services" in path.parts and "IDbContextFactory<AppDbContext>" in code:
                for offset, problem in service_errors(text, directory.name):
                    line = text.count("\n", 0, offset) + 1
                    errors.append(f"{path.relative_to(root).as_posix()}:{line}: {problem}")

        combined = "\n".join(code for _, code in sources.values())
        for path, (text, code) in sources.items():
            for entity in re.finditer(r"\bclass\s+(\w+)\s*:\s*AuditableEntity\b", code):
                name = entity[1]
                mapping = re.search(
                    rf"\bConfigure\s*\(\s*EntityTypeBuilder<{name}>\s+(\w+)\s*\)\s*\{{", combined)
                line = text.count("\n", 0, entity.start()) + 1
                location = f"{path.relative_to(root).as_posix()}:{line}: {name}"
                if not re.search(rf"\bIEntityTypeConfiguration<{name}>", combined) or not mapping:
                    errors.append(location + " needs an explicit IEntityTypeConfiguration")
                    continue
                body = combined[mapping.end():closing(combined, mapping.end() - 1, "{", "}")]
                builder = re.escape(mapping[1])
                if not re.search(rf"\b{builder}\.HasKey\s*\(", body):
                    errors.append(location + " configuration is missing HasKey")
                if not re.search(
                    rf"\b{builder}\.Property\s*\(\s*\w+\s*=>\s*\w+\.Version\s*\)\s*\.IsConcurrencyToken\s*\(", body):
                    errors.append(location + " configuration is missing the Version concurrency token")
    return errors

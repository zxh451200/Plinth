"""Run the independent pilot graphs with a real SDK; never synthesize locks."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PILOTS = {
    "dock": "tests/Plinth.DockDemo.Tests/Plinth.DockDemo.Tests.csproj",
    "runtime-plugins": "tests/Plinth.RuntimePlugins.Tests/Plinth.RuntimePlugins.Tests.csproj",
    "offline-sync": "tests/Plinth.OfflineSyncPilot.Tests/Plinth.OfflineSyncPilot.Tests.csproj",
}


def project_graph(project):
    """Include executable peers even when ReferenceOutputAssembly is false."""
    projects = set()

    def visit(path):
        path = path.resolve()
        if path in projects:
            return
        projects.add(path)
        for reference in ET.parse(path).iter("ProjectReference"):
            visit(path.parent / reference.attrib["Include"].replace("\\", "/"))

    visit(project)
    return sorted(projects)


def require_locks(projects):
    for project in projects:
        lock = project.with_name("packages.lock.json")
        if not lock.is_file() or not lock.stat().st_size:
            raise RuntimeError(f"Missing nonempty dependency lock: {lock}")


def run_step(arguments, evidence, name):
    log = evidence / f"{name}.log"
    print(f"{subprocess.list2cmdline(arguments)} -> {log}", flush=True)
    with log.open("w", encoding="utf-8") as output:
        output.write(f"$ {subprocess.list2cmdline(arguments)}\n")
        output.flush()
        try:
            result = subprocess.run(
                arguments, cwd=str(ROOT), stdout=output, stderr=subprocess.STDOUT,
            )
        except OSError as error:
            output.write(f"Could not start: {error}\n")
            raise RuntimeError(f"Could not start command; see {log}") from error
        output.write(f"\nExit code: {result.returncode}\n")
    if result.returncode:
        raise RuntimeError(f"{name} failed (exit {result.returncode}); see {log}")
    return log


def require_test_results(results):
    reports = sorted(results.glob("*.trx"))
    if not reports:
        raise RuntimeError(f"No TRX produced: {results}")
    for report in reports:
        root = ET.parse(report).getroot()
        summary = root.find("{*}ResultSummary")
        counters = summary.find("{*}Counters") if summary is not None else None
        if counters is None:
            raise RuntimeError(f"Missing TRX counters: {report}")
        try:
            executed = int(counters.attrib["executed"])
            unsuccessful = sum(int(counters.attrib.get(key, "0")) for key in
                               ("failed", "error", "timeout", "aborted", "inconclusive"))
        except (KeyError, ValueError) as error:
            raise RuntimeError(f"Invalid TRX counters: {report}") from error
        if (root.tag.rsplit("}", 1)[-1] != "TestRun" or summary is None
                or summary.attrib.get("outcome") not in ("Passed", "Completed")
                or executed <= 0 or unsuccessful):
            raise RuntimeError(f"TRX does not show a successful nonempty test run: {report}")
        print(f"Verified TRX: {report} ({executed} executed)", flush=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", required=True, choices=("generate-locks", "locked"),
                        help="generate-locks permits NuGet to write locks; locked requires existing locks")
    parser.add_argument("--pilot", choices=("all", *PILOTS), default="all")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/pilot-checks",
                        help="Evidence parent directory; each invocation creates a fresh subdirectory")
    args = parser.parse_args(argv)
    try:
        args.output.mkdir(parents=True, exist_ok=True)
        evidence = Path(tempfile.mkdtemp(prefix=f"{args.mode}-", dir=args.output.resolve()))
        print(f"Evidence: {evidence}", flush=True)
        version_log = run_step(["dotnet", "--version"], evidence, "sdk-version")
        if not any(line.strip().startswith("10.0.") for line in version_log.read_text().splitlines()):
            raise RuntimeError(f".NET 10 SDK required; see {version_log}")
        run_step(["dotnet", "--info"], evidence, "sdk-info")
        selected = PILOTS if args.pilot == "all" else {args.pilot: PILOTS[args.pilot]}
        for name, relative in selected.items():
            project = ROOT / relative
            projects = project_graph(project)
            if args.mode == "locked":
                # NuGet can create a missing lock even in locked mode: reject it first.
                require_locks(projects)
            restore = ["dotnet", "restore", str(project), "--configfile",
                       str(ROOT / "scripts/nuget.config")]
            restore += (["--locked-mode"] if args.mode == "locked" else
                        ["--use-lock-file", "-p:RestoreLockedMode=false"])
            run_step(restore, evidence, f"{name}-restore")
            require_locks(projects)
            for index, member in enumerate(projects):
                run_step(["dotnet", "format", str(member), "--verify-no-changes", "--no-restore"],
                         evidence, f"{name}-format-{index}")
            run_step(["dotnet", "build", str(project), "-c", "Release", "--no-restore",
                      "-warnaserror:nullable"], evidence, f"{name}-build")
            results = evidence / name / "test-results"
            run_step(["dotnet", "test", str(project), "-c", "Release", "--no-build",
                      "--no-restore", "-p:TestingPlatformDotnetTestSupport=true",
                      "--logger", "trx", "--results-directory", str(results)],
                     evidence, f"{name}-test")
            require_test_results(results)
        print(f"PASS: {args.mode}; evidence: {evidence}")
        if args.mode == "generate-locks":
            print("Review NuGet-generated locks, then run --mode locked separately.")
        return 0
    except (OSError, RuntimeError, ET.ParseError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

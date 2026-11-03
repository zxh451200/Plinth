"""Run the Python-only checks; this is not application acceptance."""
import argparse
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
STEPS = (
    ("Python tool tests (may report runtime-dependent skips)", ("-m", "unittest", "discover", "-s", "scripts", "-p", "test_*.py")),
    ("Source contracts (static)", ("scripts/check_source_contracts.py",)),
    ("Resources and project wiring (static)", ("scripts/integration_checks.py",)),
    ("Python SQLite smoke (not EF migrations)", ("tests/sqlite_migration_smoke.py",)),
)
LIMIT = ("LIMIT: no dependency restore, C# build/tests, EF migrations, "
         "desktop UI, packaging or Windows installation acceptance.")

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args(argv)
    print(LIMIT, flush=True)
    for name, arguments in STEPS:
        print("RUN: " + name, flush=True)
        try:
            result = subprocess.run([sys.executable, *arguments], cwd=str(ROOT))
        except OSError as error:
            print("FAIL: {} could not start: {}".format(name, error), file=sys.stderr)
            return 1
        if result.returncode != 0:
            print("FAIL: {} (exit {}); remaining checks not run.".format(name, result.returncode), file=sys.stderr)
            return 1
    print("PASS: Python-only checks completed; see test output for skips.", flush=True)
    print(LIMIT, flush=True)
    return 0

if __name__ == "__main__":
    sys.exit(main())

"""Exercise the PowerShell entry points with isolated native command fixtures.

These tests require PowerShell; skipping them is not a PowerShell syntax check.
"""
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


SCRIPTS = Path(__file__).resolve().parent
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")
FAILURE = "Python 3.8 or newer is required. No validated interpreter was found."
RUNNER = r"""
import json
import sys
from pathlib import Path

config = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
args = sys.argv[2:]
with open(config["log"], "a") as log:
    log.write(json.dumps(args) + "\n")
if config["launcher"]:
    if not args or args.pop(0) != "-3":
        sys.exit(41)
if args[:1] == ["-c"]:
    if config["probe_exit"]:
        sys.exit(config["probe_exit"])
    sys.version_info = tuple(config["version"])
    sys.executable = config["reported"]
    exec(args[1])
else:
    sys.exit(config["exit_code"])
"""


@unittest.skipUnless(POWERSHELL, "PowerShell unavailable; execution tests not run")
class PythonSelectionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="plinth python tests ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.scripts = self.root / "scripts"
        self.scripts.mkdir()
        for name in ("python.ps1", "acceptance.ps1", "publish.ps1"):
            shutil.copyfile(SCRIPTS / name, self.scripts / name)
        self.bin = self.root / "commands with spaces"
        self.bin.mkdir()
        self.runner = self.root / "native_fixture.py"
        self.runner.write_text(RUNNER)
        self.env = os.environ.copy()
        # Change only the child process environment, never the user's PATH.
        self.env["PATH"] = str(self.bin)
        self.env["PLINTH_TEST_SCRIPTS"] = str(self.scripts)

    def command(self, name, version=(3, 8), exit_code=0, probe_exit=0,
                reported=None, launcher=False):
        executable = self.bin / (name + ".cmd" if os.name == "nt" else name)
        config = self.bin / (name + ".json")
        config.write_text(json.dumps({
            "version": version, "exit_code": exit_code, "probe_exit": probe_exit,
            "reported": str(reported or executable), "launcher": launcher,
            "log": str(self.bin / (name + ".log")),
        }))
        args = [sys.executable, str(self.runner), str(config)]
        if os.name == "nt":
            executable.write_text("@" + subprocess.list2cmdline(args) + " %*\n")
        else:
            executable.write_text(
                "#!/bin/sh\nexec " + " ".join(map(shlex.quote, args)) + ' "$@"\n')
            executable.chmod(0o755)
        return executable

    def calls(self, name):
        log = self.bin / (name + ".log")
        return [json.loads(line) for line in log.read_text(encoding="utf-8-sig").splitlines()] if log.exists() else []

    def run_ps(self, code, success=True):
        result = subprocess.run(
            [POWERSHELL, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "$ErrorActionPreference = 'Stop'; Set-StrictMode -Version Latest; "
             "try { " + code + " } catch { "
             "[Console]::Error.WriteLine($_.Exception.Message); exit 1 }"],
            env=self.env, cwd=str(self.root), text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result.stdout + result.stderr

    def resolve(self, arguments="", success=True):
        return self.run_ps(
            '. "$env:PLINTH_TEST_SCRIPTS/python.ps1"; '
            "Resolve-PlinthPython " + arguments, success)

    def test_python2_and_37_are_rejected_before_python3_38(self):
        for version in ((2, 7), (3, 7)):
            with self.subTest(version=version):
                self.command("python", version=version)
                expected = self.command("python3")
                self.assertIn(str(expected), self.resolve())

    def test_valid_python_is_pinned_and_arguments_preserve_spaces(self):
        expected = self.command("python")
        self.command("python3")
        self.env["PLINTH_TEST_ARGUMENT"] = str(self.root / "an argument with spaces")
        self.run_ps(
            '. "$env:PLINTH_TEST_SCRIPTS/python.ps1"; $python = Resolve-PlinthPython; '
            "$arguments = @('fixture.py', $env:PLINTH_TEST_ARGUMENT); "
            '& $python @arguments; if ($LASTEXITCODE -ne 0) { throw "native failure" }')
        self.assertEqual(len(self.calls("python")), 3)
        self.assertEqual(self.calls("python")[-1],
                         ["fixture.py", self.env["PLINTH_TEST_ARGUMENT"]])
        self.assertEqual(self.calls("python3"), [])
        self.assertTrue(expected.is_file())

    def test_explicit_override_and_invalid_overrides_never_fall_back(self):
        expected = self.command("chosen python")
        self.command("python")
        self.env["PLINTH_TEST_OVERRIDE"] = str(expected)
        self.assertIn(str(expected), self.resolve("-PythonExecutable $env:PLINTH_TEST_OVERRIDE"))
        invalid = [
            str(self.bin / "missing"),
            str(self.command("old python", version=(3, 7))),
            str(self.command("broken python", probe_exit=23)),
            "",
        ]
        for value in invalid:
            with self.subTest(value=value):
                self.env["PLINTH_TEST_OVERRIDE"] = value
                self.assertIn(FAILURE, self.resolve(
                    "-PythonExecutable $env:PLINTH_TEST_OVERRIDE", success=False))
        self.assertEqual(self.calls("python"), [])

    def test_missing_old_and_broken_candidates_have_clear_failure(self):
        self.assertIn(FAILURE, self.resolve(success=False))
        self.command("python", version=(2, 7))
        self.command("python3", probe_exit=19)
        self.assertIn(FAILURE, self.resolve(success=False))

    def test_reported_interpreter_is_revalidated(self):
        old = self.command("old interpreter", version=(3, 7))
        self.command("python", reported=old)
        expected = self.command("python3")
        self.assertIn(str(expected), self.resolve())
        self.assertEqual(len(self.calls("old interpreter")), 1)

    @unittest.skipUnless(os.name == "nt", "Windows py -3 launcher")
    def test_windows_launcher_is_pinned_to_its_interpreter(self):
        expected = self.command("launched interpreter")
        self.command("py", launcher=True, reported=expected)
        self.assertIn(str(expected), self.resolve())
        self.assertEqual(self.calls("py")[0][0], "-3")
        self.assertEqual(len(self.calls("launched interpreter")), 1)

    def test_native_failures_stop_both_entry_points(self):
        for script, message in (("acceptance.ps1", "Tool tests failed"),
                                ("publish.ps1", "Real dependency locks required")):
            with self.subTest(script=script):
                self.command("python", exit_code=29)
                output = self.run_ps(
                    '& "$env:PLINTH_TEST_SCRIPTS/' + script + '"', success=False)
                self.assertIn(message, output)
                calls = self.calls("python")
                self.assertEqual(sum(args[:1] != ["-c"] for args in calls), 1)
                (self.bin / "python.log").unlink()

    def test_acceptance_passes_pinned_interpreter_to_publish(self):
        expected = self.command("python")
        self.command("python3")
        self.command("dotnet")
        # Isolate orchestration from actual builds and packaging.
        (self.scripts / "publish.ps1").write_text(
            "param([string]$BrandingFile, [switch]$RequireInstaller, "
            "[switch]$EnableXlsx, [string]$PythonExecutable)\n"
            "Write-Output ('PUBLISHED_WITH=' + $PythonExecutable)\n")
        output = self.run_ps('& "$env:PLINTH_TEST_SCRIPTS/acceptance.ps1" -Package')
        self.assertIn("PUBLISHED_WITH=" + str(expected), output)
        self.assertEqual(self.calls("python3"), [])
        self.assertEqual(len(self.calls("python")), 7)  # Two probes, five tool calls.


if __name__ == "__main__":
    unittest.main()

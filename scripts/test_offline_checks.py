"""Test runner orchestration without recursively invoking tool discovery."""
import contextlib
import io
import subprocess
import sys
import unittest
from unittest.mock import patch

try:
    import offline_checks
except ModuleNotFoundError:
    from scripts import offline_checks

class OfflineChecksTests(unittest.TestCase):
    def run_checks(self, results):
        output = io.StringIO()
        with patch.object(offline_checks.subprocess, "run", side_effect=results) as run:
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                code = offline_checks.main([])
        return code, output.getvalue(), run

    def test_success_uses_current_python_and_repository_directory(self):
        code, output, run = self.run_checks([subprocess.CompletedProcess([], 0) for _ in offline_checks.STEPS])
        self.assertEqual(code, 0)
        self.assertEqual(run.call_count, 4)
        for call, (_, arguments) in zip(run.call_args_list, offline_checks.STEPS):
            self.assertEqual(call.args[0], [sys.executable, *arguments])
            self.assertEqual(call.kwargs, {"cwd": str(offline_checks.ROOT)})
        self.assertIn("see test output for skips", output)
        self.assertIn(offline_checks.LIMIT, output)

    def test_each_failure_stops_later_checks(self):
        for index in range(len(offline_checks.STEPS)):
            with self.subTest(index=index):
                code, output, run = self.run_checks([subprocess.CompletedProcess([], 0) for _ in range(index)] + [subprocess.CompletedProcess([], 23)])
                self.assertEqual(code, 1)
                self.assertEqual(run.call_count, index + 1)
                self.assertIn("exit 23", output)
                self.assertNotIn("PASS:", output)

    def test_start_failure_returns_nonzero(self):
        code, output, run = self.run_checks([OSError("fixture unavailable")])
        self.assertEqual(code, 1)
        self.assertEqual(run.call_count, 1)
        self.assertIn("could not start", output)
        self.assertNotIn("PASS:", output)

if __name__ == "__main__":
    unittest.main()

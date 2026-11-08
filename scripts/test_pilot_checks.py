"""Orchestration tests only: no dotnet processes or repository locks are written."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

try:
    import pilot_checks
except ModuleNotFoundError:
    from scripts import pilot_checks


class PilotChecksTests(unittest.TestCase):
    def test_runtime_plugins_use_the_trusted_default_deny_composition_gate(self):
        root = pilot_checks.ROOT
        composition = (root / "src/Plinth.App/ServiceCollectionExtensions.cs").read_text()
        deployment = (root / "src/Plinth.App/Services/DeploymentConfiguration.cs").read_text()
        example = json.loads((root / "deployment.example.json").read_text())
        registration = (root / "src/Plinth.App/Modules/RuntimePlugins/RuntimePluginsModuleRegistration.cs").read_text()
        configuration = (root / "src/Plinth.App/Modules/RuntimePlugins/RuntimePluginConfiguration.cs").read_text()
        self.assertIn("public bool EnableRuntimePlugins { get; set; }", deployment)
        self.assertFalse(example["EnableRuntimePlugins"])
        self.assertFalse(example["EnableOfflineSyncPilot"])
        self.assertIn("if (deployment.EnableRuntimePlugins)", composition)
        self.assertIn("services.AddRuntimePluginsModule();", composition)
        self.assertIn("RequiredAnyPermission = [RuntimePluginsViewModel.InvokePermission]", registration)
        self.assertIn("private bool Allowed()", (root / "src/Plinth.App/Modules/RuntimePlugins/ViewModels/RuntimePluginsViewModel.cs").read_text())
        self.assertIn("using Plinth.App.Modules.RuntimePlugins;", composition)
        self.assertIn("RuntimePluginConfiguration? configuration = null", registration)
        self.assertIn("configuration ?? RuntimePluginConfiguration.Disabled", registration)
        self.assertIn("public static RuntimePluginConfiguration Disabled { get; } = new();", configuration)
        self.assertIn("public IReadOnlyList<AllowedPlugin> Allowlist { get; init; } = [];", configuration)
        self.assertNotIn("AddOfflineSyncModule", composition)
        self.assertIn("public bool EnableOfflineSyncPilot { get; set; }", deployment)

        project = root / "tools/Plinth.PluginPilot/Plinth.PluginPilot.csproj"
        definition = ET.parse(project)
        self.assertEqual(definition.findtext(".//OutputType"), "Exe")
        linked_sources = {
            source.resolve()
            for item in definition.iter("Compile")
            for source in project.parent.glob(item.attrib["Include"])
        }
        plugin_sources = root / "src/Plinth.App/Modules/RuntimePlugins"
        self.assertIn(plugin_sources / "RuntimePluginHost.cs", linked_sources)
        self.assertIn(plugin_sources / "PluginProtocol.cs", linked_sources)
        self.assertFalse(list(definition.iter("PackageReference")))
        graph = pilot_checks.project_graph(root / pilot_checks.PILOTS["runtime-plugins"])
        self.assertIn(project, graph)
        self.assertFalse(any(p.name == "Plinth.App.csproj" for p in graph))

    def test_real_reference_graphs_include_all_peers(self):
        expected = {"dock": 2, "runtime-plugins": 4, "offline-sync": 2}
        graphs = {}
        for name, relative in pilot_checks.PILOTS.items():
            graphs[name] = pilot_checks.project_graph(pilot_checks.ROOT / relative)
            self.assertEqual(len(graphs[name]), expected[name])
            self.assertFalse(any(p.name == "Plinth.App.csproj" for p in graphs[name]))
        self.assertIn("Plinth.PluginFixture.csproj",
                      [p.name for p in graphs["runtime-plugins"]])
        self.assertIn("Plinth.EchoPlugin.csproj",
                      [p.name for p in graphs["runtime-plugins"]])

    def exercise(self, mode="generate-locks", fail_at=None, locks=True,
                 trx=True, version="10.0.403", start_error=False, missing_peer=False,
                 report=None):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / "Test.csproj"
            peer = root / "peer/Peer.csproj"
            peer.parent.mkdir()
            project.write_text('<Project><ProjectReference Include="peer/Peer.csproj" '
                               'ReferenceOutputAssembly="false" /></Project>')
            peer.write_text("<Project />")
            projects = [project, peer]
            if locks and mode == "locked":
                for item in projects:
                    item.with_name("packages.lock.json").write_text('{"testFixture": true}')
                if missing_peer:
                    peer.with_name("packages.lock.json").unlink()
            calls = []

            def fake_run(command, **kwargs):
                calls.append(command)
                self.assertEqual(kwargs["cwd"], str(root))
                self.assertIs(kwargs["stderr"], subprocess.STDOUT)
                if start_error:
                    raise FileNotFoundError("fixture SDK absent")
                if len(calls) == fail_at:
                    kwargs["stdout"].write("fixture failure\n")
                    return subprocess.CompletedProcess(command, 23)
                if command[1] == "--version":
                    kwargs["stdout"].write(version + "\n")
                if command[1] == "restore" and locks and mode == "generate-locks":
                    # Deliberately invalid NuGet data in a disposable directory:
                    # tests assert orchestration, not lock authenticity.
                    for item in projects:
                        item.with_name("packages.lock.json").write_text('{"testFixture": true}')
                if command[1] == "test" and trx:
                    results = Path(command[command.index("--results-directory") + 1])
                    results.mkdir(parents=True)
                    (results / "fixture.trx").write_text(report if report is not None else
                        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                        '<ResultSummary outcome="Completed">'
                        '<Counters total="1" executed="1" passed="1" failed="0" />'
                        '</ResultSummary></TestRun>')
                return subprocess.CompletedProcess(command, 0)

            output = io.StringIO()
            with patch.object(pilot_checks, "ROOT", root), \
                 patch.object(pilot_checks, "PILOTS", {"dock": "Test.csproj"}), \
                 patch.object(pilot_checks.subprocess, "run", side_effect=fake_run), \
                 contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                code = pilot_checks.main(["--mode", mode, "--output", str(root / "evidence")])
            logs = {p.name: p.read_text() for p in (root / "evidence").rglob("*.log")}
            return code, calls, output.getvalue(), logs

    def test_generate_and_locked_are_separate_and_do_not_implicitly_restore(self):
        for mode in ("generate-locks", "locked"):
            with self.subTest(mode=mode):
                code, calls, output, logs = self.exercise(mode=mode)
                self.assertEqual(code, 0, output)
                self.assertEqual([c[1] for c in calls],
                                 ["--version", "--info", "restore", "format", "format", "build", "test"])
                restore = calls[2]
                self.assertEqual("--locked-mode" in restore, mode == "locked")
                self.assertEqual("--use-lock-file" in restore, mode == "generate-locks")
                self.assertIn("--configfile", restore)
                for command in calls[3:]:
                    self.assertIn("--no-restore", command)
                self.assertIn("--no-build", calls[-1])
                self.assertIn("Exit code: 0", logs["dock-test.log"])

    def test_each_command_failure_stops_immediately_and_keeps_log(self):
        for index in range(1, 8):
            with self.subTest(index=index):
                code, calls, output, logs = self.exercise(fail_at=index)
                self.assertEqual(code, 1)
                self.assertEqual(len(calls), index)
                self.assertNotIn("PASS:", output)
                self.assertTrue(any("fixture failure" in log and "Exit code: 23" in log
                                    for log in logs.values()))

    def test_locked_missing_peer_lock_fails_before_restore(self):
        code, calls, output, _ = self.exercise(mode="locked", missing_peer=True)
        self.assertEqual(code, 1)
        self.assertEqual(len(calls), 2)
        self.assertIn("dependency lock", output)
        self.assertIn("peer", output)

    def test_generate_missing_locks_and_missing_trx_fail(self):
        for options in ({"locks": False}, {"trx": False}):
            with self.subTest(options=options):
                code, _, output, _ = self.exercise(**options)
                self.assertEqual(code, 1)
                self.assertNotIn("PASS:", output)

    def test_wrong_sdk_and_missing_executable_do_not_restore(self):
        for options in ({"version": "9.0.100"}, {"start_error": True}):
            with self.subTest(options=options):
                code, calls, output, logs = self.exercise(**options)
                self.assertEqual(code, 1)
                self.assertEqual(len(calls), 1)
                self.assertNotIn("PASS:", output)
                self.assertIn("sdk-version.log", logs)

    def test_zero_exit_with_invalid_empty_or_failed_trx_does_not_pass(self):
        reports = [
            "<fixture />",
            "<TestRun>",
            '<TestRun><ResultSummary outcome="Completed">'
            '<Counters executed="0" /></ResultSummary></TestRun>',
            '<TestRun><ResultSummary outcome="Failed">'
            '<Counters executed="1" failed="1" /></ResultSummary></TestRun>',
            '<TestRun><ResultSummary outcome="Completed">'
            '<Counters executed="1" error="1" /></ResultSummary></TestRun>',
            '<TestRun><ResultSummary outcome="Completed">'
            '<Counters executed="invalid" /></ResultSummary></TestRun>',
        ]
        for report in reports:
            with self.subTest(report=report):
                code, calls, output, logs = self.exercise(report=report)
                self.assertEqual(code, 1, output)
                self.assertEqual(calls[-1][1], "test")
                self.assertNotIn("PASS:", output)
                self.assertIn("Exit code: 0", logs["dock-test.log"])


if __name__ == "__main__":
    unittest.main()

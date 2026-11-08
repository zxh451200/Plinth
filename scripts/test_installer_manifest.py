"""Synthetic publishing fixtures; do not substitute for a Windows installer run."""
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from installer_manifest import generate, is_link


class InstallerManifestTests(unittest.TestCase):
    def test_upgrade_backup_is_a_pre_overwrite_gate_and_silent_upgrade_stays_blocked(self):
        text = (Path(__file__).resolve().parents[1] / "installer/plinth.nsi").read_text(encoding="utf-8-sig")
        install = text.split('Section "Install"', 1)[1].split("SectionEnd", 1)[0]
        before, after = install.split("  install_begin:", 1)
        self.assertLess(before.index("IfSilent upgrade_silent"), before.index("ExecWait"))
        self.assertLess(before.index("Call AcquireInstallationLock"), before.index("ExecWait"))
        self.assertLess(before.index("CloseHandle(p $ExecutableLock)"), before.index("ExecWait"))
        invocation = before.split("  upgrade_backup_ready:", 1)[0]
        self.assertNotIn("Call ReleaseInstallationLock", invocation)
        self.assertIn('ExecWait \'"$INSTDIR\\Plinth.App.exe" --backup-before-upgrade "$UpgradeBackupPath"\' $0', before)
        self.assertIn("IfErrors upgrade_backup_failed", before)
        self.assertIn("IntCmp $0 0 upgrade_auto_result upgrade_select_backup upgrade_select_backup", before)
        self.assertIn('IfFileExists "$UpgradeBackupPath" upgrade_verify_backup upgrade_select_backup', before)
        self.assertIn("IntCmp $0 0 upgrade_backup_ready upgrade_backup_failed upgrade_backup_failed", before)
        self.assertLess(before.index('IfFileExists "$UpgradeBackupPath" upgrade_backup_failed 0'),
                        before.index("ExecWait"))
        self.assertRegex(before, r'Delete "\$UpgradeBackupPath"\s+IfErrors upgrade_backup_failed')
        ready = before.split("  upgrade_backup_ready:", 1)[1].split("  upgrade_backup_failed:", 1)[0]
        self.assertIn('FileOpen $1 "$UpgradeHelper\\backup-report.txt" r', ready)
        self.assertIn("IntCmp $ExecutableLock -1 upgrade_backup_failed", ready)
        silent = before.split("  upgrade_silent:", 1)[1].split("  upgrade_cancel:", 1)[0]
        self.assertIn("SetErrorLevel 6", silent)
        self.assertIn("Quit", silent)
        failure = before.split("  upgrade_backup_failed:", 1)[1].split("  upgrade_silent:", 1)[0]
        self.assertIn("SetErrorLevel 6", failure)
        self.assertIn("Quit", failure)
        self.assertNotIn('Delete "$UpgradeBackupPath"', failure)
        self.assertIn('StrCpy $InstallationStarted "1"', after)
        self.assertNotIn('Rename "$INSTDIR\\Plinth.App.exe"', before)
        self.assertIn('CopyFiles /SILENT "$INSTDIR\\deployment.json" "$UpgradeHelper\\deployment.json"', before)

    def test_history_and_missing_exe_use_complete_packaged_verifier_and_standard_picker(self):
        text = (Path(__file__).resolve().parents[1] / "installer/plinth.nsi").read_text(encoding="utf-8-sig")
        before = text.split('Section "Install"', 1)[1].split("  install_begin:", 1)[0]
        self.assertIn('SetOutPath "$UpgradeHelper"', before)
        self.assertIn('File /r /x plinth.install.lock "${PUBLISH_DIR}\\*.*"', before)
        self.assertIn('IfFileExists "$INSTDIR\\Plinth.App.exe" 0 upgrade_select_backup', before)
        self.assertIn('nsDialogs::SelectFileDialog open "" "Plinth ZIP backup|*.zip"', before)
        self.assertRegex(before, r'Pop \$UpgradeBackupPath\s+StrCmp \$UpgradeBackupPath "" upgrade_cancel')
        self.assertIn('ExecWait \'"$UpgradeHelper\\Plinth.App.exe" --inspect-backup-before-upgrade '
                      '"$UpgradeBackupPath" "$UpgradeHelper\\backup-report.txt"\' $0', before)
        verify = before.split("  upgrade_verify_backup:", 1)[1]
        self.assertLess(verify.index("--inspect-backup-before-upgrade"), verify.index("  upgrade_backup_confirm:"))
        self.assertIn('IfFileExists "$INSTDIR\\Plinth.App.exe" 0 upgrade_backup_confirm', verify)
        self.assertIn("MB_OKCANCEL|MB_ICONINFORMATION", verify)
        self.assertIn("IDCANCEL upgrade_cancel", verify)
        self.assertIn("IntOp $1 $0 & 0x410", before)
        self.assertIn("IntCmp $1 3 upgrade_no_deployment upgrade_backup_failed upgrade_backup_failed", before)

    def test_upgrade_backup_dispatch_precedes_the_desktop_installation_lock(self):
        root = Path(__file__).resolve().parents[1]
        program = (root / "src/Plinth.App/Program.cs").read_text(encoding="utf-8-sig")
        self.assertLess(program.index("CommandLine.Handle("), program.index("InstallationGuard.Acquire("))
        cli = (root / "src/Plinth.App/CommandLine.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"--backup-before-upgrade"', cli)
        self.assertNotIn("InstallationGuard.Acquire(", cli)

    def test_upgrade_backup_keeps_security_configuration_and_failure_codes(self):
        root = Path(__file__).resolve().parents[1]
        cli = (root / "src/Plinth.App/CommandLine.cs").read_text(encoding="utf-8-sig")
        self.assertIn("DatabaseConfiguration.Current.IsServer", cli)
        self.assertIn("AppPaths.DatabasePath", cli)
        self.assertIn("SecurityException", cli)
        automatic = cli.split("var service = new BackupService", 1)[1]
        self.assertLess(automatic.index("service.BackupToAsync("), automatic.index("BackupService.Inspect(destination)"))
        self.assertIn('or "--inspect-backup-before-upgrade" ? 8 : 1', cli)
        self.assertIn('ExecuteConfirmedAsync("backup:export", "Backup", "backup.inspectUpgrade"', cli)
        backup = (root / "src/Plinth.App/Services/BackupService.cs").read_text(encoding="utf-8-sig")
        self.assertIn('ExecuteConfirmedAsync("backup:export"', backup)
        self.assertIn("DatabaseInitializer.CreateConsistentCopy(source, copy)", backup)
        self.assertIn("File.Move(temp, destination, overwrite: false)", backup)
        # Never replace deployment/authentication settings to make a headless backup succeed.
        self.assertNotIn("SetEnvironmentVariable", cli)
        self.assertNotIn("ConfirmProductionAsync =", cli)

    def test_helper_config_read_errors_do_not_fall_back_to_development_or_sqlite(self):
        root = Path(__file__).resolve().parents[1]
        deployment = (root / "src/Plinth.App/Services/DeploymentConfiguration.cs").read_text(encoding="utf-8-sig")
        database = (root / "src/Plinth.App/Data/DatabaseConfiguration.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("File.Exists(", deployment)
        self.assertNotIn("File.Exists(", database)
        self.assertIn("var json = ReadOptionalConfiguration(path);", deployment)
        self.assertIn("DeploymentConfiguration.ReadOptionalConfiguration(userPath)", database)
        reader = deployment.split("internal static string? ReadOptionalConfiguration", 1)[1].split(
            "internal static DeploymentConfiguration BindOverrides", 1)[0]
        self.assertIn("File.ReadAllText(path)", reader)
        self.assertEqual(["FileNotFoundException", "DirectoryNotFoundException"],
                         re.findall(r"catch \((\w+)\)", reader))

    def test_nsis_upgrade_instruction_shapes_and_inherited_environment(self):
        text = (Path(__file__).resolve().parents[1] / "installer/plinth.nsi").read_text(encoding="utf-8-sig")
        install = text.split('Section "Install"', 1)[1].split("SectionEnd", 1)[0]
        self.assertRegex(install, r'GetTempFileName \$UpgradeBackupPath "\$LOCALAPPDATA\\Plinth-Upgrade-Backups"')
        self.assertNotIn("SetEnvironmentVariable", install)
        self.assertNotIn("ExecShell", install)
        self.assertIn("RequestExecutionLevel user", text)
        for line in text.splitlines():
            instruction = line.strip()
            if instruction.startswith(("IntCmp ", "IntCmpU ")):
                # NSIS requires two operands, one equal label, and at most two optional labels.
                self.assertIn(len(instruction.split()), (4, 5, 6), instruction)
        labels = set(re.findall(r"^\s*(\w+):", install, re.MULTILINE))
        for target in re.findall(r"\b(upgrade_\w+)\b", install):
            self.assertIn(target, labels)

    @unittest.skipUnless(shutil.which("makensis"), "makensis is not installed; NSIS compilation not executed")
    def test_nsis_compiles_with_a_generated_publish_manifest(self):
        root = Path(__file__).resolve().parents[1]
        manifest = self.publish / "manifest.nsh"
        manifest.write_text(generate(self.publish), encoding="utf-8")
        option = "/" if os.name == "nt" else "-"
        result = subprocess.run([
            shutil.which("makensis"), option + "V2",
            option + "DVERSION=0.0.0", option + "DPUBLISH_DIR=" + str(self.publish),
            option + "DOUTPUT_FILE=" + str(self.publish / "Setup.exe"),
            option + "DUNINSTALL_MANIFEST=" + str(manifest), str(root / "installer/plinth.nsi"),
        ], capture_output=True, text=True, timeout=60)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.publish = Path(self.temporary.name)
        (self.publish / "Plinth.App.exe").write_bytes(b"fixture")

    def test_exact_files_nested_directories_and_no_recursive_deletion(self):
        child = self.publish / "runtimes/win-x64/native"
        child.mkdir(parents=True)
        (child / "fixture.dll").write_bytes(b"fixture")
        (self.publish / "plinth.install.lock").write_text("marker")
        text = generate(self.publish)
        self.assertIn('Delete "$INSTDIR\\runtimes\\win-x64\\native\\fixture.dll"', text)
        self.assertNotIn('Delete "$INSTDIR\\plinth.install.lock"', text)
        self.assertNotIn("RMDir /r", text)
        self.assertLess(text.index('RMDir "$INSTDIR\\runtimes\\win-x64\\native"'),
                        text.index('RMDir "$INSTDIR\\runtimes"'))
        self.assertIn("GetFileAttributesW", text)
        self.assertIn("0x400", text)
        self.assertIn("Goto ${FailureLabel}", text)
        self.assertIn('GetFileAttributesW(w "$INSTDIR\\Plinth.App.exe")', text)
        self.assertIn('GetFileAttributesW(w "$INSTDIR\\Uninstall.exe")', text)
        self.assertIn('GetFileAttributesW(w "$INSTDIR\\plinth.install.lock")', text)
        self.assertLess(text.index("IfErrors uninstall_failed_locked"), text.index('RMDir "'))

    def test_missing_entry_is_blocked(self):
        (self.publish / "Plinth.App.exe").unlink()
        with self.assertRaisesRegex(ValueError, "Missing"):
            generate(self.publish)

    def test_user_data_and_deployment_config_are_not_packaged_for_deletion(self):
        for name in ("plinth.db", "backup.zip", "settings.json", "workspace-preferences.json", "deployment.json"):
            with self.subTest(name=name):
                path = self.publish / name
                path.write_text("fixture")
                with self.assertRaisesRegex(ValueError, "User data"):
                    generate(self.publish)
                path.unlink()

    def test_nsis_expansions_quotes_and_newlines_in_names_are_blocked(self):
        names = ["$TEMP.dll"]
        if os.name != "nt":
            names += ['bad"name.dll', "bad\nname.dll"]
        for name in names:
            with self.subTest(name=name):
                path = self.publish / name
                path.write_text("fixture")
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    generate(self.publish)
                path.unlink()

    def test_symlink_directory_is_blocked(self):
        with tempfile.TemporaryDirectory() as outside:
            try:
                (self.publish / "external").symlink_to(outside, target_is_directory=True)
            except OSError:
                self.skipTest("This account cannot create directory symlinks")
            with self.assertRaisesRegex(ValueError, "Unsafe"):
                generate(self.publish)

    def test_windows_junction_attributes_are_treated_as_links(self):
        with patch.object(Path, "is_symlink", return_value=False), patch.object(
                Path, "lstat", return_value=SimpleNamespace(st_file_attributes=0x400)):
            self.assertTrue(is_link(self.publish))


if __name__ == "__main__":
    unittest.main()

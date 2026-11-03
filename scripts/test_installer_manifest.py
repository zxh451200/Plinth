"""Synthetic publishing fixtures; do not substitute for a Windows installer run."""
from pathlib import Path
import os
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from installer_manifest import generate, is_link


class InstallerManifestTests(unittest.TestCase):
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

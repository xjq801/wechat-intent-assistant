import contextlib
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from tools import local_credentials


class CredentialPathTests(unittest.TestCase):
    def test_default_path_is_relative_to_project_not_working_directory(self):
        expected = Path(local_credentials.__file__).resolve().parent.parent / ".local" / "qwen_api_key.dpapi"
        self.assertEqual(local_credentials.default_secret_path(), expected)

    def test_missing_returns_empty(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(local_credentials.load_qwen_key(Path(directory) / "missing.dpapi"), "")

    def test_non_windows_has_clean_error_without_creating_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "key.dpapi"
            with patch.object(local_credentials.os, "name", "posix"):
                with self.assertRaisesRegex(RuntimeError, "require Windows DPAPI"):
                    local_credentials.save_qwen_key("dummy-test-token", path)
            self.assertFalse(path.exists())

    def test_invalid_input_is_not_echoed(self):
        for invalid in ("", "  ", "dummy-test-token\nanother-line"):
            with self.subTest(kind=bool(invalid)):
                with self.assertRaises(ValueError) as raised:
                    local_credentials.save_qwen_key(invalid)
                self.assertNotIn("dummy-test-token", str(raised.exception))

    def test_cli_rejects_command_line_secret_without_echo(self):
        output = io.StringIO()
        errors = io.StringIO()
        with patch.object(local_credentials.sys, "argv", ["local_credentials", "--save", "dummy-secret-in-argument"]):
            with patch.object(local_credentials, "save_qwen_key") as save:
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
                    self.assertEqual(local_credentials.main(), 2)
                save.assert_not_called()
        self.assertEqual(output.getvalue(), "")
        self.assertNotIn("dummy-secret", errors.getvalue())


@unittest.skipUnless(os.name == "nt", "Requires Windows DPAPI")
class WindowsCredentialTests(unittest.TestCase):
    def test_round_trip_replacement_and_no_plaintext_on_disk(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "nested" / "key.dpapi"
            first = "dummy-qwen-test-token-1234567890"
            second = "dummy-replacement-token-0987654321"
            local_credentials.save_qwen_key(first, path)
            self.assertEqual(local_credentials.load_qwen_key(path), first)
            ciphertext = path.read_bytes()
            self.assertNotIn(first.encode("utf-8"), ciphertext)
            self.assertNotIn(first.encode("utf-16-le"), ciphertext)
            local_credentials.save_qwen_key(second, path)
            self.assertEqual(local_credentials.load_qwen_key(path), second)
            self.assertNotEqual(ciphertext, path.read_bytes())
            self.assertEqual(list(path.parent.iterdir()), [path])
            self.assertNotIn(second.encode("utf-8"), path.read_bytes())

    def test_invalid_data_fails_without_exposing_content(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "key.dpapi"
            for data in (b"", b"dummy-sensitive-invalid-ciphertext"):
                path.write_bytes(data)
                with self.assertRaises(RuntimeError) as raised:
                    local_credentials.load_qwen_key(path)
                self.assertNotIn("dummy-sensitive", str(raised.exception))
                self.assertIsNone(raised.exception.__cause__)

    def test_failed_replace_preserves_existing_key_and_removes_temporary_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "key.dpapi"
            local_credentials.save_qwen_key("dummy-original-token", path)
            with patch.object(local_credentials.os, "replace", side_effect=OSError("dummy-private-error")):
                with self.assertRaises(RuntimeError) as raised:
                    local_credentials.save_qwen_key("dummy-new-token", path)
            self.assertNotIn("dummy-private", str(raised.exception))
            self.assertEqual(local_credentials.load_qwen_key(path), "dummy-original-token")
            self.assertEqual(list(path.parent.iterdir()), [path])

    def test_cli_reads_one_stdin_line_and_prints_only_status_and_path(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "key.dpapi"
            output = io.StringIO()
            with patch.object(local_credentials, "default_secret_path", return_value=path):
                with patch.object(local_credentials.sys, "argv", ["local_credentials", "--save"]):
                    with patch.object(local_credentials.sys, "stdin", io.StringIO("dummy-cli-token\nignored-line\n")):
                        with contextlib.redirect_stdout(output):
                            self.assertEqual(local_credentials.main(), 0)
            self.assertEqual(output.getvalue().splitlines(), ["SAVED", str(path)])
            self.assertEqual(local_credentials.load_qwen_key(path), "dummy-cli-token")


if __name__ == "__main__":
    unittest.main()

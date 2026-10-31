"""Exercise the real source-contract reader under a non-UTF8 default encoding."""
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import test_shell_contract as shell


class Utf8SourceReadTests(unittest.TestCase):
    def test_contract_reader_does_not_depend_on_cp936_default(self):
        original = Path.read_text
        def legacy_default(path, encoding=None, errors=None, **kwargs):
            return original(path, encoding=encoding or 'cp936', errors=errors, **kwargs)
        with tempfile.TemporaryDirectory() as directory:
            launcher = Path(directory)
            (launcher / 'MainWindow.xaml').write_bytes((shell.LAUNCHER / 'MainWindow.xaml').read_bytes())
            partial = launcher / 'MainWindow.DonorShell.cs'
            partial.write_text('// synthetic source: 中文 🛰\n', encoding='utf-8')
            with patch.object(Path, 'read_text', legacy_default):
                with self.assertRaises(UnicodeDecodeError):
                    partial.read_text()  # Negative control: the old implicit reader fails.
                with patch.object(shell, 'LAUNCHER', launcher):
                    result = unittest.TestResult()
                    shell.ShellContractTests('test_no_donor_update_network_or_authfile_routes').run(result)
                self.assertEqual(result.errors, [])
                self.assertEqual(result.failures, [])
                self.assertEqual(result.testsRun, 1)


if __name__ == '__main__':
    unittest.main()

"""Run installed status as an unprivileged observer of private runtime state."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(os.name == 'posix' and hasattr(os, 'geteuid') and os.geteuid() != 0,
                     'Requires a non-root POSIX observer')
class InstalledStatus(unittest.TestCase):
    def test_private_state_is_unknown_while_readable_and_missing_paths_are_distinct(self):
        with tempfile.TemporaryDirectory(prefix='site-status-') as temporary:
            root = Path(temporary)
            app = root / 'app'
            (app / 'wwwroot').mkdir(parents=True)
            for name in ('.laplace-source-revision', 'wwwroot/.laplace-web-source-revision'):
                (app / name).write_text('a' * 40)
            private = root / 'private'
            private.mkdir()
            (private / 'keys').mkdir()
            cache = root / 'cache.bin'
            cache.write_bytes(b'fixture')
            missing = root / 'absent-stockfish'
            secret = 'must-not-enter-status-output'
            (app / 'laplace-api.env').write_text(
                f'LAPLACE_DATA_PROTECTION_KEYS="{private}/keys"\n'
                f'LAPLACE_PERFCACHE_BIN="{cache}"\n'
                f'LAPLACE_STOCKFISH="{missing}"\n'
                f'LAPLACE_DB="Host=/fixture/socket;Database=fixture;Username=runtime;Password={secret}"\n')
            machine = root / 'machine.env'
            machine.write_text('LAPLACE_DECLARED=fixture\n')
            commands = root / 'commands'
            commands.mkdir()
            systemctl = commands / 'systemctl'
            systemctl.write_text('#!/bin/sh\nprintf "User=fixture\nActiveState=active\n"\n')
            systemctl.chmod(0o755)
            private.chmod(0)
            try:
                result = subprocess.run([sys.executable, str(ROOT / 'scripts/site-status.py'),
                    '--app-dir', str(app), '--machine-file', str(machine)], text=True, capture_output=True,
                    env=dict(os.environ, PATH=str(commands) + os.pathsep + os.environ['PATH']))
                self.assertEqual(result.returncode, 0, result.stderr)
                report = json.loads(result.stdout)
                self.assertNotIn(secret, result.stdout + result.stderr)
                self.assertEqual(report['paths']['LAPLACE_DATA_PROTECTION_KEYS'],
                    {'path': str(private / 'keys'), 'exists': None, 'inspection_error': 'PermissionError'})
                self.assertIs(report['paths']['LAPLACE_PERFCACHE_BIN']['exists'], True)
                self.assertIs(report['paths']['LAPLACE_STOCKFISH']['exists'], False)
                self.assertEqual(report['installed_revisions']['.laplace-source-revision'], 'a' * 40)
                self.assertEqual(report['connections']['LAPLACE_DB']['username'], 'runtime')
            finally:
                private.chmod(0o700)


if __name__ == '__main__':
    unittest.main()

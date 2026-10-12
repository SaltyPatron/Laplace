"""Validate generated service units with the host's systemd parser."""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('units', ROOT / 'deploy/linux/service-units.py')
units = importlib.util.module_from_spec(spec)
spec.loader.exec_module(units)


class ServiceUnits(unittest.TestCase):
    def test_alternate_paths_reach_every_service_and_restart_trigger(self):
        with tempfile.TemporaryDirectory() as temporary:
            prefix = Path(temporary) / 'installed mono %x $DATA'
            locks = Path(temporary) / 'request locks'
            config = {'LAPLACE_INSTALL_PREFIX': str(prefix), 'LAPLACE_LOCKS': str(locks),
                      'LAPLACE_DOTNET': '/usr/bin/true', 'LAPLACE_AGENT_USER': 'fixture-runner',
                      'LAPLACE_GROUP': 'fixture-build', 'LAPLACE_API_USER': 'fixture-api',
                      'LAPLACE_API_STATE_DIR': str(Path(temporary) / 'api state'),
                      'LAPLACE_MCP_USER': 'fixture-mcp', 'LAPLACE_PGSERVICE': 'fixture-pg'}
            rendered = units.render(config)
            self.assertEqual(len(rendered), 6)
            api = rendered['laplace-api.service']
            self.assertIn('User=fixture-api\nGroup=fixture-api', api)
            self.assertIn('Environment="TMPDIR=' + temporary + '/api state/tmp"', api)
            self.assertIn('After=network.target fixture-pg.service', api)
            self.assertIn('%%x $$DATA/app/Laplace.Endpoints.OpenAICompat.dll"', api)
            self.assertIn('PathChanged=' + str(locks) + '/restart-api\n', rendered['laplace-api-restart.path'])
            self.assertIn('User=fixture-mcp', rendered['laplace-mcp.service'])
            for name, content in rendered.items():
                self.assertNotIn('/opt/laplace', content)
                self.assertNotIn('/run/lock/laplace', content)
                (Path(temporary) / name).write_text(content)
            if shutil.which('systemd-analyze'):
                (Path(temporary) / 'fixture-pg.service').write_text('[Service]\nExecStart=/usr/bin/true\n')
                result = subprocess.run(['systemd-analyze', 'verify', '--man=no',
                                         *map(str, Path(temporary).glob('*.service')),
                                         *map(str, Path(temporary).glob('*.path'))],
                                        capture_output=True, text=True, timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr)

    def test_invalid_declarations_are_rejected_before_writing_units(self):
        for name, value in [('LAPLACE_INSTALL_PREFIX', 'relative'), ('LAPLACE_LOCKS', '/'),
                            ('LAPLACE_API_USER', 'root\nExecStart=/bin/false'),
                            ('LAPLACE_PGSERVICE', 'postgres\nWants=anything'),
                            ('LAPLACE_DOTNET', 'dotnet')]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                units.render({name: value})


if __name__ == '__main__':
    unittest.main()

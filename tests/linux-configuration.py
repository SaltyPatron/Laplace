"""Execute configuration import with real shell entry points and alternate declarations."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
READER = ROOT / 'machine-env.py' if (ROOT / 'laplace.env').is_file() else ROOT / 'scripts/machine-env.py'
spec = importlib.util.spec_from_file_location('machine', READER)
machine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(machine)


class LinuxConfiguration(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.file = Path(self.temporary.name) / 'alternate installation.env'
        self.sentinel = Path(self.temporary.name) / 'must-not-exist'

    def test_process_overrides_paths_references_comments_and_multiline_values(self):
        self.file.write_text('''# Paths are declarations, not executable shell.
LAPLACE_SRC="/different sources & trees"
LAPLACE_WORK=/machine/work # override must win before a dependent value is resolved
LAPLACE_DATA=${LAPLACE_WORK}/data
LAPLACE_ICU_DIR=/independent/icu
LAPLACE_VOLUMES="first line
second line"
''')
        process = {'LAPLACE_WORK': '/process/work'}
        resolved = dict(process, **machine.resolve(self.file, process, True))
        self.assertEqual(resolved['LAPLACE_WORK'], '/process/work')
        self.assertEqual(resolved['LAPLACE_DATA'], '/process/work/data')
        self.assertEqual(resolved['LAPLACE_SRC'], '/different sources & trees')
        self.assertEqual(resolved['LAPLACE_VOLUMES'], 'first line\nsecond line')
        env = {key: value for key, value in os.environ.items() if not key.startswith('LAPLACE_')}
        env.update(LAPLACE_MACHINE_ENV=str(self.file), SETVARS_COMPLETED='1', **process)
        keys = ['LAPLACE_SRC', 'LAPLACE_WORK', 'LAPLACE_DATA', 'LAPLACE_ICU_DIR', 'LAPLACE_VOLUMES', 'LAPLACE_CONFIG_SOURCE']
        printer = 'import json,os,sys; print(json.dumps({k:os.environ[k] for k in sys.argv[1:]}))'
        for shell in ('bash', 'dash'):
            if (ROOT / 'laplace.env').exists():
                command = '. "$1"; shift; python3 -c "$1" "${@:2}"' if shell == 'bash' else '. "$1"; shift; p=$1; shift; python3 -c "$p" "$@"'
                args = [shell, '-ec', command, str(ROOT / 'build.sh'), str(ROOT / 'laplace.env'), printer, *keys]
            else:
                args = [shell, '-ec', 'v=$(python3 "$1"); eval "$v"; shift; p=$1; shift; python3 -c "$p" "$@"',
                        'configuration', str(READER), printer, *keys]
            result = subprocess.run(args, env=env, text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout), {key: resolved[key] for key in keys})

    def test_command_substitutions_remain_literal(self):
        value = f'$(touch {self.sentinel}) `touch {self.sentinel}`'
        self.file.write_text("LAPLACE_WORK='" + value + "'\n")
        env = dict(os.environ, LAPLACE_MACHINE_ENV=str(self.file))
        env.pop('LAPLACE_WORK', None)
        result = subprocess.run(['bash', '-ec', 'v=$(python3 "$1"); eval "$v"; printf "%s" "$LAPLACE_WORK"',
                                 'literal', str(READER)], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, value)
        self.assertFalse(self.sentinel.exists())

    def test_invalid_duplicate_and_undefined_declarations_fail_before_import(self):
        for text in ('A=one\nA=two\n', 'touch ' + str(self.sentinel), 'A=${UNDEFINED_MACHINE_TEST}\n', 'A="unfinished'):
            with self.subTest(text=text):
                self.file.write_text(text)
                with self.assertRaises(ValueError):
                    machine.resolve(self.file, {}, True)
        self.assertFalse(self.sentinel.exists())

    def test_explicit_missing_file_is_not_a_default_installation(self):
        with self.assertRaises(ValueError):
            machine.resolve(self.file, {}, True)
        self.assertEqual(machine.resolve(self.file, {}, False), {'LAPLACE_CONFIG_SOURCE': 'defaults'})


if __name__ == '__main__':
    unittest.main()

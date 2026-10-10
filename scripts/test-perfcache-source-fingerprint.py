"""Exercise the real CMake fingerprint against checkout and source changes."""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'engine/core/tools/ucd_tables_emit/source-fingerprint.cmake'


class GeneratorFingerprintTests(unittest.TestCase):
    def fingerprint(self, inputs):
        with tempfile.TemporaryDirectory(prefix='laplace-fingerprint-') as directory:
            root = Path(directory)
            paths = []
            for i, content in enumerate(inputs):
                path = root / f'source {i}.cpp'
                if content is not None:
                    path.write_bytes(content)
                paths.append(f'"{path.as_posix()}"')
            output = root / 'result.txt'
            script = root / 'test.cmake'
            script.write_text(
                f'include("{MODULE.as_posix()}")\n'
                f'laplace_generator_fingerprint(result {" ".join(paths)})\n'
                f'file(WRITE "{output.as_posix()}" "${{result}}")\n', encoding='utf-8')
            result = subprocess.run(['cmake', '-P', str(script)], capture_output=True, text=True)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return output.read_text()

    def test_checkout_line_endings_preserve_existing_linux_identity(self):
        sources = [b'int seed = 42;\n// semicolons; and ${cmake} remain text\n', b'float coordinate = .5;\n']
        mix = ''.join(hashlib.sha256(source).hexdigest() for source in sources)
        expected = hashlib.sha256(mix.encode()).hexdigest()[:16]
        self.assertEqual(expected, self.fingerprint(sources))
        self.assertEqual(expected, self.fingerprint([source.replace(b'\n', b'\r\n') for source in sources]))

    def test_actual_source_change_invalidates_identity(self):
        self.assertNotEqual(self.fingerprint([b'int seed = 42;\n']), self.fingerprint([b'int seed = 43;\n']))

    def test_source_order_is_preserved(self):
        self.assertNotEqual(self.fingerprint([b'first\n', b'second\n']), self.fingerprint([b'second\n', b'first\n']))

    def test_missing_source_fails_configuration(self):
        with self.assertRaises(RuntimeError):
            self.fingerprint([b'present\n', None])


if __name__ == '__main__':
    unittest.main()

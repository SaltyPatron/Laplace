#!/usr/bin/env python3
"""Exercise the production managed resolver against real ELF dependency loading.

The compiled fixture libraries deliberately have the same SONAMEs in separate
application/external trees. Their pointer-valued exports expose whether native
dependencies and managed imports reached the same core instance. This proves
loader ownership only; it does not substitute for the full native engine tests.
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent.parent
RESOLVER = ROOT / "app/Laplace.Core/Core/NativeLibraryClosure.cs"
PROBE = r'''
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("laplace_core", EntryPoint="core_identity")]
    static extern IntPtr Core();
    [DllImport("laplace_dynamics", EntryPoint="dynamics_core_identity")]
    static extern IntPtr Dynamics();
    [DllImport("laplace_synthesis", EntryPoint="synthesis_core_identity")]
    static extern IntPtr Synthesis();

    static int Main(string[] args)
    {
        if (args[0] == "missing")
        {
            try { Dynamics(); return 21; }
            catch (DllNotFoundException) { Console.WriteLine("MISSING_APP_CORE_REFUSED"); return 0; }
        }
        if (args[0] == "concurrent")
            Parallel.Invoke(() => Core(), () => Dynamics(), () => Synthesis());
        else if (args[0] == "dynamics") Dynamics();
        else if (args[0] == "synthesis") Synthesis();
        else Core();
        var core = Core();
        if (core != Dynamics() || core != Synthesis())
        {
            Console.Error.WriteLine("native dependency and managed core pointers differ"); return 22;
        }
        using var process = Process.GetCurrentProcess();
        foreach (string library in new[] {"core", "dynamics", "synthesis"})
        {
            var paths = process.Modules.Cast<ProcessModule>()
                .Select(m => m.FileName)
                .Where(p => Path.GetFileName(p).StartsWith($"liblaplace_{library}.so", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToArray();
            string expected = Path.Combine(AppContext.BaseDirectory, $"liblaplace_{library}.so");
            if (paths.Length != 1 || paths[0] != expected)
            {
                Console.Error.WriteLine($"{library}: {string.Join(", ", paths)}; expected {expected}"); return 23;
            }
        }
        Console.WriteLine("ONE_APP_LOCAL_CLOSURE " + args[0]); return 0;
    }
}
'''


@unittest.skipUnless(sys.platform == "linux", "ELF loader contract")
class NativeLibraryClosureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="laplace-native-closure-")
        cls.root = Path(cls.temp.name)
        cls.external = cls.root / "external"
        cls.external.mkdir()
        cls.app = cls.root / "app"
        cls.app.mkdir()
        cls.dotnet = shutil.which("dotnet")
        cls.cc = shutil.which("cc")
        if not cls.dotnet or not cls.cc:
            raise RuntimeError("native closure proof requires dotnet and a C compiler")
        sources = {
            "core": "static int identity; void *core_identity(void) { return &identity; }\n",
            "dynamics": "extern void *core_identity(void); void *dynamics_core_identity(void) { return core_identity(); }\n",
            "synthesis": "extern void *dynamics_core_identity(void); void *synthesis_core_identity(void) { return dynamics_core_identity(); }\n",
        }
        for name, body in sources.items():
            source = cls.root / (name + ".c")
            source.write_text(body)
            dependencies = [] if name == "core" else ["-Wl,--no-as-needed", "-l:liblaplace_" + ("core" if name == "dynamics" else "dynamics") + ".so.0"]
            # As on the host, the build tree is present in RUNPATH and externally
            # selected by LD_LIBRARY_PATH. $ORIGIN alone cannot defeat that order.
            cls.command([cls.cc, "-shared", "-fPIC", str(source), "-o", str(cls.external / f"liblaplace_{name}.so.0"),
                         f"-Wl,-soname,liblaplace_{name}.so.0", "-Wl,-rpath,$ORIGIN:" + str(cls.external),
                         "-L" + str(cls.external), *dependencies])
            # Real, distinct copies reproduce MSBuild's dereferenced source aliases.
            shutil.copyfile(cls.external / f"liblaplace_{name}.so.0", cls.app / f"liblaplace_{name}.so")
            shutil.copyfile(cls.external / f"liblaplace_{name}.so.0", cls.app / f"liblaplace_{name}.so.0")
        (cls.root / "Program.cs").write_text(PROBE)
        cls.build("fixed", RESOLVER)
        cls.build("baseline", None)

    @classmethod
    def command(cls, args, **kwargs):
        result = subprocess.run(args, text=True, capture_output=True, timeout=180, **kwargs)
        if result.returncode:
            raise AssertionError(f"command failed: {args}\n{result.stdout}\n{result.stderr}")
        return result

    @classmethod
    def build(cls, name, resolver):
        directory = cls.root / name
        directory.mkdir()
        include = "" if resolver is None else '<Compile Include="' + escape(str(resolver), {'"': '&quot;'}) + '" />'
        (directory / "probe.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
            '</PropertyGroup><ItemGroup><Compile Include="../Program.cs" />' + include + '</ItemGroup></Project>')
        cls.command([cls.dotnet, "build", str(directory / "probe.csproj"), "-c", "Release", "--nologo",
                     "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false", "-o", str(cls.app / name)])
        for source in cls.app.glob("*.so*"):
            shutil.copyfile(source, cls.app / name / source.name)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def probe(self, build, order):
        env = dict(os.environ, LD_LIBRARY_PATH=str(self.external))
        return subprocess.run([self.dotnet, str(self.app / build / "probe.dll"), order],
                              env=env, text=True, capture_output=True, timeout=30)

    def test_dependency_first_and_core_first_share_exact_app_artifacts(self):
        for order in ("core", "dynamics", "synthesis", "concurrent"):
            with self.subTest(order=order):
                result = self.probe("fixed", order)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("ONE_APP_LOCAL_CLOSURE " + order, result.stdout)

    def test_real_baseline_dependency_first_loads_two_core_instances(self):
        result = self.probe("baseline", "dynamics")
        self.assertEqual(22, result.returncode, result.stdout + result.stderr)
        self.assertIn("native dependency and managed core pointers differ", result.stderr)

    def test_missing_app_core_does_not_fall_back_to_external_core(self):
        path = self.app / "fixed/liblaplace_core.so"
        saved = path.with_suffix(".saved")
        path.rename(saved)
        try:
            result = self.probe("fixed", "missing")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("MISSING_APP_CORE_REFUSED", result.stdout)
        finally:
            saved.rename(path)


if __name__ == "__main__":
    unittest.main(verbosity=2)

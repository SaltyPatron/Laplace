#!/usr/bin/env python3
"""Supply the Qt SDK required by the shared CuteChess source build."""
import argparse
import json
import os
from pathlib import Path
import platform
import subprocess
import sys


def run(arguments):
    result = subprocess.run([str(arg) for arg in arguments], stdout=sys.stderr, stderr=sys.stderr, timeout=1800)
    if result.returncode:
        raise RuntimeError(f"{arguments[0]} exited with code {result.returncode}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True, help='existing shared Qt SDK installation root')
    parser.add_argument('--work', type=Path, required=True, help='build volume for Python/tool scratch')
    args = parser.parse_args()
    lock = json.loads((Path(__file__).resolve().parents[1] / 'deploy/cutechess-release.json').read_text())
    try:
        system = platform.system()
        if system not in ('Linux', 'Windows') or platform.machine().lower() not in ('x86_64', 'amd64'):
            raise RuntimeError(f'No configured Qt SDK for {system}/{platform.machine()}; configure CMAKE_PREFIX_PATH explicitly')
        windows = system == 'Windows'
        sdk = args.root.resolve() / lock['qt_version'] / ('msvc2022_64' if windows else 'gcc_64')
        required = ['Qt6', 'Qt6Core', 'Qt6Gui', 'Qt6Widgets', 'Qt6Concurrent',
                    'Qt6PrintSupport', 'Qt6Core5Compat', 'Qt6Svg']
        if not all((sdk / f'lib/cmake/{module}/{module}Config.cmake').is_file() for module in required):
            args.work.mkdir(parents=True, exist_ok=True)
            args.root.mkdir(parents=True, exist_ok=True)
            for name in ('TMPDIR', 'TMP', 'TEMP'):
                os.environ[name] = str(args.work.resolve())
            venv = args.work / f'aqt-{lock["aqtinstall_version"]}'
            python = venv / ('Scripts/python.exe' if windows else 'bin/python')
            if not python.is_file():
                run([sys.executable, '-m', 'venv', venv])
            run([python, '-m', 'pip', 'install', '--disable-pip-version-check', f'aqtinstall=={lock["aqtinstall_version"]}'])
            run([python, '-m', 'aqt', 'install-qt', 'windows' if windows else 'linux', 'desktop',
                 lock['qt_version'], 'win64_msvc2022_64' if windows else 'linux_gcc_64',
                 '-O', args.root, '-m', *lock['qt_modules']])
        if not all((sdk / f'lib/cmake/{module}/{module}Config.cmake').is_file() for module in required):
            raise RuntimeError(f'Qt {lock["qt_version"]} SDK incomplete: {sdk}')
        # qmake loads the SDK's real Qt runtime, detecting incompatible libc or
        # absent host libraries before CMake produces an unusable installation.
        qmake = sdk / 'bin' / ('qmake.exe' if windows else 'qmake')
        actual = subprocess.run([str(qmake), '-query', 'QT_VERSION'], capture_output=True, text=True, timeout=15)
        if actual.returncode or actual.stdout.strip() != lock['qt_version']:
            raise RuntimeError(f'Qt {lock["qt_version"]} cannot run on this host: {actual.stderr.strip()}')
        print(sdk)
        return 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f'Qt provisioning: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())

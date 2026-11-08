#!/usr/bin/env python3
"""Provision the locked CuteChess source and verify the executable's Qt closure."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil  # noqa: F401
import stat
import subprocess
import sys
import tempfile
import urllib.error  # noqa: F401
import urllib.request  # noqa: F401
import zipfile  # noqa: F401

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from installation_paths import configured
from lib.chess_source_integrity import verify_checkout  # noqa: F401
from provision_cutechess_helpers import (  # noqa: F401
    EVALUATION_BEGIN,
    EVALUATION_END,
    GUI_MODULES,
    LOCK,
    _desktop_exec,
    _publish_desktop_text,
    calibration_configuration,
    calibration_machine,
    check_latest,
    desktop_perfcache,
    digest,
    evaluation_configuration,
    gui_environment,
    gui_inventory,
    provision,
    register_desktop,
    reset_build_cache,
    run,
    verify_source,
    without_evaluation_defaults,
)


def probe(binary, lock, qt_version=None):
    # Executing the process checks the dynamic loader, Core and Core5Compat;
    # directory existence cannot establish a working Qt runtime.
    output = run([binary, "--version"])
    if not re.search(r"^cutechess-cli " + re.escape(lock["version"]) + r"\s*$", output, re.M):
        raise RuntimeError(f"{binary} is not locked CuteChess {lock['version']}: {output[:800]}")
    match = re.search(r"Using Qt version (\d+\.\d+\.\d+)", output)
    if not match or tuple(map(int, match[1].split('.'))) < (6, 8, 0):
        raise RuntimeError(f"{binary} requires a working Qt >=6.8 runtime: {output[:800]}")
    qt_version = qt_version or lock.get("qt_version")
    if qt_version and match[1] != qt_version:
        raise RuntimeError(f"{binary} uses Qt {match[1]}; expected {qt_version}")
    return {"component": "cutechess", "version": lock["version"],
            "qt_version": match[1], "path": str(binary), "ready": True}


def verify_build(source, binary, lock, qt_version=None, receipt_path=None):
    """Verify post-build source and the tested binary before publication."""
    source = source.resolve(strict=True)
    binary = binary.resolve(strict=True)
    verify_source(source, lock)
    before = digest(binary)
    runtime = probe(binary, lock, qt_version)
    integrity = verify_source(source, lock)
    if digest(binary) != before:
        raise RuntimeError("CuteChess executable changed during the runtime probe")
    cache = binary.parent / "CMakeCache.txt"
    receipt = {"schema": "laplace.cutechess-source-build.v1", "repository": lock["repository"],
               "commit": lock["commit"], "source": str(source), "source_integrity": integrity,
               "binary": str(binary), "binary_sha256": before, "runtime": runtime,
               "cmake_cache_sha256": digest(cache) if cache.is_file() else None,
               "scope": "post-build committed source bytes/modes and probed executable identity"}
    if receipt_path is not None:
        receipt_path = receipt_path.absolute()
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".cutechess-receipt-", dir=receipt_path.parent) as temporary:
            pending = Path(temporary) / "receipt.json"
            pending.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            os.replace(pending, receipt_path)
    return receipt


def probe_gui(binary, lock, qt_prefix, work=None):
    """Run upstream QApplication initialization without opening a desktop window."""
    if binary.is_symlink() or not binary.is_file():
        raise RuntimeError(f"GUI executable must be a regular direct build/install file: {binary}")
    binary = binary.resolve(strict=True)
    inventory = gui_inventory(qt_prefix, lock)
    before = digest(binary)
    scratch = Path(work) if work else configured("LAPLACE_WORK_ROOT") / "chess-tools"
    scratch.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env.update(gui_environment(qt_prefix))
    env.update({"QT_QPA_PLATFORM": "offscreen", "QT_DEBUG_PLUGINS": "1",
                "QT_FORCE_STDERR_LOGGING": "1",
                "QT_LOGGING_RULES": "qt.core.library.debug=true;qt.core.plugin.*.debug=true",
                "QT_MESSAGE_PATTERN": "%{category}: %{message}"})
    for key in ("DISPLAY", "WAYLAND_DISPLAY", "QT_QPA_PLATFORMTHEME", "QT_QPA_GENERIC_PLUGINS"):
        env.pop(key, None)
    with tempfile.TemporaryDirectory(prefix="cutechess-gui-probe-", dir=scratch) as temporary:
        root = Path(temporary)
        for key, name in (("XDG_CONFIG_HOME", "config"), ("XDG_CONFIG_DIRS", "config-dirs"),
                          ("XDG_DATA_HOME", "data"), ("XDG_DATA_DIRS", "data-dirs"),
                          ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime")):
            path = root / name
            path.mkdir(mode=0o700)
            env[key] = str(path)
        # Upstream constructs CuteChessApplication/QApplication before --version.
        # This proves the offscreen QApplication/platform loader, not an event loop.
        result = subprocess.run([str(binary), "-platform", "offscreen", "--version"],
                                stdin=subprocess.DEVNULL, capture_output=True, text=True,
                                timeout=30, env=env, cwd=root)
    if result.returncode:
        raise RuntimeError(f"CuteChess GUI initialization failed ({result.returncode}): {result.stderr[-2000:]}")
    if not re.search(r"^Cute Chess " + re.escape(lock["version"]) + r"\s*$", result.stdout, re.M):
        raise RuntimeError(f"GUI is not locked Cute Chess {lock['version']}: {result.stdout[:800]}")
    if not re.search(r"^Using Qt version " + re.escape(lock["qt_version"]) + r"\s*$", result.stdout, re.M):
        raise RuntimeError(f"GUI does not use selected Qt {lock['qt_version']}: {result.stdout[:800]}")
    loaded = set()
    for match in re.finditer(r'^qt\.core\.library:\s+("(?:[^"\\]|\\.)*")\s+loaded library\s*$', result.stderr, re.M):
        path = Path(json.loads(match[1]))
        if path.name in ("libqoffscreen.so", "qoffscreen.dll"):
            loaded.add(str(path.resolve(strict=True)))
    expected = str(Path(inventory["offscreen"]["path"]).resolve(strict=True))
    if loaded != {expected}:
        raise RuntimeError(f"GUI did not load the selected offscreen plugin: expected {expected}, observed {sorted(loaded)}")
    if digest(binary) != before or gui_inventory(qt_prefix, lock) != inventory:
        raise RuntimeError("GUI executable or selected Qt inputs changed during the runtime probe")
    return {"component": "cutechess-gui", "version": lock["version"], "qt_version": lock["qt_version"],
            "path": str(binary), "binary_sha256": before, "ready": True, "status": "ready-headless",
            "qapplication_initialized": True, "qt": inventory, "loaded_platform_plugin": expected,
            "plugin_diagnostics_sha256": hashlib.sha256(result.stderr.encode()).hexdigest(),
            "scope": "official GUI QApplication initialization and offscreen platform loading through --version",
            "settings_scope": "isolated XDG directories" if sys.platform.startswith("linux") else
                              "temporary XDG directories; platform native settings are not isolated",
            "interactive_desktop_tested": False, "interactive_desktop_ready": None,
            "direct_launch": {"argv": [str(binary)], "environment": gui_environment(qt_prefix)}}


def verify_gui_build(source, binary, lock, qt_prefix, receipt_path, work=None):
    source = source.resolve(strict=True)
    verify_source(source, lock)
    runtime = probe_gui(binary, lock, qt_prefix, work)
    integrity = verify_source(source, lock)
    if digest(binary) != runtime["binary_sha256"]:
        raise RuntimeError("CuteChess GUI executable changed after its runtime probe")
    cache = binary.parent / "CMakeCache.txt"
    receipt = {"schema": "laplace.cutechess-gui-source-build.v1", "repository": lock["repository"],
               "commit": lock["commit"], "source": str(source), "source_integrity": integrity,
               "build_target": "gui", "binary": str(binary.resolve(strict=True)),
               "binary_sha256": runtime["binary_sha256"], "runtime": runtime,
               "cmake_cache_sha256": digest(cache) if cache.is_file() else None}
    if receipt_path:
        receipt_path = receipt_path.absolute()
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".cutechess-gui-receipt-", dir=receipt_path.parent) as temporary:
            pending = Path(temporary) / "receipt.json"
            pending.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            os.replace(pending, receipt_path)
    return receipt


def verify_gui_install(binary, receipt_path, lock, work=None):
    """Bind the direct installed executable and selected SDK to a verified build."""
    receipt_bytes = receipt_path.read_bytes()
    receipt = json.loads(receipt_bytes)
    if (receipt.get("schema") != "laplace.cutechess-gui-source-build.v1" or
            receipt.get("repository") != lock["repository"] or receipt.get("commit") != lock["commit"] or
            receipt.get("build_target") != "gui"):
        raise RuntimeError("GUI build receipt does not match the selected official source")
    if binary.is_symlink() or not binary.is_file() or digest(binary) != receipt.get("binary_sha256"):
        raise RuntimeError("GUI installed executable differs from the retained official build")
    qt = receipt["runtime"]["qt"]
    qt_prefix = Path(qt["prefix"])
    if gui_inventory(qt_prefix, lock) != qt:
        raise RuntimeError("GUI selected Qt inputs differ from the retained build")
    runtime = probe_gui(binary, lock, qt_prefix, work)
    if runtime["binary_sha256"] != receipt["binary_sha256"]:
        raise RuntimeError("GUI installed executable changed during verification")
    if receipt_path.read_bytes() != receipt_bytes:
        raise RuntimeError("GUI retained build receipt changed during verification")
    return {"build_receipt": str(receipt_path), "build_receipt_sha256": hashlib.sha256(receipt_bytes).hexdigest(),
            "repository": receipt["repository"], "commit": receipt["commit"], "source": receipt["source"],
            "build_target": "gui", "runtime": runtime}


def desktop_calibration(path, expected_sha256, stockfish):
    if not re.fullmatch(r"[0-9a-f]{64}", expected_sha256 or ""):
        raise ValueError("select the exact completed calibration SHA256")
    path = Path(path)
    with path.open("rb") as stream:
        raw = stream.read((32 << 20) + 1)
    if len(raw) > 32 << 20 or hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise ValueError("selected calibration report differs")
    report = json.loads(raw)
    spec = importlib.util.spec_from_file_location(
        "cutechess_machine_calibration", Path(__file__).with_name("benchmark-chess-environment.py"))
    benchmark = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(benchmark)
    options, receipt = calibration_configuration(report, benchmark.source_identity(stockfish),
                                                 benchmark.machine(), benchmark.recommendations(report))
    receipt.update(reportSha256=expected_sha256, producerSchema=report["schema"],
                   startedUtc=report["started_utc"], finishedUtc=report["finished_utc"])
    return options, receipt, raw


def install_desktop(prefix, verified, stockfish, work_root=None, calibration_report=None, calibration_sha256=None):
    """Install public user-session launch files from the installed GUI verifier."""
    if not sys.platform.startswith("linux"):
        raise RuntimeError("desktop launcher installation requires Linux")
    prefix = prefix.resolve()
    runtime = verified["runtime"]
    direct = runtime["direct_launch"]
    binary = Path(runtime["path"])
    sdk = Path(runtime["qt"]["prefix"])
    library = str(sdk / "lib")
    expected = {"QT_PLUGIN_PATH": str(sdk / "plugins"),
                "QT_QPA_PLATFORM_PLUGIN_PATH": str(sdk / "plugins/platforms")}
    if (not runtime.get("ready") or direct.get("argv") != [str(binary)]
            or binary.is_symlink() or not binary.is_file()
            or digest(binary) != runtime["binary_sha256"]
            or any(direct["environment"].get(key) != value for key, value in expected.items())
            or direct["environment"].get("LD_LIBRARY_PATH", "").split(os.pathsep)[0] != library):
        raise RuntimeError("desktop launch selection differs from installed GUI verification")
    if not Path("/usr/bin/python3").is_file() or not os.access("/usr/bin/python3", os.X_OK):
        raise RuntimeError("system desktop launcher requires /usr/bin/python3")
    launcher = prefix / "bin/laplace-cutechess"
    desktop = prefix / "share/applications/laplace-cutechess.desktop"
    manifest = prefix / "share/laplace/cutechess-desktop.json"
    stockfish = Path(stockfish).resolve(strict=True)
    if not stockfish.is_file() or not os.access(stockfish, os.X_OK):
        raise RuntimeError("selected installed Stockfish executable is unavailable")
    selected_options = []
    calibration = {"status": "not-selected", "scope": "existing explicit user options or engine defaults"}
    explicit_calibration = calibration_report is not None
    if calibration_report is None and manifest.is_file() and not manifest.is_symlink():
        prior = json.loads(manifest.read_text()).get("calibration", {})
        if prior.get("status") == "selected":
            calibration_report, calibration_sha256 = prior["report"], prior["reportSha256"]
    if calibration_report is not None:
        try:
            selected_options, calibration, raw = desktop_calibration(calibration_report, calibration_sha256, stockfish)
        except (OSError, ValueError, KeyError, TypeError):
            if explicit_calibration:
                raise
            calibration = {"status": "stale", "reportSha256": calibration_sha256,
                           "scope": "existing user options or engine defaults; remeasure this installed engine"}
        else:
            retained = prefix / "share/laplace/chess-calibrations" / calibration_sha256 / "report.json"
            if retained.exists() and (retained.is_symlink() or retained.read_bytes() != raw):
                raise ValueError("retained calibration identity changed")
            _publish_desktop_text(retained, raw.decode("utf-8"), 0o644)
            calibration["report"] = str(retained)
    perfcache = desktop_perfcache(prefix)
    work_root = Path(work_root).absolute() if work_root else configured("LAPLACE_WORK_ROOT")
    if not work_root.is_dir():
        raise RuntimeError("existing permanent desktop work root is unavailable")
    engine_catalog = prefix / "share/laplace/cutechess-engines.json"
    session_helper = prefix / "share/laplace/cutechess-user-engines.py"
    helper_source = Path(__file__).with_name("cutechess-user-engines.py").read_text(encoding="utf-8")
    stockfish_selection = {"binary": str(stockfish), "sha256": digest(stockfish)}
    catalog = [{"name": "Stockfish (official)", "command": str(stockfish), "protocol": "uci"},
               {"name": "Laplace (substrate)", "command": str(prefix / "app/laplace-uci"), "protocol": "uci"}]
    if selected_options:
        catalog[0]["options"] = selected_options
    _publish_desktop_text(engine_catalog, json.dumps(catalog, indent=2) + "\n", 0o644)
    _publish_desktop_text(session_helper, helper_source, 0o644)
    selection = {"argv": [str(binary)], "binary_sha256": runtime["binary_sha256"],
                 "stockfish": stockfish_selection,
                 "previous_stockfish_command": str(prefix / "bin/laplace-cutechess-stockfish"),
                 "environment": expected, "qt_library_path": library,
                 "session_helper": str(session_helper), "session_helper_sha256": digest(session_helper),
                 "engine_catalog": str(engine_catalog), "engine_catalog_sha256": digest(engine_catalog),
                 "work_root": str(work_root), "t0_perfcache": str(perfcache),
                 "chess_floor_root": str(prefix / "share/laplace/chess-floor")}
    # Preserve the invoking user's display, settings and extra search paths.
    # Do not publish provisioning-process environment or read the API's secrets.
    program = '''#!/usr/bin/env python3
"""Launch the installed CuteChess GUI in the invoking user's desktop session."""
import hashlib
import importlib.util
import os
from pathlib import Path
import sys

SELECTION = ''' + repr(selection) + '''

def main():
    binary = Path(SELECTION["argv"][0])
    digest = hashlib.sha256()
    if binary.is_symlink() or not binary.is_file():
        raise RuntimeError("installed CuteChess executable is unavailable")
    with binary.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    if digest.hexdigest() != SELECTION["binary_sha256"]:
        raise RuntimeError("installed CuteChess changed; rerun chess provisioning")
    stockfish = Path(SELECTION["stockfish"]["binary"])
    digest = hashlib.sha256()
    with stockfish.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    if digest.hexdigest() != SELECTION["stockfish"]["sha256"]:
        raise RuntimeError("selected official Stockfish changed; rerun chess provisioning")
    for role in ("session_helper", "engine_catalog"):
        path = Path(SELECTION[role])
        if path.is_symlink() or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != SELECTION[role + "_sha256"]:
            raise RuntimeError("installed CuteChess engine configuration changed; rerun chess provisioning")
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("laplace_cutechess_user_engines", SELECTION["session_helper"])
    session = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(session)
    # The inheritable lock remains held by the GUI for its whole lifetime.
    session_lock, session_environment, _ = session.prepare(
        SELECTION["engine_catalog"], binary, SELECTION["work_root"],
        previous_stockfish=SELECTION["previous_stockfish_command"])
    environment = session.launch_environment(SELECTION, session_environment)
    os.execve(str(binary), [*SELECTION["argv"], *sys.argv[1:]], environment)

if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        print("CuteChess: " + str(error), file=sys.stderr)
        raise SystemExit(1)
'''
    entry = ("[Desktop Entry]\nType=Application\nName=Cute Chess (Laplace)\n"
             "Comment=Play and analyze chess with the installed Cute Chess GUI\n"
             "Exec=/usr/bin/python3 " + _desktop_exec(launcher) + "\nTerminal=false\n"
             "Categories=Game;BoardGame;\nStartupNotify=true\n")
    _publish_desktop_text(launcher, program, 0o755)
    _publish_desktop_text(desktop, entry, 0o644)
    system = Path("/usr/local/share/applications/laplace-cutechess.desktop")
    result = {"schema": "laplace.cutechess-desktop-install/v1",
              "binary": str(binary), "binary_sha256": runtime["binary_sha256"],
              "qt_prefix": str(sdk), "build_receipt_sha256": verified["build_receipt_sha256"],
              "launcher": {"path": str(launcher), "sha256": digest(launcher)},
              "desktop": {"path": str(desktop), "sha256": digest(desktop)},
              "engine_catalog": {"path": str(engine_catalog), "sha256": digest(engine_catalog)},
              "session_helper": {"path": str(session_helper), "sha256": digest(session_helper)},
              "stockfish": stockfish_selection, "calibration": calibration,
              "session_work_root": str(work_root),
              "launch": selection,
              "t0_perfcache": str(perfcache),
              "engine_execution_verified": False, "desktop_substrate_access_verified": False,
              "system_desktop_file": str(system) if system.is_symlink() and
                                     os.readlink(system) == str(desktop) else None,
              "operator_desktop_tested": False, "autostart_installed": False}
    _publish_desktop_text(manifest, json.dumps(result, indent=2) + "\n", 0o644)
    return result


def configure_evaluation(prefix, stockfish):
    """Resolve a retained calibration once during ordinary cold deployment."""
    prefix = Path(prefix).resolve(strict=True)
    env_path = prefix / "app/laplace-api.env"
    original = env_path.read_text(encoding="utf-8")
    remaining = without_evaluation_defaults(original)
    manifest = json.loads((prefix / "share/laplace/cutechess-desktop.json").read_text())
    calibration = manifest.get("calibration", {})
    receipt = {"schema": "laplace.stockfish-evaluation-defaults/v1",
               "status": "not-selected", "configurationApplied": False,
               "scope": "Installed cold-launch defaults; caller environment/options take precedence"}
    if calibration.get("status") == "selected":
        try:
            options, identity, raw = desktop_calibration(
                calibration["report"], calibration["reportSha256"], Path(stockfish).resolve(strict=True))
        except (OSError, ValueError, KeyError, TypeError):
            receipt.update(status="stale", reportSha256=calibration.get("reportSha256"))
        else:
            spec = importlib.util.spec_from_file_location(
                "evaluation_runtime_configuration", Path(__file__).with_name("chess-runtime-env.py"))
            doctor = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(doctor)
            keys = {"LAPLACE_STOCKFISH_EVAL_" + name for name in ("THREADS", "HASH_MB", "PROCESSES")}
            explicit = doctor.configuration(prefix, keys, api_environment=remaining)
            spec = importlib.util.spec_from_file_location(
                "evaluation_machine_configuration", Path(__file__).with_name("benchmark-chess-environment.py"))
            benchmark = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(benchmark)
            report = json.loads(raw)
            host = benchmark.machine()
            if calibration_machine(host) != identity["machine"]:
                raise ValueError("machine limits changed while selecting evaluation defaults")
            effective, resource = evaluation_configuration(options, explicit, report, host)
            # The block only fills absent installed values. Explicit environment values
            # are persisted here only when no corresponding user-owned file value exists.
            installed = doctor.configuration(prefix, keys, api_environment=remaining, include_environment=False)
            entries = {key: value for key, value in effective.items() if key not in installed}
            installed_values = {**installed, **{key: str(value) for key, value in entries.items()}}
            _, installed_resource = evaluation_configuration(options, installed_values, report, host)
            block = (EVALUATION_BEGIN + "\n"
                     + "# report-sha256=" + identity["reportSha256"] + "\n"
                     + "".join(key + "=" + str(value) + "\n" for key, value in entries.items())
                     + EVALUATION_END + "\n")
            remaining = remaining.rstrip("\n") + "\n" + block
            receipt.update(status="selected", configurationApplied=True,
                           reportSha256=identity["reportSha256"], stockfish=identity["stockfish"],
                           machine=identity["machine"],
                           installedConfiguration=installed_resource["configuration"],
                           environmentOverridesPersistedOnlyForMissingFileKeys=True, **resource)
    if remaining != original:
        _publish_desktop_text(env_path, remaining, stat.S_IMODE(env_path.stat().st_mode))
    receipt["environmentFile"] = str(env_path)
    receipt["restartRequiredForRunningServices"] = True
    receipt_path = prefix / "share/laplace/stockfish-evaluation-defaults.json"
    _publish_desktop_text(receipt_path, json.dumps(receipt, indent=2) + "\n", 0o644)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=LOCK)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--verify-source", type=Path, help="Read-only exact source verification; never updates the checkout")
    parser.add_argument("--reset-build-cache", type=Path, help="Remove only generated CMakeCache.txt/CMakeFiles for the verified source before configure")
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--receipt", type=Path, help="Retain verified source and binary identity after the build")
    parser.add_argument("--qt-version")
    parser.add_argument("--gui", action="store_true", help="Verify the official GUI through offscreen QApplication initialization")
    parser.add_argument("--qt-prefix", type=Path, help="Selected Qt SDK for GUI build verification")
    parser.add_argument("--verify-receipt", type=Path, help="Read-only GUI installed-binary verification against its retained source build")
    parser.add_argument("--work", type=Path, help="Build-volume directory for isolated GUI probe settings")
    parser.add_argument("--check-latest", action="store_true")
    parser.add_argument("--install-desktop", type=Path, help="Install a public desktop launcher under this prefix after GUI installed verification")
    parser.add_argument("--desktop-stockfish", type=Path, help="Selected direct official Stockfish executable for the public GUI catalog")
    parser.add_argument("--register-desktop", type=Path, help="Register an existing prefix desktop installation in the system application menu; no GUI execution")
    parser.add_argument("--configure-evaluation", type=Path, help="Apply retained measured analysis defaults to this prefix at cold deployment")
    parser.add_argument("--calibration-report", type=Path, help="Completed exact-machine Stockfish calibration to apply as missing GUI option defaults")
    parser.add_argument("--calibration-sha256", help="SHA256 of the explicitly selected calibration report")
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text())
    if not re.fullmatch(r"[0-9a-f]{40}", lock["commit"]):
        parser.error("release lock must contain a complete source commit")
    if args.configure_evaluation:
        if args.desktop_stockfish is None:
            parser.error("--configure-evaluation requires the selected direct --desktop-stockfish")
        print(json.dumps(configure_evaluation(args.configure_evaluation, args.desktop_stockfish)))
        return 0
    if bool(args.calibration_report) != bool(args.calibration_sha256) or (args.calibration_report and not args.install_desktop):
        parser.error("calibration report and SHA256 must be selected together with --install-desktop")
    if args.register_desktop:
        if any((args.source_dir, args.verify_source, args.binary, args.check_latest, args.gui,
                args.qt_prefix, args.receipt, args.verify_receipt, args.install_desktop, args.desktop_stockfish, args.reset_build_cache, args.work)):
            parser.error("--register-desktop is a separate metadata-only operation")
        try:
            print(json.dumps(register_desktop(args.register_desktop)))
            return 0
        except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
            print(f"CuteChess: {error}", file=sys.stderr)
            return 1
    if args.install_desktop and not (args.gui and args.verify_receipt and args.desktop_stockfish):
        parser.error("--install-desktop requires --gui installed verification and --desktop-stockfish")
    if args.desktop_stockfish and not args.install_desktop:
        parser.error("--desktop-stockfish requires --install-desktop")
    if not (args.source_dir or args.verify_source or args.binary or args.check_latest):
        parser.error("choose --source-dir, --verify-source, --binary or --check-latest")
    if args.reset_build_cache and (not args.verify_source or args.binary or args.receipt or args.source_dir or args.check_latest):
        parser.error("--reset-build-cache requires --verify-source without another action")
    if args.receipt and not (args.verify_source and args.binary):
        parser.error("--receipt requires --verify-source and --binary")
    if args.gui and (not args.binary or (not args.verify_receipt and not args.qt_prefix)):
        parser.error("--gui requires --binary and either --qt-prefix or --verify-receipt")
    if args.verify_receipt and (not args.gui or args.receipt or args.source_dir or args.verify_source):
        parser.error("--verify-receipt requires --gui and --binary without a source/build mutation")
    if (args.qt_prefix or args.verify_receipt or args.work) and not args.gui:
        parser.error("GUI options require --gui")
    try:
        if args.check_latest:
            print(json.dumps(check_latest(lock)))
        if args.source_dir:
            print(provision(args.source_dir.resolve(), lock))
        if args.reset_build_cache:
            print(json.dumps(reset_build_cache(args.verify_source, args.reset_build_cache, lock)))
        elif args.gui and args.verify_receipt:
            verified = verify_gui_install(args.binary, args.verify_receipt, lock, args.work)
            if args.install_desktop:
                verified["desktop_install"] = install_desktop(args.install_desktop, verified, args.desktop_stockfish, args.work,
                                                              args.calibration_report, args.calibration_sha256)
            print(json.dumps(verified))
        elif args.gui and args.verify_source:
            print(json.dumps(verify_gui_build(args.verify_source, args.binary, lock, args.qt_prefix, args.receipt, args.work)))
        elif args.gui:
            print(json.dumps(probe_gui(args.binary, lock, args.qt_prefix, args.work)))
        elif args.verify_source and args.binary:
            print(json.dumps(verify_build(args.verify_source, args.binary, lock, args.qt_version, args.receipt)))
        elif args.verify_source:
            print(json.dumps(verify_source(args.verify_source, lock)))
        elif args.binary:
            print(json.dumps(probe(args.binary, lock, args.qt_version)))
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"CuteChess: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

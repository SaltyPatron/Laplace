#!/usr/bin/env python3
"""Operational block for the existing locked GUI operator workflow, not a product entrypoint.

Invoke as the ordinary nonroot mapped account, with the host lock owned by the
calling workflow for this whole process. No setup-laplace-env/chess-runtime-env
wrapper: the GUI uses its public installed launch environment and peer auth.
Selection: candidate_commit, candidate_tree, installed_source, proof_run_id,
proof_run_attempt, prefix, pg_prefix, gui_build_receipt, cli_build_receipt,
cli_binary, expected_user, driver_root, qualification_helper_sha256, optional
managed_build_root (otherwise normal Linux project output). The selected candidate
root remains the installed source; driver_root supplies the explicitly reviewed
qualification helper. All paths are explicit; no newest-build search is used.
"""
import datetime
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import signal
import subprocess
import sys
import time

sys.dont_write_bytecode = True
if len(sys.argv) != 4:
    raise SystemExit("supply selection, exact checkout and fresh permanent output")
selection_path, root_arg, output_arg = map(Path, sys.argv[1:4])
root = root_arg.resolve(strict=True)
output = output_arg
if (not output.is_absolute() or output.exists()
        or output.is_symlink() or not output.parent.resolve(strict=True).is_relative_to(Path("/build/laplace"))):
    raise SystemExit("supply selection, exact checkout and fresh permanent output")
if os.getuid() != os.geteuid() or os.getuid() == 0:
    raise SystemExit("run the existing operator as a nonroot account")
os.umask(0o077)
output.mkdir(mode=0o700)
scratch = output / "work"
scratch.mkdir(mode=0o700)

def module(name, filename, base=root):
    spec = importlib.util.spec_from_file_location(name, base / "scripts" / filename)
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value

owner = module("gui_operator_acceptance", "accept-chess-environment.py")
export = module("gui_operator_qualification", "export-qualified-chess-floors.py")
engine = module("gui_operator_engines", "check-cutechess-user-engines.py")
game = module("gui_operator_game", "check-cutechess-gui-game.py")
guard = module("gui_operator_runtime", "check-application-runtime.py")
plan = export.load(selection_path, 65536)
driver_root = Path(plan["driver_root"])
qualification_helper = driver_root / "scripts/export-qualified-chess-floors.py"
qualification_blob = "aaeb16afc2dd9b82ce581ef91adc665dfc3ad21a"
qualification_sha256 = plan["qualification_helper_sha256"]
engine.require(driver_root.is_absolute() and driver_root.resolve(strict=True) == driver_root
               and not str(driver_root).startswith(("/tmp/", "/var/tmp/", "/dev/shm/"))
               and qualification_helper.is_file() and not qualification_helper.is_symlink()
               and qualification_helper.stat().st_size <= 1 << 20
               and isinstance(qualification_sha256, str)
               and re.fullmatch(r"[0-9a-f]{64}", qualification_sha256) is not None
               and engine.digest(qualification_helper) == qualification_sha256
               and export.git(driver_root, "hash-object", "--no-filters", str(qualification_helper)) == qualification_blob,
               "qualification helper differs from the reviewed driver selection")
export = module("gui_operator_qualification", "export-qualified-chess-floors.py", driver_root)
engine.require(engine.digest(qualification_helper) == qualification_sha256,
               "qualification helper changed while loading")
engine.require(export.git(root, "rev-parse", "HEAD") == plan["candidate_commit"]
               and export.git(root, "rev-parse", "HEAD^{tree}") == plan["candidate_tree"]
               and not export.git(root, "status", "--porcelain", "--untracked-files=all"),
               "operator checkout is not the exact clean selected source")
engine.require(plan["expected_user"] == pwd.getpwuid(os.getuid()).pw_name,
               "selected invoking account changed")
for key in ("prefix", "pg_prefix", "gui_build_receipt", "cli_build_receipt", "cli_binary"):
    engine.require(Path(plan[key]).is_absolute(), "selected paths must be absolute")
prefix, pg = Path(plan["prefix"]), Path(plan["pg_prefix"])
started = time.monotonic()
utc = lambda: datetime.datetime.now(datetime.timezone.utc).isoformat()
receipt = {"schema": "laplace.gui-operator-observation/v1", "status": "incomplete",
           "startedUtc": utc(), "phases": [], "source": export.selection_identity(plan),
           "uid": os.getuid(), "user": plan["expected_user"], "groups": os.getgroups(),
           "machineColdBootMeasured": False, "serviceRestartMeasured": False,
           "operatorDesktopGameProven": False, "databaseRecordingProven": False,
           "qualificationDriver": {"driverRoot": str(driver_root), "candidateRoot": str(root),
               "path": str(qualification_helper), "gitBlob": qualification_blob,
               "sha256": qualification_sha256}}

def save():
    owner.save(output / "receipt.json", receipt)

def phase(name, operation, required=True):
    item = {"name": name, "required": required, "status": "running", "startedUtc": utc()}
    receipt["phases"].append(item)
    save()
    begin = time.monotonic()
    try:
        value = operation()
        item.update(status="passed", result=value)
        return True
    except Exception as error:
        # Do not publish arbitrary exception messages from runtime configuration.
        item.update(status="failed", failureType=type(error).__name__)
        return False
    finally:
        item.update(finishedUtc=utc(), seconds=time.monotonic() - begin)
        save()

def run(name, argv, seconds, environment=None):
    owner.command(argv, output / (name + ".log"), seconds, env=environment or env)

def interrupt(_signum, _frame):
    raise KeyboardInterrupt("operator interrupted")

signal.signal(signal.SIGTERM, interrupt)
save()
try:
    with owner.deadline(2400):
        # The shared owner authenticates the real event and resolves the full plan
        # from this candidate. Retain lexical-only failure truthfully while each
        # applicable installed-service/engine/game check proves its own result.
        build, receipt["qualification"] = export.qualification(plan, root)
        engine.require(engine.digest(qualification_helper) == qualification_sha256,
                       "qualification helper changed during proof validation")
        qualified_root = Path(receipt["qualification"]["lifecycle_checkout"])
        receipt["bootIdBefore"] = owner.boot_id()
        # Keep only public session/tool selection. No inherited service credential,
        # build-tree native path, perfcache override or GitHub token enters engines.
        public = ("PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE",
                  "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR",
                  "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS",
                  "DBUS_SESSION_BUS_ADDRESS", "DOTNET_ROOT", "DOTNET_CLI_HOME")
        env = {key: value for key, value in os.environ.items() if key in public}
        env.update(PYTHONDONTWRITEBYTECODE="1", TMPDIR=str(scratch), TMP=str(scratch),
                   TEMP=str(scratch), PGHOST="/var/run/postgresql", PGPORT="5432",
                   PGUSER="laplace_admin", PGDATABASE="laplace",
                   PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=10000",
                   LAPLACE_INSTALL_PREFIX=str(prefix), LAPLACE_PG_PREFIX=str(pg),
                   LAPLACE_API_BASE="http://127.0.0.1:5187",
                   LAPLACE_LICHESS_STATUS_BASE="http://127.0.0.1:5189")
        os.environ.clear()
        os.environ.update(env)
        receipt["publicDatabaseRoute"] = engine.default_database_route(env)
        save()

        def installed():
            return guard.snapshot(qualified_root, prefix, guard.read_database(pg),
                                  receipt["qualification"]["native_fingerprint"])
        before = installed()
        owner.save(output / "native-before.json", before)

        psql = [pg / "bin/psql", "-X", "-w", "-h", "/var/run/postgresql", "-p", "5432",
                "-U", "laplace_admin", "-d", "laplace", "-v", "ON_ERROR_STOP=1", "-At"]
        peer = phase("invoking-account-peer-access", lambda: run("peer-access", psql + ["-c",
            "SELECT json_build_object('role',current_user,'database',current_database(),"
            "'server_version_num',current_setting('server_version_num'),"
            "'socket',current_setting('unix_socket_directories'))"], 20))
        phase("configured-peer-mappings", lambda: run("peer-mappings", psql + ["-c",
            "SELECT json_agg(json_build_object('map',map_name,'system_user',sys_name,"
            "'database_role',pg_username,'error_present',error IS NOT NULL)) "
            "FROM pg_ident_file_mappings WHERE map_name='laplace_map' AND pg_username='laplace_admin'"],
            20), required=False)

        resident = output / "resident-services"
        resident.mkdir()
        phase("resident-services", lambda: owner.services(resident))
        phase("application-readiness", lambda: run("application-readiness",
            [sys.executable, root / "scripts/verify-application-release.py",
             "--base", "http://127.0.0.1:5187", "--state-file", resident / "application-readiness.json",
             "--timeout-seconds", "60"], 90))

        def frontdoors(base):
            rows = {}
            for route in ("/play", "/chess", "/lab"):
                body, response = owner.response(base, route, 4 << 20)
                engine.require(response["contentType"] == "text/html" and b'id="root"' in body,
                               "browser frontdoor did not return the installed SPA")
                rows[route] = {**response, "url": base + route, "bytes": len(body),
                               "sha256": __import__("hashlib").sha256(body).hexdigest(),
                               "scope": "local served SPA; interactive browser behavior not measured here"}
            return rows
        phase("api-browser-frontdoors", lambda: frontdoors("http://127.0.0.1:5187"))
        phase("published-browser-frontdoors", lambda: frontdoors("http://127.0.0.1:8080"))
        def mcp_readiness():
            body, observed = owner.response("http://127.0.0.1:5188", "/health/ready", 65536)
            value = json.loads(body)
            engine.require(value.get("service") == "laplace-mcp" and value.get("ready") is True,
                           "installed MCP is not ready")
            return {**observed, "service": value["service"], "ready": value["ready"],
                    "authenticatedClientInitializationMeasured": False}
        phase("mcp-readiness", mcp_readiness)
        def sessions():
            run("login-sessions", ["loginctl", "list-sessions", "--no-legend", "--no-pager"], 15)
            with (output / "login-sessions.log").open("rb") as stream:
                raw = stream.read(65537)
            engine.require(len(raw) <= 65536, "login session inventory exceeded its envelope")
            lines = raw.decode("utf-8").splitlines()
            ids = [line.split()[0] for line in lines if line.split()]
            engine.require(len(ids) <= 32 and all(re.fullmatch(r"[A-Za-z0-9_-]+", item) for item in ids),
                           "login session inventory is outside its bounded identifiers")
            for number, session in enumerate(ids):
                run("login-session-" + str(number), ["loginctl", "show-session", session,
                    "--no-pager", "--property=Name,User,Type,Class,Active,State,Remote,Display,Service"],
                    5)
            return {"sessionIds": ids, "operatorDesktopGameProven": False}
        phase("login-sessions", sessions, required=False)
        def resident_gui():
            run("resident-gui-processes", ["ps", "-C", "cutechess", "-o", "uid=,pid=,lstart=,comm="], 10)
            return {"scope": "preexisting process inventory only; window/session ownership not inferred"}
        phase("preexisting-cutechess-processes", resident_gui, required=False)
        phase("hostname", lambda: run("hostname", ["hostname", "-f"], 10), required=False)
        phase("interface-addresses", lambda: run("interface-addresses",
            ["ip", "-j", "address", "show", "up"], 10), required=False)
        phase("listeners", lambda: run("listeners",
            ["ss", "-ltn", "sport = :5187 or sport = :5188 or sport = :5189 or sport = :8080 or sport = :8443"],
            15), required=False)

        def process_access():
            states = owner.unit_states()
            pid = states["laplace-api"]["pid"]
            result = {"observedUtc": utc(), "units": states, "invokingUid": os.getuid()}
            engine.require(pid > 0, "API has no running PID")
            process = Path("/proc") / str(pid)
            result["apiProcessUid"] = process.stat().st_uid
            for key in ("exe", "maps"):
                try:
                    if key == "exe":
                        result[key] = {"readable": True, "target": os.readlink(process / key),
                                       "sha256": owner.sha256(process / key)}
                    else:
                        with (process / key).open("rb") as stream:
                            data = stream.read((4 << 20) + 1)
                        engine.require(len(data) <= 4 << 20, "API maps exceeded bounded observation")
                        result[key] = {"readable": True, "bytes": len(data),
                                       "sha256": __import__("hashlib").sha256(data).hexdigest()}
                except OSError as error:
                    result[key] = {"readable": False, "errno": error.errno}
            return result
        phase("service-process-observation-access", process_access, required=False)

        def menu():
            desktop, _ = engine.authenticate_desktop(
                prefix, prefix / "share/laplace/cutechess-desktop.json",
                Path(plan["gui_build_receipt"]))
            link = Path("/usr/local/share/applications/laplace-cutechess.desktop")
            target = prefix / "share/applications/laplace-cutechess.desktop"
            if not link.exists() and not link.is_symlink():
                run("menu-registration", ["sudo", "-n", "/usr/bin/python3",
                    root / "scripts/provision-cutechess.py", "--register-desktop", prefix], 30)
            engine.require(link.is_symlink() and os.readlink(link) == str(target)
                           and link.resolve(strict=True) == target.resolve(strict=True),
                           "system menu does not select the public installed desktop entry")
            return {"path": str(link), "target": str(target), "sha256": engine.digest(target),
                    "publicLauncher": desktop["launcher"]["path"], "autostartClaim": False}
        phase("desktop-menu", menu)

        def catalog():
            managed = plan.get("managed_build_root", "")
            if managed:
                engine.require(Path(managed).is_absolute()
                               and Path(managed).resolve(strict=True).is_relative_to(Path("/build/laplace/build")),
                               "managed build selection is outside permanent build ownership")
            buildenv = dict(env, LAPLACE_BUILD_ROOT=managed,
                            LAPLACE_ENGINE_BUILD=str(build / "engine"),
                            MSBUILDDISABLENODEREUSE="1", UseSharedCompilation="false")
            run("catalog-output-evaluation", ["dotnet", "msbuild",
                qualified_root / "app/ChessCatalogSurfaces/ChessCatalogSurfaces.csproj",
                "-nologo", "-p:Configuration=Release", "-getProperty:TargetPath"], 60, buildenv)
            with (output / "catalog-output-evaluation.log").open("rb") as stream:
                raw = stream.read(65537)
            engine.require(len(raw) <= 65536, "MSBuild property output exceeded its envelope")
            path = Path(raw.decode("utf-8").strip())
            engine.require(path.is_absolute() and path.name == "ChessCatalogSurfaces.dll",
                           "MSBuild did not resolve the selected catalog output")
            apphost = path.with_suffix("").resolve(strict=True)
            wrapper = (prefix / "app/laplace-uci").resolve(strict=True)
            closure = game.catalog_closure(apphost, Path(str(wrapper) + ".native"))
            receipt["catalog"] = {"path": str(apphost), "closure": closure,
                                  "managedBuildRootSelection": managed,
                                  "sourceProject": str(qualified_root / "app/ChessCatalogSurfaces/ChessCatalogSurfaces.csproj")}
            return receipt["catalog"]
        catalog_ready = phase("catalog-installed-closure", catalog)
        packages = phase("gui-accessibility-tools", lambda: run("gui-accessibility-tools",
            ["bash", root / "scripts/bootstrap-laplace-runner.sh", "chess-gui-acceptance-tools"], 320))
        x11_ready = phase("x11-tool-selection", lambda: run("x11-tool-selection",
            [sys.executable, root / "scripts/chess-x11-runtime.py",
             "--root", prefix / "tools/chess/x11-runtime", "--deadline-seconds", "280",
             "--output", output / "x11-runtime.json",
             "--evidence-output", output / "x11-acquisition"], 310))
        direct = False
        if peer:
            direct = phase("configured-direct-engines", lambda: run("configured-direct-engines",
                [sys.executable, root / "scripts/check-cutechess-user-engines.py", "--prefix", prefix,
                 "--expected-user", plan["expected_user"], "--gui-build-receipt", plan["gui_build_receipt"],
                 "--cli-binary", plan["cli_binary"], "--cli-build-receipt", plan["cli_build_receipt"],
                 "--output-dir", output / "direct", "--timeout-seconds", "300"], 330))
        if direct and catalog_ready and packages and x11_ready:
            phase("complete-gui-game", lambda: run("complete-gui-game",
                [sys.executable, root / "scripts/check-cutechess-gui-game.py", "--prefix", prefix,
                 "--expected-user", plan["expected_user"],
                 "--engine-acceptance-receipt", output / "direct/receipt.json",
                 "--catalog", receipt["catalog"]["path"],
                 "--x11-runtime-receipt", output / "x11-runtime.json",
                 "--output-dir", output / "game", "--timeout-seconds", "900"], 930))
        else:
            receipt["phases"].append({"name": "complete-gui-game", "required": True,
                                      "status": "blocked", "reason": "actual prerequisites did not pass"})
        after = installed()
        owner.save(output / "native-after.json", after)
        engine.require(before == after, "installed native/database selection changed during GUI observation")
        receipt["bootIdAfter"] = owner.boot_id()
        engine.require(receipt["bootIdBefore"] == receipt["bootIdAfter"], "boot identity changed")
        receipt["status"] = "passed" if all(p["status"] == "passed" for p in receipt["phases"] if p["required"]) else "failed"
except BaseException as error:
    receipt.update(status="failed", failureType=type(error).__name__)
finally:
    receipt.update(finishedUtc=utc(), elapsedSeconds=time.monotonic() - started)
    save()
print(json.dumps({"status": receipt["status"], "receipt": str(output / "receipt.json")}))
raise SystemExit(0 if receipt["status"] == "passed" else 1)

#!/usr/bin/env python3
"""Authenticate and refresh a consumer's declared runtime credentials through OpenBao."""
import argparse
from contextlib import contextmanager
import fcntl
import json
import os
from pathlib import Path
import pwd
import re
import shlex
import struct
import subprocess
import tempfile
import sys
from datetime import datetime, timezone
import urllib.request


def atomic(path, text, mode, uid, gid):
    if path.is_symlink():
        raise ValueError("Refusing to replace a symbolic-link configuration file")
    fd, temporary = tempfile.mkstemp(prefix=".substrate-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as output:
            output.write(text)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.chown(temporary, uid, gid)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def assignments(path, values):
    stat = path.stat()
    lines = [line for line in path.read_text().splitlines() if line.partition("=")[0] not in values]
    lines.extend(key + "=" + shlex.quote(str(value)) for key, value in values.items())
    atomic(path, "\n".join(lines) + "\n", stat.st_mode & 0o777, stat.st_uid, stat.st_gid)


@contextmanager
def configuration_transaction(paths, recover):
    snapshots = []
    for path in paths:
        if path.is_symlink():
            raise ValueError("Refusing to replace a symbolic-link configuration file")
        snapshots.append((path, path.read_text(), path.stat()) if path.exists() else (path, None, None))
    try:
        yield
    except Exception:
        for path, content, stat in snapshots:
            if stat is None:
                path.unlink(missing_ok=True)
            else:
                atomic(path, content, stat.st_mode & 0o777, stat.st_uid, stat.st_gid)
        recover()
        raise


def activate(user, prefix, base):
    started = datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S UTC')
    subprocess.run(["systemctl", "restart", "laplace-api"], check=True, timeout=30)
    verifier = prefix / "app/operations/scripts/verify-application-release.py"
    env = dict(os.environ)
    logs = prefix / 'app/logs'
    for line in (prefix / 'app/laplace-api.env').read_text().splitlines():
        key, sep, value = line.partition('=')
        if sep and key == 'LAPLACE_OPS_LOG_DIR':
            logs = Path(shlex.split(value)[0] if value.startswith(('"', "'")) else value.strip())
    # systemd supplies this to the API from its root-read EnvironmentFile. The
    # verifier runs as the API identity without access to deployment credentials.
    for line in (prefix / 'secrets/operator.env').read_text().splitlines():
        key, sep, value = line.partition('=')
        if sep and key == 'LAPLACE_OPERATOR_TOKEN':
            parsed = shlex.split(value)
            env[key] = parsed[0] if parsed else ''
    result = subprocess.run(["runuser", "-u", user, "--", "python3", str(verifier),
        "--base", base, "--timeout-seconds", "10", "--state-file",
        str(logs / "substrate-selection-verification.json")],
        capture_output=True, text=True, timeout=60, env=env)
    if result.returncode:
        if os.environ.get('LAPLACE_INSTALL_CAPTURE_DIAGNOSTICS') == '1':
            # The installer captures stderr into a root-private diagnostic file.
            # Never include an environment dump or the credential JSON input.
            print(result.stdout + result.stderr, file=sys.stderr)
            journal = subprocess.run(['journalctl', '-u', 'laplace-api', '--since', started,
                '--no-pager', '-o', 'cat', '-n', '100'], text=True, capture_output=True, timeout=10)
            print(journal.stdout, file=sys.stderr)
        raise ValueError("Selected substrate failed installed application verification; configuration will be restored")


def validate_substrate_receipt(prefix, candidate_receipt):
    """Substrate contents may differ; the loaded Tier-0 coordinate contract may not."""
    selected = None
    for line in (prefix / "app/laplace-api.env").read_text().splitlines():
        key, sep, value = line.partition("=")
        if sep and key == "LAPLACE_PERFCACHE_BIN":
            value = value.strip()
            selected = shlex.split(value)[0] if value.startswith(('"', "'")) else value
    if not selected or not Path(selected).is_absolute():
        raise ValueError("The installed API must declare its Tier-0 artifact before substrate selection")
    with Path(selected).open("rb") as stream:
        if stream.read(8) != struct.pack("<II", 0x4652504C, 4):
            raise ValueError("Unsupported installed Tier-0 format")
        stream.seek(-16, os.SEEK_END)
        installed_receipt = stream.read(16).hex()
    if installed_receipt != candidate_receipt or installed_receipt == "0" * 32:
        raise ValueError("Selected substrate is incompatible with the installed Tier-0 artifact")
    return installed_receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--credentials", type=Path, required=True)
    parser.add_argument("--user", required=True)
    parser.add_argument("--prefix", type=Path, required=True)
    parser.add_argument("--machine-file", type=Path, required=True)
    parser.add_argument("--lock", type=Path, default=Path("/run/lock/laplace/host-resource.lock"))
    parser.add_argument("--bao-address", required=True)
    parser.add_argument("--root-certificate", type=Path, help="Verify both declared database endpoints against this installation's CA")
    parser.add_argument("--select", action="store_true", help="Select the credential's substrate target and restart the API")
    parser.add_argument("--application-record", help="Additional host KV record for the API's restricted application login")
    parser.add_argument("--restart-on-change", action="store_true", help="Reload pools after verified credentials change")
    parser.add_argument("--verification-base", help="Required with --select: the installed API URL to verify after activation")
    args = parser.parse_args()
    if (args.select or args.restart_on_change) and (not args.verification_base or not args.verification_base.startswith(("https://", "http://127.0.0.1:", "http://localhost:"))):
        parser.error("Selection/restart requires --verification-base (HTTPS or local HTTP)")
    if args.application_record and not re.fullmatch(r'[a-z][a-z0-9_-]*', args.application_record):
        parser.error('Invalid application record name')
    if not re.fullmatch(r"[a-zA-Z0-9._-]+", args.host) or not args.bao_address.startswith("https://"):
        raise ValueError("Expected a declared host and HTTPS Bao address")
    if args.credentials.stat().st_mode & 0o077:
        raise ValueError("AppRole credentials must be private to their owner")
    inherited = os.environ.get('LAPLACE_HOST_LOCK_FD')
    lock = os.fdopen(os.dup(int(inherited)), 'a') if inherited else args.lock.open('a')
    if not os.path.samestat(os.fstat(lock.fileno()), args.lock.stat()):
        raise ValueError('Inherited operation lock belongs to a different installation')
    fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    token = None

    def request(path, payload=None):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["X-Vault-Token"] = token
        data = json.dumps(payload).encode() if payload is not None else None
        with urllib.request.urlopen(urllib.request.Request(args.bao_address.rstrip("/") + "/v1/" + path,
                data=data, headers=headers), timeout=20) as response:
            body = response.read(1024 * 1024)
            return json.loads(body) if body else None

    try:
        token = request("auth/approle/login", json.loads(args.credentials.read_text()))["auth"]["client_token"]
        record = request("kv/data/hosts/" + args.host + "/substrate")["data"]["data"]
        host, database, role = (record[name] for name in ("database_host", "database", "role"))
        if not all(isinstance(v, str) and re.fullmatch(r"[a-zA-Z0-9._-]+", v) for v in (host, database, role)):
            raise ValueError("Invalid substrate selection")
        port = int(record["port"])
        if not 1 <= port <= 65535 or not re.fullmatch(r"[0-9a-f]{64}", record["password"]):
            raise ValueError("Invalid substrate credential")
        account = pwd.getpwnam(args.user)
        pgpass = Path(account.pw_dir) / ".pgpass"
        env = dict(os.environ, PGPASSWORD=record["password"], PGSSLMODE="require", PGCONNECT_TIMEOUT="10")
        if args.root_certificate:
            if not args.root_certificate.is_absolute() or not args.root_certificate.is_file():
                raise ValueError('Database verification requires an installed absolute trust file')
            env.update(PGSSLMODE='verify-full', PGSSLROOTCERT=str(args.root_certificate))
        check = subprocess.run(["psql", "-X", "-w", "-h", host, "-p", str(port), "-U", role, "-d", database,
            "-Atqc", "SELECT current_user, encode(laplace.perfcache_receipt(), 'hex')"], env=env,
            text=True, capture_output=True, timeout=20)
        if check.returncode or not check.stdout.startswith(role + "|"):
            raise ValueError("Substrate database authentication/native receipt check failed: " + check.stderr.strip())
        if args.select and (not args.machine_file.is_file() or not (args.prefix / "app/laplace-api.env").is_file()):
            raise ValueError("Existing machine declaration and application environment are required")
        if args.select:
            validate_substrate_receipt(args.prefix, check.stdout.strip().split("|", 1)[1])
        prefix = f"{host}:{port}:{database}:{role}:"
        previous = pgpass.read_text().splitlines() if pgpass.exists() else []
        lines = [line for line in previous if not line.startswith(prefix)]
        lines.append(prefix + record["password"])
        if args.application_record:
            application = request('kv/data/hosts/' + args.host + '/' + args.application_record)['data']['data']
            ahost, adb, arole = (application[name] for name in ('database_host', 'database', 'role'))
            aport = int(application['port'])
            if not all(isinstance(v, str) and re.fullmatch(r'[a-zA-Z0-9._-]+', v) for v in (ahost, adb, arole)) or not 1 <= aport <= 65535 or not re.fullmatch(r'[0-9a-f]{64}', application['password']):
                raise ValueError('Invalid application runtime credential')
            appcheck = subprocess.run(['psql', '-X', '-w', '-h', ahost, '-p', str(aport), '-U', arole, '-d', adb,
                '-Atqc', 'SELECT current_user'], env=dict(env, PGPASSWORD=application['password']), capture_output=True, text=True, timeout=20)
            if appcheck.returncode or appcheck.stdout.strip() != arole:
                raise ValueError('Application runtime authentication failed; credentials were not published')
            appprefix = f'{ahost}:{aport}:{adb}:{arole}:'
            lines = [line for line in lines if not line.startswith(appprefix)]
            lines.append(appprefix + application['password'])
        changed = previous != lines
        paths = [pgpass]
        if args.select:
            paths += [args.machine_file, args.prefix / "app/laplace-api.env"]
        def recover():
            if args.select or (args.restart_on_change and changed):
                activate(args.user, args.prefix, args.verification_base)
        with configuration_transaction(paths, recover):
            atomic(pgpass, "\n".join(lines) + "\n", 0o600, account.pw_uid, account.pw_gid)
            if args.select:
                assignments(args.machine_file, {"LAPLACE_PGHOST": host, "LAPLACE_PGPORT": port,
                    "LAPLACE_DBNAME": database, "LAPLACE_DATABASE_ROLE": role, "LAPLACE_DB_SSLMODE": "Require",
                    "LAPLACE_CONNINFO": f"host={host} port={port} user={role} dbname={database} sslmode=require"})
                assignments(args.prefix / "app/laplace-api.env", {"LAPLACE_DB":
                    f"Host={host};Port={port};Username={role};Database={database};SSL Mode=Require"})
                activate(args.user, args.prefix, args.verification_base)
            elif args.restart_on_change and changed:
                activate(args.user, args.prefix, args.verification_base)
        print(json.dumps({"host": args.host, "substrate_host": host, "database": database, "role": role,
            "authenticated": True, "native_receipt": check.stdout.strip().split("|", 1)[1],
            "selection_updated": args.select, "credentials_changed": changed,
            "application_credential": bool(args.application_record)}))
    finally:
        try:
            if token:
                request("auth/token/revoke-self", {})
        finally:
            lock.close()


if __name__ == "__main__":
    main()

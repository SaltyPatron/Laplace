#!/usr/bin/env python3
"""Read-only PostgreSQL access snapshot for the installed legacy Linux host.

Uses ONLY local peer authentication. Never reads credentials, SQL statement text,
HBA option values, process environments or password hashes. No sudo, reload,
network scan or authentication probe. Missing privileges produce explicit unknowns.
"""
import datetime
import json
import os
import socket
import subprocess
import sys

PSQL = "/opt/laplace/pgsql-18/bin/psql"
ENV = {
    "PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LANG": "C", "LC_ALL": "C",
    "PGAPPNAME": "laplace-access-audit", "PGCONNECT_TIMEOUT": "3",
    "PGPASSFILE": "/dev/null", "PGSERVICEFILE": "/dev/null",
    "PGOPTIONS": "-c default_transaction_read_only=on -c statement_timeout=5000 -c lock_timeout=1000",
}

# Every value is an authored SELECT over system metadata. Do not add statement
# text, application_name values (caller-controlled), auth options or pg_authid.
QUERIES = {
    "settings": """SELECT name, setting, source, sourcefile, sourceline, pending_restart
        FROM pg_settings WHERE name IN ('listen_addresses', 'port', 'ssl',
        'password_encryption', 'log_connections', 'log_disconnections',
        'config_file', 'hba_file', 'ident_file', 'unix_socket_directories') ORDER BY name""",
    "configuration_times": """SELECT pg_conf_load_time() AS loaded_at,
        (pg_stat_file(current_setting('hba_file'))).modification AS hba_modified_at,
        (pg_stat_file(current_setting('ident_file'))).modification AS ident_modified_at""",
    "hba_file_rules": """SELECT rule_number, file_name, line_number, type, database, user_name,
        address, netmask, auth_method,
        ARRAY(SELECT split_part(option, '=', 1) FROM unnest(options) AS option) AS option_names,
        error IS NOT NULL AS parse_error FROM pg_hba_file_rules ORDER BY rule_number, line_number""",
    "ident_file_mappings": """SELECT map_number, file_name, line_number, map_name, sys_name,
        pg_username, error IS NOT NULL AS parse_error FROM pg_ident_file_mappings ORDER BY map_number, line_number""",
    "login_roles": """SELECT rolname, rolsuper, rolcanlogin, rolreplication, rolbypassrls
        FROM pg_roles WHERE rolcanlogin ORDER BY rolname""",
    "clients": """SELECT datname, usename, client_addr::text AS client_address,
        client_addr IS NULL AS unix_socket, (application_name <> '') AS application_name_present,
        state, ssl, count(*) AS connections
        FROM pg_stat_activity a LEFT JOIN pg_stat_ssl s USING (pid)
        WHERE backend_type = 'client backend' AND pid <> pg_backend_pid()
        GROUP BY datname, usename, client_addr, (application_name <> ''), state, ssl
        ORDER BY datname, usename, client_addr, state""",
}


def capture(argv, run=subprocess.run):
    try:
        result = run(argv, stdin=subprocess.DEVNULL, capture_output=True,
            text=True, check=False, timeout=10, env=ENV)
    except (OSError, subprocess.TimeoutExpired):
        return {"status": "unavailable", "reason": "missing permission, command, or timely response"}
    if result.returncode != 0:
        # stderr may contain a connection string or authentication diagnostic.
        return {"status": "unavailable", "exit_code": result.returncode,
            "reason": "permission or command failure; diagnostic text intentionally omitted"}
    return {"status": "available", "output": result.stdout}


def database_snapshot(run=subprocess.run):
    result = {}
    for name, query in QUERIES.items():
        sql = "SELECT coalesce(json_agg(snapshot), '[]'::json) FROM (" + query + ") AS snapshot"
        response = capture([PSQL, "-X", "-w", "-A", "-t", "-v", "ON_ERROR_STOP=1",
            "-h", "/var/run/postgresql", "-p", "5432", "-U", "laplace_admin", "-d", "laplace", "-c", sql], run)
        if response["status"] == "available":
            try:
                rows = json.loads(response.pop("output"))
                if not isinstance(rows, list):
                    raise ValueError("not an array")
                response["rows"] = rows
            except (ValueError, TypeError):
                response = {"status": "unavailable", "reason": "invalid metadata response; text omitted"}
        result[name] = response
    return result


def network_snapshot(run=subprocess.run):
    # ss deliberately excludes -p: no process arguments/environments are collected.
    commands = {
        "listeners_5432": ["ss", "-H", "-lnt", "sport = :5432"],
        "established_5432": ["ss", "-H", "-nt", "state", "established", "( sport = :5432 or dport = :5432 )"],
        "addresses": ["ip", "-brief", "address", "show"],
        "ipv4_default_route": ["ip", "-4", "route", "show", "default"],
        "ipv6_default_route": ["ip", "-6", "route", "show", "default"],
        "ufw_rules": ["ufw", "status", "verbose"],
        "nft_rules": ["nft", "list", "ruleset"],
    }
    # Rule output can include administrator comments; report inspectability only.
    # An administrator separately reviews full rules, not a public CI artifact.
    results = {name: capture(argv, run) for name, argv in commands.items()}
    for name in ("ufw_rules", "nft_rules"):
        results[name].pop("output", None)
    return results


def main():
    if len(sys.argv) != 1:
        print("Usage: python3 scripts/audit-pg-access.py (local peer only)", file=sys.stderr)
        return 2
    report = {
        "observed_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "host": socket.gethostname(), "effective_uid": os.geteuid(),
        "database": database_snapshot(), "network": network_snapshot(),
        "limits": [
            "HBA/ident views parse files on disk; they do not prove which rules were last loaded.",
            "Client rows are a point-in-time snapshot, not a history of intermittent consumers.",
            "Firewall inspectability is not a verdict on effective reachability or router/NAT exposure.",
            "No TCP login probe, password/hash inspection, reload, configuration change or external scan was performed.",
        ],
    }
    print(json.dumps(report, indent=2))
    return 0 if all(item["status"] == "available" for item in report["database"].values()) else 1


if __name__ == "__main__":
    sys.exit(main())

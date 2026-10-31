#!/usr/bin/env bash
# Restart a stale-busy GitHub Actions runner after all inactivity probes pass.
# Active jobs, ingestion, database work, or CLI processes prevent the restart.
set -euo pipefail

UNIT="actions.runner.SaltyPatron-Laplace.hart-server.service"
PSQL=(psql -h localhost -U postgres -d laplace -AtX)

fail() { echo "REFUSED: $1" >&2; exit 1; }

# 1. No ingest run open in the journal (the substrate's own ledger).
running=$("${PSQL[@]}" -c "SELECT count(*) FROM ops.ingest_runs(50) WHERE status = 'running';") \
    || fail "journal query failed; runner inactivity could not be verified"
[ "$running" = "0" ] || fail "ingest_run_journal shows $running run(s) status=running"

# 2. No active backend on the database (COPY, fold, anything non-idle).
active=$("${PSQL[@]}" -c "SELECT count(*) FROM pg_stat_activity WHERE datname='laplace' AND state <> 'idle' AND pid <> pg_backend_pid();") \
    || fail "pg_stat_activity probe failed — fail closed"
[ "$active" = "0" ] || fail "$active active backend(s) on laplace"

# 3. No Laplace.Cli process (an ingest the journal has not seen yet).
if pgrep -f "Laplace\.Cli" >/dev/null 2>&1; then
    fail "a Laplace.Cli process is running"
fi

# 4. No Runner.Worker (a CI job is genuinely executing — busy is NOT stale).
if pgrep -f "Runner\.Worker" >/dev/null 2>&1; then
    fail "Runner.Worker exists — the runner is actually working, not wedged"
fi

echo "guards passed: journal quiet, backends idle, no CLI, no worker"
# Absolute path: sudoers matches commands by full path, so a bare `systemctl`
# resolving to /bin on some host would false-refuse against the installed rule.
sudo -n /usr/bin/systemctl restart "$UNIT" \
    || fail "sudo -n restart failed — passwordless rule for $UNIT not installed"
sleep 3
systemctl is-active "$UNIT" >/dev/null || fail "$UNIT did not come back active"
echo "bounced: $UNIT active"

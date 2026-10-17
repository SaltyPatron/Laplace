#!/usr/bin/env python3
"""The shared hart-server host lock: a delivery phase waits for another repository's host operation (an Engine CI
parity ingest, the MCP deploy) up to the declared deadline, and the deadline is the repository variable with the same
default Laplace-Operations' delivery uses (Laplace-Operations#131), never a literal short enough to fail a delivery
while an ingest holds the lock (runs 39029518051, 39033153778: "host lock deadline expired ... no deployment phase ran").
Every job that runs deploy/linux/site.sh under the deadline gives itself time to wait the whole default deadline and still do its work."""
import pathlib
import re
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
DECLARED = "${{ vars.LAPLACE_HOST_LOCK_WAIT_SECONDS || '3600' }}"
DEFAULT_SECONDS = 3600


def jobs(text):
    """(name, body) for each job under jobs:, by its two-space indentation."""
    lines = text.splitlines(); out = []; inside = False; name = None; body = []
    for line in lines:
        if line.startswith("jobs:"):
            inside = True; continue
        if not inside:
            continue
        m = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if m:
            if name: out.append((name, "\n".join(body)))
            name, body = m.group(1), []
        elif name:
            body.append(line)
    if name: out.append((name, "\n".join(body)))
    return out


class HostLockWait(unittest.TestCase):
    def test_no_literal_deadline(self):
        for wf in sorted(WORKFLOWS.glob("*.yml")):
            for n, line in enumerate(wf.read_text(encoding="utf-8").splitlines(), 1):
                if re.search(r"LAPLACE_HOST_LOCK_WAIT_SECONDS:\s*", line):
                    value = line.split(":", 1)[1].strip()
                    self.assertEqual(value, DECLARED, f"{wf.name}:{n}: the deadline is the repository variable, not {value}")

    def test_site_jobs_outlast_the_deadline(self):
        for wf in sorted(WORKFLOWS.glob("*.yml")):
            text = wf.read_text(encoding="utf-8")
            if "LAPLACE_HOST_LOCK_WAIT_SECONDS" not in text:
                continue
            workflow_level = "LAPLACE_HOST_LOCK_WAIT_SECONDS" in text.split("\njobs:", 1)[0]
            for name, body in jobs(text):
                if "site.sh" not in body or not (workflow_level or "LAPLACE_HOST_LOCK_WAIT_SECONDS" in body):
                    continue                                   # a job the deadline is not declared for refuses at once (0)
                m = re.search(r"^    timeout-minutes:\s*(\d+)", body, re.M)
                self.assertIsNotNone(m, f"{wf.name}: job {name} runs site.sh with no timeout")
                self.assertGreater(int(m.group(1)) * 60, DEFAULT_SECONDS,
                                   f"{wf.name}: job {name} ({m.group(1)} min) cannot wait the {DEFAULT_SECONDS}s default deadline")

    def test_site_sh_waits_then_refuses(self):
        site = (ROOT / "deploy" / "linux" / "site.sh").read_text(encoding="utf-8")
        self.assertIn('wait="${LAPLACE_HOST_LOCK_WAIT_SECONDS:-0}"', site)
        self.assertIn('host_lock_hold "$mode" 8 "$path" "$wait"', site)   # through the turnstile (deploy/linux/host-lock.sh, #1899)
        self.assertIn("host lock deadline expired", site)
        hold = (ROOT / "deploy" / "linux" / "host-lock.sh").read_text(encoding="utf-8")
        self.assertIn('flock -x -w "$wait" 7', hold)                      # the turnstile waits no longer than the deadline
        self.assertIn('flock "$flag" -w "$left" "$fd"', hold)              # nor the lock, with what of it is left


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False, verbosity=1).result.wasSuccessful() else 1)
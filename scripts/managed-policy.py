#!/usr/bin/env python3
"""Match application service data to an installed, compatible managed policy.

This unprivileged owner never installs privileged code or changes sudo authority.
The installed helper remains responsible for validating and reconciling every
unit, preserving stop intent, and committing or rolling back publication.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys

NAMES = ("laplace-managed-deploy", "laplace-service-control")
# Reviewed predecessor: identical verbs, unit hardening, identity, TLS and
# transaction semantics. Only its owned scratch directories have legacy names.
SCRATCH_PREDECESSOR = (
    "c87daff9fc8a8e4a48657a3a55bfed41dd9bb46d",
    "dfade6c9b72277ed4d95e64376d283a4aa8821d2",
    "14d7470f276ed33562451a12c0724767bd43aa82",
)
# This installed generation has the same publication, service validation and
# rollback implementation. The source changes only bootstrap's backup-directory
# mode and nginx's loopback allow rules. Neither requires replacing the helper
# to publish application payloads; retain its exact bytes in the staged receipt.
PUBLICATION_PREDECESSOR = (
    "bf491d485c5399241846226016465b841a4c8973",
    "cf9a67f6271e3208bfda6c1d0d6904105ce17ac6",
    "14d7470f276ed33562451a12c0724767bd43aa82",
)

def blob(raw):
    return hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest()

def select_profile(source, installed):
    if source[NAMES[1]] != installed[NAMES[1]]:
        raise ValueError("installed service-control policy is incompatible with this publication")
    if source[NAMES[0]] == installed[NAMES[0]]:
        return "same-policy"
    identities = (blob(source[NAMES[0]]), blob(installed[NAMES[0]]), blob(installed[NAMES[1]]))
    if identities == SCRATCH_PREDECESSOR:
        return "retained-legacy-scratch"
    if identities == PUBLICATION_PREDECESSOR:
        return "retained-publication-policy"
    raise ValueError("installed managed policy requires a supported policy upgrade")

def trusted_bytes(path, trusted_uid=0):
    metadata = path.lstat()
    parent = path.parent.lstat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != trusted_uid \
            or stat.S_IMODE(metadata.st_mode) != 0o755 \
            or not stat.S_ISDIR(parent.st_mode) or parent.st_uid != trusted_uid \
            or parent.st_mode & 0o022:
        raise ValueError("installed managed policy must be a root-owned regular mode-0755 file in its trusted directory")
    raw = path.read_bytes()
    current = path.lstat()
    if (metadata.st_dev, metadata.st_ino, metadata.st_size, metadata.st_mtime_ns) != \
            (current.st_dev, current.st_ino, current.st_size, current.st_mtime_ns):
        raise ValueError("installed managed policy changed during observation")
    return raw

def unit_text(text, name, profile):
    if profile in ("same-policy", "retained-publication-policy"):
        return text
    if profile != "retained-legacy-scratch" or name not in ("mcp", "lichess"):
        raise ValueError("unknown managed policy profile")
    # Only these four complete directives change. Executables, accounts,
    # privileges, environment secrets and every hardening directive are retained.
    for directive in ("Environment=TMPDIR=", "Environment=TMP=", "Environment=TEMP=", "ReadWritePaths="):
        old = directive + "/build/laplace/work/" + name
        new = directive + "/build/laplace/work/legacy-" + name
        lines = text.splitlines(keepends=True)
        if sum(line.rstrip("\r\n") == old for line in lines) != 1:
            raise ValueError("managed service scratch directive is missing or ambiguous")
        text = "".join(new + line[len(old):] if line.rstrip("\r\n") == old else line for line in lines)
    return text

def select(root, installed_dir=Path("/usr/local/libexec"), trusted_uid=0):
    source = {name: (root / "deploy/linux" / name).read_bytes() for name in NAMES}
    installed = {name: trusted_bytes(installed_dir / name, trusted_uid) for name in NAMES}
    profile = select_profile(source, installed)
    units = {name: unit_text((root / "deploy/linux/managed-services" /
                             ("laplace-" + name + ".service")).read_text(), name, profile)
             for name in ("mcp", "lichess")}
    receipt = {"schema": "laplace.managed-policy-compatibility/v1", "profile": profile,
               "privilegedPolicyReplaced": False,
               "sourcePolicy": {name: blob(raw) for name, raw in source.items()},
               "installedPolicy": {name: blob(raw) for name, raw in installed.items()},
               "scratchDirectories": {
                   name: "/build/laplace/work/" + ("legacy-" if profile == "retained-legacy-scratch" else "") + name
                   for name in ("mcp", "lichess")}}
    return installed, units, receipt

def stage(root, destination):
    installed, units, receipt = select(root)
    # deploy.sh owns a fresh private staging directory; never rewrite a serving
    # policy directory here. The root helper independently checks these bytes.
    destination.mkdir(mode=0o755)
    for name, text in units.items():
        (destination / ("laplace-" + name + ".service")).write_text(text)
    (destination / NAMES[0]).write_bytes(installed[NAMES[0]])
    (destination / "policy-compatibility.json").write_text(json.dumps(receipt, sort_keys=True) + "\n")
    return receipt

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--stage", type=Path)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    receipt = stage(root, args.stage) if args.stage is not None else select(root)[2]
    print(json.dumps(receipt, sort_keys=True))

if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print("managed policy compatibility failed: " + str(error), file=sys.stderr)
        raise SystemExit(1)

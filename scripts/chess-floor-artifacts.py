#!/usr/bin/env python3
"""Validate recorded chess inputs and atomically publish an immutable floor pair."""
import argparse
import ctypes
import hashlib
import json
import mmap
import os
from pathlib import Path
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import uuid

EXPORT_SCHEMA = "laplace.chess-recorded-floor-export/v1"
PAIR_SCHEMA = "laplace.chess-floor-pair/v1"
NAMES = ("laplace_chess_position_perfcache.bin", "laplace_chess_transition_perfcache.bin")
ROLES = {"position-surfaces", "transition-floor", "inventory-summary", "witnessed-inputs"}


def sha256(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            value.update(block)
    return value.hexdigest()


def read_json(path):
    path = Path(path)
    if path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("receipt exceeds the bounded JSON envelope")
    with path.open(encoding="utf-8") as source:
        return json.load(source)


def sync_directory(path):
    if os.name == "posix":
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex)
    try:
        with temporary.open("x", encoding="utf-8") as target:
            json.dump(value, target, sort_keys=True, separators=(",", ":"), allow_nan=False)
            target.write("\n")
            target.flush()
            os.fsync(target.fileno())
        os.replace(temporary, path)
        sync_directory(path.parent)
    finally:
        temporary.unlink(missing_ok=True)


def regular_child(root, relative):
    relative = Path(relative)
    if relative.is_absolute() or not relative.parts or any(p in ("", ".", "..") for p in relative.parts):
        raise ValueError("export file must be a relative descendant")
    current = root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError("export files may not traverse symlinks")
    if not stat.S_ISREG(current.stat().st_mode):
        raise ValueError("export input must be a regular file")
    return current


def validate_export(receipt):
    receipt = Path(receipt)
    if not receipt.is_absolute() or receipt.is_symlink() or not receipt.is_file():
        raise ValueError("completed export receipt must be an absolute regular file")
    receipt = receipt.resolve()
    before = sha256(receipt)
    value = read_json(receipt)
    if value.get("schema") != EXPORT_SCHEMA or value.get("status") != "completed":
        raise ValueError("only a completed recorded-floor export can be selected")
    if value.get("inventory_complete") is not True or value.get("snapshot") is not False:
        raise ValueError("export must retain its complete observed-read-interval qualification")
    if value.get("observation_scope") != "observed-read-interval":
        raise ValueError("unsupported export observation scope")
    first, last = value.get("database_before"), value.get("database_after")
    if not isinstance(first, dict) or first != last or set(first) != {"database", "database_oid", "system_identifier"}:
        raise ValueError("database identity is missing or changed during export")
    if any(v is None or str(v) == "" for v in first.values()):
        raise ValueError("database identity is incomplete")
    for name in ("selected_playings", "exported_playings", "position_occurrences",
                 "transition_occurrences", "unique_transitions"):
        if type(value.get(name)) is not int or value[name] < 0:
            raise ValueError("invalid export coverage count: " + name)
    if value["selected_playings"] != value["exported_playings"]:
        raise ValueError("selected playings were not completely exported")
    if value["position_occurrences"] != value["transition_occurrences"] + value["exported_playings"]:
        raise ValueError("complete lines require one more position occurrence than transitions")
    if value["unique_transitions"] > value["transition_occurrences"]:
        raise ValueError("unique transition count exceeds occurrences")
    files = value.get("files")
    if not isinstance(files, list) or len(files) != len(ROLES):
        raise ValueError("export must bind all four declared files")
    result, paths = {}, set()
    for row in files:
        if not isinstance(row, dict):
            raise ValueError("export file entry must be an object")
        role = row.get("role")
        if role not in ROLES or role in result:
            raise ValueError("duplicate or unknown export role")
        path = regular_child(receipt.parent, row.get("path", ""))
        if path in paths:
            raise ValueError("two export roles share a file")
        paths.add(path)
        if type(row.get("bytes")) is not int or row["bytes"] != path.stat().st_size:
            raise ValueError("export file length changed: " + role)
        if row.get("sha256") != sha256(path):
            raise ValueError("export file hash changed: " + role)
        result[role] = str(path)
    inventory = read_json(result["inventory-summary"])
    expected_identity = {"Database": first["database"], "DatabaseOid": first["database_oid"],
                         "SystemIdentifier": first["system_identifier"]}
    if (inventory.get("Status") != "completed" or inventory.get("ReachedEnd") is not True
            or inventory.get("Scope") != "observed-read-interval"
            or inventory.get("Snapshot") is not False
            or inventory.get("ProvesHistoricalCoverage") is not False):
        raise ValueError("inventory does not establish a complete observed read interval")
    if inventory.get("DatabaseBefore") != expected_identity or inventory.get("DatabaseAfter") != expected_identity:
        raise ValueError("inventory and export database identities differ")
    for name in ("SelectedBefore", "SelectedAfter", "Enumerated", "Retained"):
        if type(inventory.get(name)) is not int or inventory[name] != value["selected_playings"]:
            raise ValueError("inventory and exported playing counts differ")
    if (type(inventory.get("White")) is not int or type(inventory.get("Black")) is not int
            or inventory["White"] < 0 or inventory["Black"] < 0
            or inventory["White"] + inventory["Black"] != value["selected_playings"]
            or inventory.get("Unclassified") != 0 or inventory.get("PendingPlayingIds") != []):
        raise ValueError("inventory has unclassified or inconsistent playing coverage")
    witnessed = Path(result["witnessed-inputs"])
    if (inventory.get("InputsSha256") != sha256(witnessed)
            or inventory.get("RetainedBytes") != witnessed.stat().st_size):
        raise ValueError("inventory does not bind the retained witnessed inputs")
    if sha256(receipt) != before:
        raise ValueError("export receipt changed during validation")
    return {"receipt": str(receipt), "receipt_sha256": before, "roles": result,
            "coverage": {k: value[k] for k in ("selected_playings", "exported_playings",
                         "position_occurrences", "transition_occurrences", "unique_transitions")},
            "database": first}


def selected_export(prefix):
    explicit = os.environ.get("LAPLACE_CHESS_CORPUS_EXPORT")
    if explicit:
        return validate_export(explicit)
    selection = Path(prefix) / "etc/chess-corpus-export.json"
    if not selection.exists():
        return None
    saved = read_json(selection)
    actual = validate_export(saved["receipt"])
    if actual["receipt_sha256"] != saved.get("receipt_sha256"):
        raise ValueError("selected corpus receipt changed")
    return actual


def native_hasher(library):
    library = Path(library)
    if not library.is_absolute() or not library.is_file():
        raise ValueError("an exact built native core library is required")
    dll = ctypes.CDLL(str(library))
    method = dll.hash128_blake3
    method.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p]
    method.restype = None

    def hash_body(mapping, size):
        anchor = ctypes.c_ubyte.from_buffer(mapping)
        result = (ctypes.c_uint64 * 2)()
        try:
            method(ctypes.addressof(anchor), size, ctypes.byref(result))
            return bytes(result)
        finally:
            del anchor
    return hash_body


def validate_floor(path, kind, hash_body):
    path = Path(path)
    if kind not in ("position", "transition"):
        raise ValueError("unknown chess floor kind")
    header, record, magic = (128, 80, b"LCHP") if kind == "position" else (64, 32, b"LCHT")
    with path.open("rb") as source:
        initial = os.fstat(source.fileno())
        if initial.st_size < header + 16:
            raise ValueError("floor is shorter than its fixed framing")
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_COPY) as data:
            if data[:4] != magic or struct.unpack_from("<I", data, 4)[0] != 1:
                raise ValueError("floor magic/version mismatch")
            count = struct.unpack_from("<Q", data, 8)[0]
            if initial.st_size != header + count * record + 16:
                raise ValueError("floor count/length mismatch")
            if kind == "position" and struct.unpack_from("<QQ", data, 16) != (80, 128):
                raise ValueError("position record layout mismatch")
            if hash_body(data, initial.st_size - 16) != data[-16:]:
                raise ValueError("floor BLAKE3 body checksum mismatch")
            previous = None
            for offset in range(header, initial.st_size - 16, record):
                key = data[offset:offset + 16]
                if previous is not None and key <= previous:
                    raise ValueError("floor keys are not sorted and unique")
                previous = key
            digest = hashlib.sha256()
            for offset in range(0, initial.st_size, 1 << 20):
                digest.update(data[offset:offset + (1 << 20)])
        final = os.fstat(source.fileno())
        if (initial.st_size, initial.st_mtime_ns, initial.st_ctime_ns) != (
                final.st_size, final.st_mtime_ns, final.st_ctime_ns):
            raise ValueError("floor changed during validation")
    return {"bytes": initial.st_size, "sha256": digest.hexdigest(), "records": count}


def require_transition_coverage(corpus, merged, hash_body):
    validate_floor(corpus, "transition", hash_body)
    validate_floor(merged, "transition", hash_body)
    with Path(corpus).open("rb") as incoming, Path(merged).open("rb") as output:
        source_count = struct.unpack("<Q", incoming.read(16)[8:])[0]
        output_count = struct.unpack("<Q", output.read(16)[8:])[0]
        incoming.seek(64)
        output.seek(64)
        candidate = output.read(32) if output_count else None
        remaining = output_count
        for _ in range(source_count):
            record = incoming.read(32)
            while candidate is not None and candidate[:16] < record[:16]:
                remaining -= 1
                candidate = output.read(32) if remaining else None
            if candidate != record:
                raise ValueError("merged transition floor omitted or changed a corpus transition")


def seal_pair(position, transition, library, emitter, seed_surfaces, receipt, export=None):
    hash_body = native_hasher(library)
    provenance = validate_export(export) if export else None
    floor_before = [(Path(path).stat().st_size, sha256(path)) for path in (position, transition)]
    producer_before = {"binary_sha256": sha256(emitter), "seed_surfaces_sha256": sha256(seed_surfaces)}
    arguments = [str(emitter), "--verify-existing", "--output", str(position),
                 "--surfaces", str(seed_surfaces)]
    if provenance:
        arguments += ["--additional-surfaces", provenance["roles"]["position-surfaces"]]
    # Verification uses the actual producer's recipe and full reader, with no writes.
    subprocess.run(arguments, check=True, timeout=1800, stdout=subprocess.DEVNULL)
    facts = [validate_floor(p, k, hash_body)
             for p, k in zip((position, transition), ("position", "transition"))]
    if floor_before != [(fact["bytes"], fact["sha256"]) for fact in facts]:
        raise ValueError("floor files changed across producer recipe verification")
    if provenance:
        require_transition_coverage(provenance["roles"]["transition-floor"], transition, hash_body)
        if validate_export(export) != provenance:
            raise ValueError("corpus input changed while sealing the floor pair")
    producer_after = {"binary_sha256": sha256(emitter), "seed_surfaces_sha256": sha256(seed_surfaces)}
    if producer_before != producer_after:
        raise ValueError("producer or seed surfaces changed during verification")
    if any(sha256(path) != fact["sha256"] for path, fact in zip((position, transition), facts)):
        raise ValueError("floor files changed while verifying corpus coverage")
    manifest = {"schema": PAIR_SCHEMA, "files": dict(zip(NAMES, facts)),
                "export": provenance,
                "producer": producer_after,
                "coverage_note": "position records include finite alphabets and seed positions"}
    atomic_json(receipt, manifest)
    return manifest


def validate_pair_receipt(receipt, facts):
    value = read_json(receipt)
    if value.get("schema") != PAIR_SCHEMA or value.get("files") != dict(zip(NAMES, facts)):
        raise ValueError("sealed pair differs from the exact built floors")
    producer = value.get("producer")
    if not isinstance(producer, dict) or set(producer) != {"binary_sha256", "seed_surfaces_sha256"}:
        raise ValueError("sealed pair lacks its producer/input binding")
    if any(not isinstance(h, str) or len(h) != 64 or any(c not in "0123456789abcdef" for c in h)
           for h in producer.values()):
        raise ValueError("invalid sealed producer/input hash")
    provenance = value.get("export")
    if provenance and validate_export(provenance["receipt"]) != provenance:
        raise ValueError("sealed recorded corpus input changed")
    return value


def copy_verified(source, target, expected):
    with Path(source).open("rb") as incoming, Path(target).open("xb") as outgoing:
        shutil.copyfileobj(incoming, outgoing, 1 << 20)
        outgoing.flush()
        os.fsync(outgoing.fileno())
    if target.stat().st_size != expected["bytes"] or sha256(target) != expected["sha256"]:
        raise ValueError("floor changed while copying")
    target.chmod(0o664)


def retain_generation(root, sources, manifest):
    encoded = json.dumps(manifest, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
    identity = hashlib.sha256(encoded).hexdigest()
    destination = root / "generations" / identity
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        if destination.is_symlink() or read_json(destination / "receipt.json") != manifest:
            raise ValueError("existing generation identity conflicts with its receipt")
        for name, row in manifest["files"].items():
            if (destination / name).stat().st_size != row["bytes"] or sha256(destination / name) != row["sha256"]:
                raise ValueError("retained generation content changed")
        return destination
    temporary = Path(tempfile.mkdtemp(prefix=".staging-", dir=destination.parent))
    try:
        for name, source in zip(NAMES, sources):
            copy_verified(source, temporary / name, manifest["files"][name])
        atomic_json(temporary / "receipt.json", manifest)
        temporary.chmod(0o775)
        sync_directory(temporary)
        os.rename(temporary, destination)
        sync_directory(destination.parent)
        return destination
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


def verify_generation(path, hash_body):
    path = Path(path)
    if not path.is_dir() or path.is_symlink():
        raise ValueError("retained generation must be an ordinary directory")
    receipt = read_json(path / "receipt.json")
    if receipt.get("schema") != PAIR_SCHEMA or set(receipt.get("files", {})) != set(NAMES):
        raise ValueError("retained generation receipt is incomplete")
    for name, kind in zip(NAMES, ("position", "transition")):
        if validate_floor(path / name, kind, hash_body) != receipt["files"][name]:
            raise ValueError("retained generation differs from its complete pair receipt")


def replace_link(path, target):
    temporary = path.with_name("." + path.name + "." + uuid.uuid4().hex)
    try:
        temporary.symlink_to(target)
        os.replace(temporary, path)
        sync_directory(path.parent)
    finally:
        temporary.unlink(missing_ok=True)


def install_pair(prefix, position, transition, library, pair_receipt, hash_body=None):
    prefix = Path(prefix)
    if sys.byteorder != "little" or ctypes.sizeof(ctypes.c_void_p) != 8:
        raise ValueError("chess v1 publication requires the current little-endian 64-bit native ABI")
    if not prefix.is_absolute():
        raise ValueError("install prefix must be absolute")
    hash_body = hash_body or native_hasher(library)
    sources = (Path(position), Path(transition))
    facts = [validate_floor(p, k, hash_body) for p, k in zip(sources, ("position", "transition"))]
    manifest = validate_pair_receipt(pair_receipt, facts)
    share = prefix / "share/laplace"
    share.mkdir(parents=True, exist_ok=True)
    root = share / "chess-floor"
    root.mkdir(exist_ok=True)
    current = root / "current"
    destination = retain_generation(root, sources, manifest)
    # On first adoption preserve the old complete pair while compatibility links
    # are installed. The one current symlink is the publication commit point.
    if not current.exists() and not current.is_symlink():
        old = tuple(share / name for name in NAMES)
        if all(p.is_file() and not p.is_symlink() for p in old):
            prior = [validate_floor(p, k, hash_body) for p, k in zip(old, ("position", "transition"))]
            prior_manifest = {"schema": PAIR_SCHEMA, "files": dict(zip(NAMES, prior)),
                              "export": None, "coverage_note": "retained legacy pair; corpus provenance unknown"}
            initial = retain_generation(root, old, prior_manifest)
        elif all(not p.exists() and not p.is_symlink() for p in old):
            initial = destination
        else:
            raise ValueError("legacy floor pair is incomplete or has unsupported links")
        replace_link(current, str(initial.relative_to(root)))
    elif not current.is_symlink() or current.resolve(strict=True).parent != (root / "generations").resolve():
        raise ValueError("existing current generation is not an owned generation link")
    else:
        verify_generation(current.resolve(strict=True), hash_body)
    for name in NAMES:
        expected = "chess-floor/current/" + name
        path = share / name
        if not path.is_symlink() or os.readlink(path) != expected:
            replace_link(path, expected)
    replace_link(current, str(destination.relative_to(root)))
    return {"generation": destination.name, "receipt": str(destination / "receipt.json"),
            "files": [str(share / name) for name in NAMES],
            "manifest_files": [str(share / name) for name in NAMES] +
                              [str(current / "receipt.json")]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="mode", required=True)
    validate = sub.add_parser("validate-export")
    validate.add_argument("--receipt", required=True, type=Path)
    select = sub.add_parser("select-export")
    select.add_argument("--receipt", required=True, type=Path)
    select.add_argument("--prefix", required=True, type=Path)
    selected = sub.add_parser("selected-export")
    selected.add_argument("--prefix", required=True, type=Path)
    selected.add_argument("--path-only", action="store_true")
    seal = sub.add_parser("seal-pair")
    for flag in ("position", "transition", "native-library", "position-emitter", "seed-surfaces", "receipt"):
        seal.add_argument("--" + flag, required=True, type=Path)
    seal.add_argument("--export-receipt", type=Path)
    install = sub.add_parser("install-pair")
    for flag in ("prefix", "position", "transition", "native-library"):
        install.add_argument("--" + flag, required=True, type=Path)
    install.add_argument("--pair-receipt", required=True, type=Path)
    install.add_argument("--manifest-only", action="store_true")
    args = parser.parse_args()
    if args.mode == "validate-export":
        result = validate_export(args.receipt)
    elif args.mode == "select-export":
        result = validate_export(args.receipt)
        atomic_json(args.prefix / "etc/chess-corpus-export.json",
                    {key: result[key] for key in ("receipt", "receipt_sha256")})
    elif args.mode == "selected-export":
        result = selected_export(args.prefix)
        if args.path_only:
            print(result["receipt"] if result else "")
            return
    elif args.mode == "seal-pair":
        result = seal_pair(args.position, args.transition, args.native_library,
                           args.position_emitter, args.seed_surfaces, args.receipt, args.export_receipt)
    else:
        prefix = args.prefix
        if os.environ.get("DESTDIR"):
            prefix = Path(os.environ["DESTDIR"]) / str(prefix).lstrip("/")
        result = install_pair(prefix, args.position, args.transition, args.native_library, args.pair_receipt)
        if args.manifest_only:
            # CMake install manifests record logical paths, excluding DESTDIR.
            for path in result["manifest_files"]:
                print(str(args.prefix / Path(path).relative_to(prefix)))
            return
    print(json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError, subprocess.SubprocessError) as error:
        raise SystemExit("chess floor artifacts: " + str(error))

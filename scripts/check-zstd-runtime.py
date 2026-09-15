#!/usr/bin/env python3
"""Exercise the native Zstandard streaming ABI used by chess corpus ingestion."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys

FRAME = bytes.fromhex("28b52ffd0458950200d2c4111980b939e8ffff81055e128980723abab4cdcceebd9136e8767acb5d3aca47a131329ca811355277f974d4bd4cd11480975f378230600b2fafdcba0783002fbf4d03f72674c1600200805398a70e661a3a1174")
GAME = b'[Event "Test"]\n[White "A"]\n[Black "B"]\n[Result "1-0"]\n\n1. e4 e5 2. Nf3 1-0\n'
EXPECTED = GAME + b'\n' + GAME.replace(b'"A"', b'"C"')


class Buffer(ctypes.Structure):
    _fields_ = [("data", ctypes.c_void_p), ("size", ctypes.c_size_t), ("pos", ctypes.c_size_t)]


def loaded_path(library):
    if os.name == "nt":
        target = ctypes.create_unicode_buffer(32768)
        get_name = ctypes.windll.kernel32.GetModuleFileNameW
        get_name.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint32]
        if get_name(library._handle, target, len(target)):
            return Path(target.value)
    elif sys.platform.startswith("linux"):
        address = ctypes.cast(library.ZSTD_versionNumber, ctypes.c_void_p).value
        for line in Path("/proc/self/maps").read_text().splitlines():
            fields = line.split(maxsplit=5)
            low, high = (int(part, 16) for part in fields[0].split("-"))
            if low <= address < high and len(fields) == 6 and fields[-1].startswith("/"):
                return Path(fields[-1])
    path = Path(library._name)
    return path if path.is_absolute() and path.is_file() else None


def probe(path=None, window_log_max=27, expected_version=None):
    if not 10 <= window_log_max <= (31 if ctypes.sizeof(ctypes.c_void_p) == 8 else 30):
        raise ValueError("LAPLACE_ZSTD_WINDOW_LOG_MAX must be 10 through 31 (30 on 32-bit hosts)")
    if path and not Path(path).is_absolute():
        raise ValueError("LAPLACE_ZSTD_LIBRARY must be an absolute shared-library path")
    names = ([str(path)] if path else ["libzstd.dll", "zstd.dll"] if os.name == "nt"
             else ["libzstd.1.dylib", "libzstd.dylib"] if sys.platform == "darwin"
             else ["libzstd.so.1", "libzstd.so"])
    library = None
    for name in names:
        try:
            library = ctypes.CDLL(name)
            break
        except OSError:
            continue
    if library is None:
        raise RuntimeError("Native Zstandard runtime unavailable; install libzstd or set LAPLACE_ZSTD_LIBRARY. Tried: " + ", ".join(names))
    signatures = {
        "ZSTD_createDCtx": (ctypes.c_void_p, []),
        "ZSTD_freeDCtx": (ctypes.c_size_t, [ctypes.c_void_p]),
        "ZSTD_DCtx_setParameter": (ctypes.c_size_t, [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]),
        "ZSTD_decompressStream": (ctypes.c_size_t, [ctypes.c_void_p, ctypes.POINTER(Buffer), ctypes.POINTER(Buffer)]),
        "ZSTD_isError": (ctypes.c_uint, [ctypes.c_size_t]),
        "ZSTD_getErrorName": (ctypes.c_char_p, [ctypes.c_size_t]),
        "ZSTD_versionString": (ctypes.c_char_p, []),
        "ZSTD_versionNumber": (ctypes.c_uint, []),
    }
    try:
        for name, (result, arguments) in signatures.items():
            function = getattr(library, name)
            function.restype, function.argtypes = result, arguments
    except AttributeError as error:
        raise RuntimeError("Native Zstandard library lacks the required streaming ABI") from error

    def check(result):
        if library.ZSTD_isError(result):
            raise RuntimeError("Native Zstandard probe failed: " + library.ZSTD_getErrorName(result).decode())
        return result

    context = library.ZSTD_createDCtx()
    if not context:
        raise RuntimeError("Native Zstandard could not allocate a decoder context")
    try:
        check(library.ZSTD_DCtx_setParameter(context, 100, window_log_max))
        source = ctypes.create_string_buffer(FRAME)
        destination = ctypes.create_string_buffer(len(EXPECTED))
        incoming = Buffer(ctypes.addressof(source), len(FRAME), 0)
        outgoing = Buffer(ctypes.addressof(destination), len(EXPECTED), 0)
        remaining = check(library.ZSTD_decompressStream(context, ctypes.byref(outgoing), ctypes.byref(incoming)))
        if remaining or incoming.pos != len(FRAME) or outgoing.pos != len(EXPECTED) or destination.raw != EXPECTED:
            raise RuntimeError("Native Zstandard probe did not recover the complete checksummed PGN fixture")
    finally:
        library.ZSTD_freeDCtx(context)
    version = library.ZSTD_versionString().decode()
    if expected_version and version != expected_version:
        raise RuntimeError(f"Native Zstandard {version} loaded; the verified release is {expected_version}")
    try:
        actual = loaded_path(library)
        digest = hashlib.sha256(actual.read_bytes()).hexdigest() if actual else None
    except OSError:
        actual, digest = None, None
    return {"library": str(actual) if actual else None, "requested_library": library._name,
            "library_sha256": digest,
            "version": version, "window_log_max": window_log_max,
            "window_limit_bytes": 1 << window_log_max, "stream_buffer_bytes": 2 * 128 * 1024,
            "fixture_sha256": hashlib.sha256(FRAME).hexdigest(), "decoded_bytes": len(EXPECTED),
            "verification": "native streaming ABI and exact checksummed PGN fixture; corpus ingestion is checked separately"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", help="Exact native library; overrides installed/process configuration")
    parser.add_argument("--prefix", type=Path, help="Read the same installed chess settings as the application")
    parser.add_argument("--require-version", help="Require this exact verified native release")
    parser.add_argument("--window-log-max", type=int)
    args = parser.parse_args()
    try:
        config = {key: os.environ[key] for key in ("LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_WINDOW_LOG_MAX") if os.environ.get(key)}
        if args.prefix:
            spec = importlib.util.spec_from_file_location("chess_readiness", Path(__file__).with_name("check-chess-dependencies.py"))
            readiness = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(readiness)
            config = readiness.configuration(args.prefix)
        library = args.library or config.get("LAPLACE_ZSTD_LIBRARY")
        window = args.window_log_max if args.window_log_max is not None else int(config.get("LAPLACE_ZSTD_WINDOW_LOG_MAX", "27"))
        print(json.dumps(probe(library, window, args.require_version), indent=2))
        return 0
    except (OSError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

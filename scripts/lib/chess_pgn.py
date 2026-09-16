"""Load an artifact-locked external PGN rules provider without a global install.

This provider validates external benchmark games. It is not a native Laplace
chess implementation. Upstream uses absolute chess imports, so an unrelated
already-imported package is refused instead of silently reused or replaced.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import urllib.request
import importlib.util
from pathlib import Path, PurePosixPath
import shutil
import stat
import sys
import tarfile
import tempfile
import threading
from types import ModuleType

ROOT = Path(__file__).resolve().parents[2]
LOCK = ROOT / "deploy/chess-pgn-validator.json"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def default_cache() -> Path:
    return Path(os.environ.get("LAPLACE_CHESS_PGN_CACHE",
                str(Path(os.environ.get("LAPLACE_WORK_ROOT", "/build/laplace/work")) / "chess-pgn-validation")))


def artifact_lock() -> dict:
    return json.loads(LOCK.read_text(encoding="utf-8"))


def verify_artifact(path: Path, artifact: dict) -> None:
    require(_regular_file(path) and path.stat().st_size == artifact["size"],
            f"PGN provider artifact size or regular-file mismatch: {path}")
    require(hashlib.sha256(path.read_bytes()).hexdigest() == artifact["sha256"],
            f"PGN provider artifact SHA-256 mismatch: {path}")


def acquire(artifact: dict, cache: Path, offline: bool) -> Path:
    target = cache / artifact["filename"]
    if not target.exists():
        require(not offline, f"offline PGN provider artifact missing: {target}")
        descriptor, name = tempfile.mkstemp(prefix=".download-", dir=cache)
        part = Path(name)
        try:
            request = urllib.request.Request(artifact["url"], headers={"User-Agent": "Laplace-PGN-validator/1"})
            with os.fdopen(descriptor, "wb") as output, urllib.request.urlopen(request, timeout=60) as response:
                remaining = artifact["size"]
                while remaining:
                    block = response.read(min(remaining, 1024 * 1024))
                    require(bool(block), "PGN provider download is truncated")
                    output.write(block)
                    remaining -= len(block)
                require(not response.read(1), "PGN provider download exceeds its pinned size")
            verify_artifact(part, artifact)
            os.replace(part, target)
        finally:
            part.unlink(missing_ok=True)
    verify_artifact(target, artifact)
    return target


_MODULES = ("chess", "chess.svg", "chess.engine", "chess.variant", "chess.pgn",
            "chess.polyglot", "chess.syzygy", "chess.gaviota")
_loaded: dict[str, ModuleType] = {}
_lock = threading.RLock()


def _module_path(name: str) -> str:
    return "chess/__init__.py" if name == "chess" else name.replace(".", "/") + ".py"


def _regular_file(path: Path) -> bool:
    try:
        status = path.lstat()
    except FileNotFoundError:
        return False
    return stat.S_ISREG(status.st_mode) and status.st_nlink == 1


def _runtime_files(archive: Path, version: str) -> dict[str, bytes]:
    """Inspect every member; read only the official runtime and its license."""
    prefix = f"chess-{version}"
    files: dict[str, bytes] = {}
    seen: set[str] = set()
    with tarfile.open(archive, "r:gz") as source:
        for member in source:
            path = PurePosixPath(member.name)
            require(bool(path.parts) and not path.is_absolute() and ".." not in path.parts and
                          "\\" not in member.name and ":" not in member.name and
                          str(path) == member.name.rstrip("/") and
                          path.parts[0] == prefix, f"unsafe PGN provider archive path: {member.name}")
            require(str(path) not in seen, f"duplicate PGN provider archive member: {member.name}")
            seen.add(str(path))
            require(member.isdir() or member.isfile(),
                          f"PGN provider archive links or special files are forbidden: {member.name}")
            relative = PurePosixPath(*path.parts[1:])
            if member.isdir() or not (str(relative) == "LICENSE.txt" or relative.parts[:1] == ("chess",)):
                continue
            require(member.size <= 4 * 1024 * 1024, "PGN provider runtime member exceeds its finite envelope")
            content = source.extractfile(member)
            require(content is not None, "PGN provider runtime member is unreadable")
            body = content.read()
            require(len(body) == member.size, "PGN provider runtime member is truncated")
            files[str(relative)] = body
    required = {_module_path(name) for name in _MODULES} | {"chess/py.typed", "LICENSE.txt"}
    require(set(files) == required, "PGN provider runtime or license inventory differs from its supported package")
    return files


def _verify_runtime(root: Path, files: dict[str, bytes]) -> None:
    require(root.is_dir() and not root.is_symlink(), "PGN provider runtime must be a physical directory")
    observed = set()
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        require(not path.is_symlink(), f"PGN provider runtime link is forbidden: {relative}")
        if path.is_dir():
            require(relative == "chess", f"extra PGN provider runtime directory: {relative}")
            continue
        require(relative in files and _regular_file(path), f"extra or nonregular PGN provider runtime file: {relative}")
        require(path.read_bytes() == files[relative], f"PGN provider runtime bytes differ: {relative}")
        observed.add(relative)
    require(observed == set(files), "PGN provider runtime inventory is incomplete")


def _prepare_runtime(cache: Path, files: dict[str, bytes]) -> Path:
    root = cache / "runtime"
    if not root.exists() and not root.is_symlink():
        staged = Path(tempfile.mkdtemp(prefix=".runtime-", dir=cache))
        try:
            for relative, body in files.items():
                target = staged / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(body)
            _verify_runtime(staged, files)
            try:
                staged.rename(root)
            except FileExistsError:
                pass  # Another complete publication must pass the same validation.
        finally:
            if staged.exists():
                shutil.rmtree(staged)
    _verify_runtime(root, files)
    return root


def _verify_modules(root: Path, version: str) -> None:
    present = {name: value for name, value in sys.modules.items()
               if name == "chess" or name.startswith("chess.")}
    require(set(present) == set(_MODULES), "PGN provider imported module inventory differs")
    for name, module in present.items():
        origin = str(root / _module_path(name))
        require(module is _loaded.get(name), f"PGN provider module substitution: {name}")
        spec = getattr(module, "__spec__", None)
        require(getattr(module, "__file__", None) == origin and spec is not None and
                      spec.origin == origin, f"PGN provider module origin differs: {name}")
        if name != "chess":
            require(getattr(module, "chess", None) is present["chess"],
                          f"PGN provider absolute import substitution: {name}")
    require(getattr(present["chess"], "__version__", None) == version, "PGN provider version differs from its artifact lock")
    require(getattr(present["chess"], "__path__", None) == [str(root / "chess")], "PGN provider package search path differs")
    for name in _MODULES[1:]:
        require(getattr(present["chess"], name.split(".")[1], None) is present[name],
                      f"PGN provider package module substitution: {name}")


def load_provider(cache: Path, offline: bool = False) -> tuple[ModuleType, ModuleType, dict]:
    """Return the exact chess module, PGN module, and retained provenance receipt."""
    with _lock:
        artifact = artifact_lock()
        require(PurePosixPath(artifact["filename"]).name == artifact["filename"] and
                      "\\" not in artifact["filename"] and ":" not in artifact["filename"],
                      "unsafe PGN provider archive filename")
        cache = Path(cache).absolute()
        require(not cache.is_symlink(), "PGN provider cache must not be a symlink")
        cache.mkdir(parents=True, exist_ok=True)
        cache = cache.resolve(strict=True)
        target = cache / artifact["filename"]
        require(not target.is_symlink() and (not target.exists() or _regular_file(target)),
                      "PGN provider archive must be a physical regular file")
        archive = acquire(artifact, cache, offline)
        verify_artifact(archive, artifact)
        files = _runtime_files(archive, artifact["version"])
        root = _prepare_runtime(cache, files)
        present = {name for name in sys.modules if name == "chess" or name.startswith("chess.")}
        if not present:
            _loaded.clear()
            try:
                for name in _MODULES:
                    path = root / _module_path(name)
                    spec = importlib.util.spec_from_file_location(name, path)
                    require(spec is not None and spec.loader is not None, f"PGN provider module cannot load: {name}")
                    module = importlib.util.module_from_spec(spec)
                    sys.modules[name] = module
                    _loaded[name] = module
                    # Compile the verified archive bytes, never an ambient .pyc.
                    exec(compile(files[_module_path(name)], str(path), "exec"), module.__dict__)
                    if name != "chess":
                        setattr(_loaded["chess"], name.split(".")[1], module)
            except BaseException:
                for name, module in _loaded.items():
                    if sys.modules.get(name) is module:
                        del sys.modules[name]
                _loaded.clear()
                raise
        _verify_modules(root, artifact["version"])
        _verify_runtime(root, files)
        inventory = [{"path": relative, "size": len(body), "sha256": hashlib.sha256(body).hexdigest()}
                     for relative, body in sorted(files.items())]
        modules = {name: {"origin": str(root / _module_path(name)),
                          "sha256": hashlib.sha256(files[_module_path(name)]).hexdigest()} for name in _MODULES}
        receipt = {"schema": "laplace.chess-pgn-provider/v1", "scope": "external benchmark legal-move and game-outcome validation",
                   "artifact_key": "chess-pgn-validator", "version": artifact["version"],
                   "archive": {"path": str(archive), "url": artifact["url"], "size": artifact["size"], "sha256": artifact["sha256"]},
                   "runtime_root": str(root), "files": inventory, "modules": modules,
                   "retention": {"bytes": "verified external cache; not copied into benchmark output",
                                 "manifest": "this report retains the archive and every runtime file content hash"},
                   "license": {"path": str(root / "LICENSE.txt"), "sha256": hashlib.sha256(files["LICENSE.txt"]).hexdigest()},
                   "validation": "archive, every runtime file, module origin and version verified"}
        return _loaded["chess"], _loaded["chess.pgn"], receipt


def validate_games(text: str, expected: int, *, provider: tuple | None = None,
                   max_moves: int = 0) -> list[dict]:
    """Replay standard-start CuteChess mainlines, including their exact endings.

    A positive move cap identifies a diagnostic workload. Only its exact maximal
    game-length draw can replace a rules-derived ending. Draw claims use the
    complete move history; they do not make earlier claimable positions forced
    endpoints. This audits external games, not Laplace's chess implementation.
    """
    require(type(expected) is int and expected > 0, "PGN expected game count must be positive")
    require(type(max_moves) is int and max_moves >= 0, "PGN diagnostic move cap must be nonnegative")
    chess, pgn, _ = provider or load_provider(default_cache())
    blocks = [part for part in re.split(r'(?=^\[Event ")', text, flags=re.M) if part.strip()]
    require(len(blocks) == expected, f"expected {expected} PGN games, found {len(blocks)}")
    games = []
    tag_pattern = r'^\[(\w+) "((?:[^"\\\n]|\\.)*)"\][ \t]*$'
    for block in blocks:
        require(block.lstrip().startswith('[Event "'), "PGN has content outside a game")
        tag_rows = re.findall(tag_pattern, block, re.M)
        tags = dict(tag_rows)
        require(len(tags) == len(tag_rows), "PGN has duplicate header tags")
        # The upstream parser defaults to the standard board when FEN is absent,
        # even with SetUp=1, and accepts FEN independently of its SetUp header.
        require(tags.get("SetUp") != "1" or bool(tags.get("FEN", "").strip()),
                "PGN SetUp=1 requires a nonempty FEN")
        require("FEN" not in tags or tags.get("SetUp") == "1", "PGN FEN requires SetUp=1")
        result = tags.get("Result")
        require(result in {"1-0", "0-1", "1/2-1/2"}, "PGN includes an unscored or incomplete game")
        require(not re.search(r"stall|crash|disconnect|illegal move|time forfeit|loses on time", block, re.I),
                "PGN records a process/protocol failure")
        stream = io.StringIO(block)
        game = pgn.read_game(stream)
        require(game is not None and not game.errors,
                f"PGN contains illegal or invalid moves: {getattr(game, 'errors', None)}")
        require(pgn.read_game(stream) is None, "PGN block contains more than one game")
        board = game.board()
        require(type(board) is chess.Board and not board.chess960 and board.fen() == chess.STARTING_FEN,
                "benchmark PGN must start from the standard initial position")
        require(board.is_valid(), "PGN initial position is invalid")
        # The upstream PGN parser tolerates unknown text. Account for every SAN
        # token emitted by CuteChess and replay it through the same rules owner.
        movetext = re.sub(tag_pattern, " ", block, flags=re.M)
        movetext = re.sub(r"\{[^{}]*\}|;[^\n]*", " ", movetext)
        tokens = re.sub(r"(?<!\S)\d+\.(?:\.\.)?", " ", movetext).split()
        tokens = [token for token in tokens if not re.fullmatch(r"\$\d+|[!?]+", token)]
        require(bool(tokens) and tokens[-1] == result,
                "PGN movetext is truncated or disagrees with its Result tag")
        moves = list(game.mainline_moves())
        require(len(tokens) - 1 == len(moves), "PGN contains unaccounted movetext or result markers")
        for token, move in zip(tokens[:-1], moves):
            require(board.outcome(claim_draw=False) is None, "PGN continues after a terminal position")
            try:
                parsed = board.parse_san(re.sub(r"[!?]+$", "", token))
            except ValueError as error:
                raise ValueError(f"PGN contains invalid SAN: {token}") from error
            require(bool(move) and parsed == move and board.is_legal(move), "PGN contains an illegal or null move")
            board.push(move)
        plies = len(moves)
        require(tags.get("PlyCount", "").isdigit() and int(tags["PlyCount"]) == plies and plies > 0,
                "PGN PlyCount disagrees with serialized moves or is missing")
        termination = tags.get("Termination", "").lower()
        outcome = board.outcome(claim_draw=True)
        normal = termination in {"", "normal"} and not re.search(r"adjudicat|resign|maximal game length", block, re.I)
        if normal:
            require(outcome is not None and outcome.result() == result,
                    "PGN result lacks a matching legal terminal outcome")
        else:
            require(max_moves > 0 and termination == "adjudication"
                    and "Draw by adjudication: maximal game length" in block
                    and result == "1/2-1/2" and plies == 2 * max_moves,
                    "PGN non-normal termination is not the declared capped diagnostic")
            require(outcome is None or outcome.result() == result,
                    "PGN adjudication contradicts the legal terminal outcome")
        require(max_moves == 0 or plies <= 2 * max_moves, "PGN exceeds the declared diagnostic move cap")
        games.append({"white": tags.get("White"), "black": tags.get("Black"), "result": result,
                      "plies": plies, "termination": termination or "normal",
                      "normal_completion": normal, "legal_moves_validated": True,
                      "board_outcome": outcome.termination.name if outcome else None,
                      "final_fen": board.fen(), "draw_claims": True,
                      "moves_sha256": hashlib.sha256(" ".join(move.uci() for move in moves).encode()).hexdigest()})
    return games

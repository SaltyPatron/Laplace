#!/usr/bin/env python3
"""The conventional ladder Laplace is measured against: rungs, their calibration, and gauntlets, conducted by fastchess
and rated by Ordo (docs/guides/chess-lab.md, Ladder).

A rung is an engine at a fixed budget, written as a spec:

    sf:nodes=1024                 Stockfish, full strength, Threads=1, Hash=16, the installed Syzygy set
    lc0:nodes=64[:net=FILE][:backend=cuda-fp16]
                                  Lc0, Threads=1, a fixed minibatch, no smart pruning, temperature 0
    laplace[:nodes=N][:substrate=substrate|off]
                                  laplace-uci (LAPLACE_DB names its database)

Node-limited rungs do not depend on the speed of the core they run on, so they may run on any core; a time-controlled
match pins every engine to equal cores with --affinity (on HART-DESKTOP not the two P-cores capped below the rest).
Lc0 needs the GPU: on HART-DESKTOP that is chess-GPU mode (Laplace-Operations chess/chess-gpu.ps1 on), which this tool
checks and never switches.

    chess-ladder.py calibrate --rungs sf:nodes=256,sf:nodes=512,sf:nodes=1024 --games 40 --concurrency 12 --affinity 16-31
    chess-ladder.py gauntlet --seed laplace:nodes=2000 --rungs sf:nodes=16,sf:nodes=32 --games 20
    chess-ladder.py rate RUN_DIR [--anchor sf:nodes=1024 --anchor-elo 0]
    chess-ladder.py stockfish-receipt [--out FILE]

stockfish-receipt writes the installed Stockfish's identity (tag and commit, binary SHA-256, the compiler line, the
network and its SHA-256, the bench signature) and fails when the bench is not the generation's (deploy/stockfish-profiles.json).
calibrate plays each rung against the next (paired, colour-swapped, balanced book); gauntlet plays the seed against
every rung; rate runs Ordo over a run's PGNs with one rung fixed. Every run is a directory under --out holding the
games, fastchess's output and log, the Ordo table, and receipt.json: the tools and engines by SHA-256, Stockfish's
bench signature, Lc0's network and backend and the GPU driver, the exact argv, the book and seed, the host, and the
WHEA-Logger event 19 count before and after (Windows). A rating is a chess-engine rating on this ladder, not a
Glicko-2 standing and not a human rating.
"""
import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
LOCK = json.loads((ROOT / "deploy/chess-ladder-release.json").read_text())


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


DEPS = module("check-chess-dependencies")


def sha256(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            value.update(block)
    return value.hexdigest()


def config():
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", str(Path(os.environ.get("LAPLACE_TOOLS", "tools")) / "chess")
                                 if os.name == "nt" else "/opt/laplace"))
    return DEPS.configuration(prefix)


class Rung:
    def __init__(self, spec, cfg):
        self.spec = spec
        kind, *parts = spec.split(":")
        fields = dict(part.split("=", 1) for part in parts)
        self.kind = kind
        self.nodes = int(fields["nodes"]) if "nodes" in fields else None
        self.options = {}
        suffix = ".exe" if os.name == "nt" else ""
        if kind == "sf":
            self.cmd = cfg.get("LAPLACE_STOCKFISH") or shutil.which("stockfish")
            self.options = {"Threads": "1", "Hash": "16"}
            syzygy = cfg.get("LAPLACE_SYZYGY")
            if syzygy and fields.get("syzygy", "on") != "off":
                self.options["SyzygyPath"] = syzygy
            self.name = f"SF19-n{self.nodes}"
        elif kind == "lc0":
            self.cmd = cfg.get("LAPLACE_LC0")
            net = Path(fields.get("net") or cfg.get("LAPLACE_LC0_NET") or "")
            if not net.is_file():
                candidates = [p for p in Path(cfg.get("LAPLACE_LC0_NET", ".")).parent.glob("*.pb.gz") if fields.get("net", "") in p.name]
                net = candidates[0] if len(candidates) == 1 else net
            self.net = net
            self.backend = fields.get("backend") or cfg.get("LAPLACE_LC0_BACKEND") or "cuda-fp16"
            small = self.nodes is not None and self.nodes <= 64
            self.options = {"WeightsFile": str(net), "Backend": self.backend, "Threads": "1",
                            "MinibatchSize": "1" if small else "32", "SmartPruningFactor": "0",
                            "Temperature": "0", "NNCacheSize": "200000"}
            tag = re.sub(r"[^A-Za-z0-9]+", "", net.name.split("-")[0])[:6] or "net"
            self.name = f"Lc0-{tag}-n{self.nodes}"
        elif kind == "laplace":
            self.cmd = cfg.get("LAPLACE_UCI") or str(Path(os.environ.get("LAPLACE_TOOLS", "tools")) / "chess" / "app" / ("laplace-uci" + suffix))
            self.options = {"Substrate": fields.get("substrate", "substrate")}
            self.name = "Laplace" + (f"-n{self.nodes}" if self.nodes else "") + ("-off" if self.options["Substrate"] == "off" else "")
        else:
            raise ValueError(f"unknown rung kind in '{spec}' (sf, lc0, laplace)")
        if not self.cmd or not Path(self.cmd).is_file():
            raise ValueError(f"{spec}: no engine binary ({self.cmd}); see chess-lab.env")

    def engine_args(self, tc):
        args = ["-engine", f"name={self.name}", f"cmd={self.cmd}", f"dir={Path(self.cmd).parent}", "proto=uci"]
        args += [f"option.{k}={v}" for k, v in self.options.items()]
        if self.nodes:
            args += ["tc=inf", f"nodes={self.nodes}"] if tc is None else [f"nodes={self.nodes}"]
        elif tc is None:
            raise ValueError(f"{self.spec}: a rung needs nodes=N or the match a --tc")
        return args

    def identity(self):
        result = {"spec": self.spec, "name": self.name, "cmd": self.cmd, "sha256": sha256(self.cmd), "options": self.options}
        if self.kind == "lc0":
            result["network"] = {"file": str(self.net), "sha256": sha256(self.net)}
            result["backend"] = self.backend
        return result


def gpu_mode():
    try:
        line = subprocess.run(["nvidia-smi", "--query-gpu=name,driver_version,memory.free,memory.used", "--format=csv,noheader"],
                              capture_output=True, text=True, timeout=30).stdout.strip()
    except OSError:
        return None, None
    held = None
    if os.name == "nt":
        held = [s for s in ("llama-embedding", "llama-embedding-text", "llama-reranker")
                if "STOPPED" not in subprocess.run(["sc.exe", "query", s], capture_output=True, text=True).stdout]
    return line, held


def whea19():
    if os.name != "nt":
        return None
    reply = subprocess.run(["pwsh", "-NoProfile", "-Command",
                            "@(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'; Id=19} -ErrorAction SilentlyContinue).Count"],
                           capture_output=True, text=True, timeout=120)
    return int(reply.stdout.strip() or 0)


def stockfish_bench(cmd):
    reply = subprocess.run([cmd, "bench"], capture_output=True, text=True, timeout=300, stdin=subprocess.DEVNULL)
    text = reply.stdout + reply.stderr
    nodes = re.search(r"Nodes searched\s*:\s*(\d+)", text)
    compiler = subprocess.run([cmd, "compiler"], capture_output=True, text=True, timeout=60, stdin=subprocess.DEVNULL)
    return {"bench_nodes": int(nodes.group(1)) if nodes else None, "compiler": compiler.stdout.strip()}


def stockfish_receipt(cmd):
    profiles = json.loads((ROOT / "deploy/stockfish-profiles.json").read_text())["identity"]
    lock = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())
    measured = stockfish_bench(cmd)
    network = profiles["network"]
    net_file = next((p for p in (Path(cmd).parent / network, Path(cmd).parent.parent / "src" / network) if p.is_file()), None)
    record = {"tag": lock["tag"], "commit": lock["commit"], "binary": str(Path(cmd).resolve()), "binary_sha256": sha256(cmd),
              "compiler": measured["compiler"], "network": network,
              "network_sha256": sha256(net_file) if net_file else None,
              "network_note": "embedded in the binary; the file's SHA-256 begins with the name's 12 hex digits" if net_file else "embedded in the binary; no copy beside it",
              "bench_nodes": measured["bench_nodes"], "bench_expected": profiles["bench_nodes"],
              "host": platform.node(), "measured_at": datetime.datetime.now(datetime.timezone.utc).isoformat()}
    if net_file and not record["network_sha256"].startswith(network.split("-")[1].split(".")[0]):
        raise ValueError(f"{net_file} does not hash to its name")
    if measured["bench_nodes"] != profiles["bench_nodes"]:
        raise ValueError(f"bench is {measured['bench_nodes']}, {lock['tag']}'s is {profiles['bench_nodes']}: a different engine")
    return record


def book_args(cfg, book, seed, plies):
    root = Path(cfg.get("LAPLACE_CHESS_BOOKS") or Path(cfg.get("LAPLACE_DATA_ROOT", "D:/Data/Ingest" if os.name == "nt" else "/vault/Data")) / LOCK["books"]["directory"])
    path = root / book
    if not path.is_file():
        raise ValueError(f"opening suite {path} is missing (LAPLACE_CHESS_BOOKS)")
    fmt = "pgn" if path.suffix == ".pgn" else "epd"
    args = ["-openings", f"file={path}", f"format={fmt}", "order=random"] + ([f"plies={plies}"] if fmt == "pgn" else [])
    return args + ["-srand", str(seed)], {"file": str(path), "sha256": LOCK["books"]["files"].get(path.name), "order": "random",
                                         "plies": plies if fmt == "pgn" else None, "seed": seed, "commit": LOCK["books"]["commit"]}


def adjudication(cfg):
    args = ["-maxmoves", "200"]
    syzygy = cfg.get("LAPLACE_SYZYGY")
    if syzygy:
        args += ["-tb", syzygy, "-tbpieces", "5", "-tbadjudicate", "BOTH"]
    return args


def fastchess_run(cfg, out, label, rungs, args, games, concurrency, affinity, tc, tournament=None):
    fastchess = cfg.get("LAPLACE_FASTCHESS") or shutil.which("fastchess")
    if not fastchess:
        raise ValueError("LAPLACE_FASTCHESS is not set")
    pgn = out / f"{label}.pgn"
    argv = [fastchess]
    for rung in rungs:
        argv += rung.engine_args(tc)
    argv += ["-each"] + ([f"tc={tc}"] if tc else []) + ["timemargin=10000"]
    argv += args
    argv += ["-games", "2", "-rounds", str(max(1, games // 2)), "-concurrency", str(concurrency), "-recover",
             "-report", "penta=true", "-ratinginterval", "0", "-autosaveinterval", "0",
             "-pgnout", f"file={pgn}", "nodes=true", "nps=true", "tbhits=true",
             "-log", f"file={out / (label + '.log')}", "level=warn", "-event", f"laplace-ladder/{out.name}/{label}"]
    if tournament:
        argv += ["-tournament", tournament]
    if affinity:
        argv += ["-use-affinity", affinity]
    started = datetime.datetime.now(datetime.timezone.utc)
    with open(out / f"{label}.out", "w", encoding="utf-8") as transcript:
        code = subprocess.run(argv, stdout=transcript, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, cwd=out).returncode
    text = (out / f"{label}.out").read_text(encoding="utf-8", errors="replace")
    return {"label": label, "argv": argv, "exit": code, "pgn": str(pgn),
            "started": started.isoformat(), "finished": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "summary": [line for line in text.splitlines() if re.match(r"\s*(Results of|Elo:|LOS:|Games:|Ptnml|Score of|Elo difference)", line)][-8:]}


def ordo(cfg, out, anchor, anchor_elo, simulations=1000, ladder=None):
    binary = cfg.get("LAPLACE_ORDO") or shutil.which("ordo")
    if not binary:
        raise ValueError("LAPLACE_ORDO is not set")
    # a gauntlet is rated together with the calibration run that fixed its rungs, so the database is connected
    pgns = sorted(p for p in out.glob("*.pgn") if p.name != "all.pgn")
    if ladder:
        pgns += sorted(p for p in Path(ladder).glob("*.pgn") if p.name != "all.pgn")
    combined = out / "all.pgn"
    with open(combined, "w", encoding="utf-8") as sink:
        for p in pgns:
            sink.write(p.read_text(encoding="utf-8", errors="replace").rstrip() + "\n\n")
    argv = [binary, "-p", str(combined), "-a", str(anchor_elo), "-A", anchor, "-W", "-D", "-s", str(simulations),
            "-o", str(out / "ratings.txt"), "-c", str(out / "ratings.csv")]
    reply = subprocess.run(argv, capture_output=True, text=True, stdin=subprocess.DEVNULL)
    if reply.returncode:
        # an unconnected database (an all-loss gauntlet, say) is a result to report, not a crash
        return {"argv": argv, "error": f"ordo exited {reply.returncode}", "table": reply.stdout + reply.stderr}
    return {"argv": argv, "table": (out / "ratings.txt").read_text(encoding="utf-8", errors="replace")}


def receipt(cfg, out, kind, rungs, extra):
    sf = [r for r in rungs if r.kind == "sf"]
    gpu, held = gpu_mode() if any(r.kind == "lc0" for r in rungs) else (None, None)
    fastchess = cfg.get("LAPLACE_FASTCHESS")
    return {"format": 1, "kind": kind, "run": out.name, "host": platform.node(), "platform": platform.platform(),
            "processor": platform.processor(), "conductor": {"path": fastchess, "sha256": sha256(fastchess),
                                                             "lock": LOCK["fastchess"]["version"], "commit": LOCK["fastchess"]["commit"]},
            "stockfish": stockfish_receipt(sf[0].cmd) if sf else None,
            "gpu": gpu, "rungs": [r.identity() for r in rungs], **extra}


def guard_gpu(rungs):
    if not any(r.kind == "lc0" for r in rungs):
        return
    gpu, held = gpu_mode()
    if gpu is None:
        raise ValueError("Lc0 rungs need an NVIDIA GPU (nvidia-smi not found)")
    if held:
        raise ValueError(f"Lc0 rungs need chess-GPU mode: {', '.join(held)} still hold the card (Laplace-Operations chess/chess-gpu.ps1 on)")


def new_run(base, kind):
    out = Path(base) / f"{datetime.datetime.now(datetime.timezone.utc):%Y%m%dT%H%M%SZ}-{kind}"
    out.mkdir(parents=True, exist_ok=False)
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("calibrate", "gauntlet"):
        p = sub.add_parser(name)
        p.add_argument("--rungs", required=True, help="comma-separated rung specs")
        if name == "gauntlet":
            p.add_argument("--seed", required=True, help="the rung spec under test (laplace:nodes=N)")
        p.add_argument("--games", type=int, default=40, help="games per pairing (even; colour-swapped pairs)")
        p.add_argument("--concurrency", type=int, default=4)
        p.add_argument("--affinity", help="fastchess -use-affinity CPU list")
        p.add_argument("--tc", help="time control (fastchess tc=); omit for node-limited rungs")
        p.add_argument("--book", default="8moves_v3.pgn")
        p.add_argument("--plies", type=int, default=16)
        p.add_argument("--srand", type=int, default=20261006)
        p.add_argument("--anchor", help="rung name Ordo fixes (default: the first rung)")
        p.add_argument("--ladder", help="a calibration run whose games are rated with this run's (gauntlet)")
        p.add_argument("--anchor-elo", type=float, default=0.0)
        p.add_argument("--out", default=os.environ.get("LAPLACE_CHESS_LADDER_DIR",
                                                       "D:/Data/Laplace/work/chess/ladder" if os.name == "nt" else "/vault/work/chess/ladder"))
    p = sub.add_parser("stockfish-receipt")
    p.add_argument("--out", help="write the receipt here as well as printing it")
    p = sub.add_parser("rate")
    p.add_argument("run")
    p.add_argument("--ladder", help="a calibration run whose games are rated with this run's")
    p.add_argument("--anchor", required=True)
    p.add_argument("--anchor-elo", type=float, default=0.0)
    args = parser.parse_args()
    cfg = config()

    if args.command == "stockfish-receipt":
        record = stockfish_receipt(cfg.get("LAPLACE_STOCKFISH") or shutil.which("stockfish"))
        text = json.dumps(record, indent=2)
        if args.out:
            Path(args.out).write_text(text + "\n", encoding="utf-8")
        print(text)
        return 0

    if args.command == "rate":
        print(ordo(cfg, Path(args.run), args.anchor, args.anchor_elo, ladder=args.ladder)["table"])
        return 0

    if args.games < 2 or args.games % 2:
        raise SystemExit("--games must be even (colour-swapped pairs)")
    rungs = [Rung(spec, cfg) for spec in args.rungs.split(",")]
    seed = Rung(args.seed, cfg) if args.command == "gauntlet" else None
    every = ([seed] if seed else []) + rungs
    guard_gpu(every)
    out = new_run(args.out, args.command)
    book, book_receipt = book_args(cfg, args.book, args.srand, args.plies)
    common = book + adjudication(cfg)
    whea_before = whea19()
    matches = []
    if args.command == "calibrate":
        for low, high in zip(rungs, rungs[1:]):
            matches.append(fastchess_run(cfg, out, f"{low.name}_vs_{high.name}", [low, high], common, args.games,
                                         args.concurrency, args.affinity, args.tc))
            print(f"{low.name} vs {high.name}: " + " | ".join(matches[-1]["summary"][-3:]), flush=True)
    else:
        matches.append(fastchess_run(cfg, out, f"{seed.name}_gauntlet", every, common, args.games,
                                     args.concurrency, args.affinity, args.tc, tournament="gauntlet"))
        print(" | ".join(matches[-1]["summary"]), flush=True)
    anchor = args.anchor or rungs[0].name
    rating = ordo(cfg, out, anchor, args.anchor_elo, ladder=args.ladder)
    record = receipt(cfg, out, args.command, every, {
        "book": book_receipt, "adjudication": common[len(book):], "games_per_pairing": args.games,
        "concurrency": args.concurrency, "affinity": args.affinity, "tc": args.tc, "matches": matches,
        "ordo": {"anchor": anchor, "anchor_elo": args.anchor_elo, "argv": rating["argv"], "ladder": args.ladder,
                 "error": rating.get("error")},
        "whea19": {"before": whea_before, "after": whea19()},
        "statement": "chess-engine ratings on this ladder, anchored as stated; not a Glicko-2 standing, not a human rating"})
    (out / "receipt.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    print(rating["table"])
    print(f"run: {out}")
    failed = [m["label"] for m in matches if m["exit"]]
    if record["whea19"]["before"] is not None and record["whea19"]["after"] != record["whea19"]["before"]:
        print(f"WARNING: WHEA-Logger event 19 count went from {record['whea19']['before']} to {record['whea19']['after']}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""One bounded, non-recording real UCI match using candidate engine binaries."""
import argparse
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("cutechess")
    parser.add_argument("uci")
    parser.add_argument("stockfish")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="laplace-cutechess-check-") as temporary:
        pgn = Path(temporary) / "game.pgn"
        env = dict(os.environ, LAPLACE_UCI_SUBSTRATE="off", LAPLACE_OPS_LOG_DIR=temporary)
        command = [args.cutechess,
                   "-engine", "name=Laplace", "cmd=" + args.uci, "proto=uci",
                   "-engine", "name=Stockfish", "cmd=" + args.stockfish, "proto=uci",
                   "option.UCI_LimitStrength=false", "-each", "tc=inf", "depth=1",
                   "-rounds", "1", "-maxmoves", "4", "-pgnout", str(pgn), "-debug", "all"]
        process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, start_new_session=True)
        try:
            transcript, _ = process.communicate(timeout=45)
        finally:
            # Kill only this test's session, including engines if cutechess failed.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)
        if process.returncode != 0:
            raise AssertionError("cutechess failed:\n" + transcript[-6000:])
        record = pgn.read_text()
        if any(tag not in record for tag in ('[Result "1/2-1/2"]', '[PlyCount "8"]', '[Termination "adjudication"]')):
            raise AssertionError("expected bounded eight-ply match, got:\n" + record)
        moves = re.findall(r"<.*?: bestmove ([a-h][1-8][a-h][1-8][qrbn]?)", transcript)
        if len(moves) != 8 or "Finished game 1" not in transcript:
            raise AssertionError("expected eight played plies and one finished game:\n" + transcript[-6000:])
        print("PASS: real cutechess / candidate Laplace / Stockfish match completed; 8 plies, recorded PGN, no substrate writes")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Real-provider chess recording acceptance.

Starts the installed Chess Lab provider import, waits for its exact persisted
readback receipt, then independently reads one playable game back through the
public chess API. No fixture PGN and no fake substrate client are used here.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

MAX_RESPONSE = 8 << 20
TERMINAL = {"completed", "failed", "cancelled"}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


class Client:
    def __init__(self, base: str, timeout: float):
        parsed = urllib.parse.urlsplit(base)
        require(parsed.scheme in {"http", "https"} and bool(parsed.hostname),
                "API base must be HTTP(S)")
        self.base = base.rstrip("/")
        self.timeout = timeout
        self.headers = {
            "Content-Type": "application/json",
            "X-Laplace-Tenant": os.environ.get("LAPLACE_PROOF_TENANT", "ci"),
        }
        key = os.environ.get("LAPLACE_API_KEY")
        if key:
            self.headers["Authorization"] = "Bearer " + key
        quote = os.environ.get("LAPLACE_QUOTE_ID")
        if quote:
            self.headers["X-Laplace-Quote-Id"] = quote

    def request(self, path: str, body: object | None = None, raw: bool = False):
        request = urllib.request.Request(
            self.base + path,
            headers=self.headers,
            data=None if body is None else json.dumps(body).encode("utf-8"),
            method="GET" if body is None else "POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read(MAX_RESPONSE + 1)
        except urllib.error.HTTPError as error:
            raise ValueError(f"API {path} returned HTTP {error.code}") from None
        require(len(payload) <= MAX_RESPONSE, f"API {path} exceeded response envelope")
        return payload if raw else json.loads(payload)


def wait_for_job(client: Client, job_id: str, deadline: float, poll: float) -> dict:
    while time.monotonic() < deadline:
        job = client.request(f"/chess/lab/jobs/{urllib.parse.quote(job_id)}")
        state = str(job.get("state", "")).lower()
        if state in TERMINAL:
            return job
        time.sleep(poll)
    raise TimeoutError(f"provider job {job_id} exceeded acceptance deadline")


def find_player(client: Client, user: str) -> dict:
    query = urllib.parse.urlencode({"search": user, "limit": 10, "offset": 0})
    response = client.request("/v1/chess/players?" + query)
    players = response.get("players")
    require(isinstance(players, list) and players,
            "provider player is absent from chess read surface")
    exact = next((p for p in players
                  if str(p.get("name", "")).casefold() == user.casefold()), None)
    return exact or players[0]


def latest_readback(client: Client, player_id: str) -> dict:
    page = client.request(
        f"/v1/chess/players/{urllib.parse.quote(player_id)}/games?limit=1&offset=0")
    games = page.get("games")
    require(isinstance(games, list) and len(games) == 1,
            "provider player has no latest readable game")
    game_id = str(games[0].get("id", ""))
    require(re.fullmatch(r"[0-9a-fA-F]{32}", game_id) is not None,
            "latest provider game id is not canonical Hash128")
    detail = client.request(f"/v1/chess/games/{game_id}")
    plies = client.request(f"/v1/chess/games/{game_id}/plies")
    rows = plies.get("plies")
    require(isinstance(rows, list), "latest provider game replay is malformed")
    termination = str(detail.get("termination") or "")
    terminal_finish = "checkmate" in termination.casefold() or "stalemate" in termination.casefold()
    if terminal_finish:
        require(rows, "source-declared board-terminal latest game has no persisted move trajectory")
        require(plies.get("truncated") is None,
                f"latest board-terminal game replay truncated: {plies.get('truncated')}")
    return {
        "gameId": game_id.lower(),
        "playedOn": detail.get("played_on"),
        "white": detail.get("white"),
        "black": detail.get("black"),
        "result": detail.get("result"),
        "termination": detail.get("termination"),
        "plies": len(rows),
        "terminalFinishRequiresMoves": terminal_finish,
    }


def playable_readback(client: Client, player_id: str) -> dict:
    page = client.request(
        f"/v1/chess/players/{urllib.parse.quote(player_id)}/games?limit=50&offset=0")
    games = page.get("games")
    require(isinstance(games, list) and games,
            "recorded provider player has no readable games")

    for game in games:
        game_id = str(game.get("id", ""))
        if not re.fullmatch(r"[0-9a-fA-F]{32}", game_id):
            continue
        plies = client.request(f"/v1/chess/games/{game_id}/plies")
        rows = plies.get("plies")
        if not isinstance(rows, list) or not rows:
            continue
        require(plies.get("truncated") is None,
                f"game {game_id} replay truncated: {plies.get('truncated')}")
        for row in rows:
            require(isinstance(row.get("uci"), str) and row["uci"],
                    "replayed ply lacks UCI")
            require(isinstance(row.get("fen"), str) and row["fen"],
                    "replayed ply lacks FEN")
            require(re.fullmatch(r"[0-9a-fA-F]{32}",
                                 str(row.get("position_id", ""))) is not None,
                    "replayed ply lacks canonical position id")
        detail = client.request(f"/v1/chess/games/{game_id}")
        movetext = detail.get("movetext")
        require(isinstance(movetext, str) and movetext.strip(),
                "playable game has no reconstructed movetext")
        return {
            "gameId": game_id.lower(),
            "plies": len(rows),
            "firstUci": rows[0]["uci"],
            "lastUci": rows[-1]["uci"],
            "result": detail.get("result"),
            "termination": detail.get("termination"),
            "playedOn": detail.get("played_on"),
        }
    raise ValueError("no non-empty typed move trajectory was readable for provider player")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--api-base",
        default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:5187"))
    parser.add_argument(
        "--user",
        default=os.environ.get("LAPLACE_CHESS_PROVIDER_PROOF_USER", "Anthony-Hart"))
    parser.add_argument(
        "--site", choices=("chesscom", "lichess"),
        default=os.environ.get("LAPLACE_CHESS_PROVIDER_PROOF_SITE", "chesscom"))
    parser.add_argument(
        "--games", type=int,
        default=int(os.environ.get("LAPLACE_CHESS_PROVIDER_PROOF_GAMES", "0")))
    parser.add_argument("--timeout", type=float, default=900.0)
    parser.add_argument("--poll", type=float, default=1.0)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    require(0 <= args.games <= 1000,
            "provider proof game count must be 0 (all) or in 1..1000")
    require(args.user.strip() == args.user and bool(args.user),
            "provider proof username is invalid")

    client = Client(args.api_base, min(60.0, args.timeout))
    started = time.monotonic()
    request = {
        "kind": "lichess-fetch",
        "config": {
            "user": args.user,
            "site": args.site,
            "all": "true" if args.games == 0 else "false",
            "max": str(max(1, args.games)),
            "ingest": "true",
            "persistPgn": "true",
        },
    }
    created = client.request("/chess/lab/start", request)
    job_id = str(created.get("jobId", ""))
    require(re.fullmatch(r"[A-Za-z0-9_-]+", job_id) is not None,
            "provider import did not return a safe job id")

    job = wait_for_job(client, job_id, started + args.timeout, args.poll)
    require(str(job.get("state", "")).lower() == "completed",
            f"provider import ended {job.get('state')}: {job.get('summary')}")

    proof_bytes = client.request(
        f"/chess/lab/jobs/{urllib.parse.quote(job_id)}/artifact/recording-proof.json",
        raw=True)
    proof = json.loads(proof_bytes)
    require(proof.get("schema") == "laplace.chess-provider-recording/v1",
            "provider recording proof has wrong schema")
    require(proof.get("provider") == args.site,
            "provider recording proof names wrong provider")
    require(str(proof.get("providerUser", "")).casefold() == args.user.casefold(),
            "provider recording proof names wrong user")

    fetched = proof.get("fetchedGames")
    verified = proof.get("persistedVerifiedGames")
    verified_plies = proof.get("persistedVerifiedPlies")
    require(isinstance(fetched, int) and fetched > 0,
            "provider recording proof contains no games")
    if args.games > 0:
        require(fetched == args.games,
                f"provider returned {fetched} games, expected {args.games}")
    require(proof.get("parsedGames") == fetched,
            "provider source parse count differs from fetched count")
    require(verified == fetched and proof.get("exactPersistedReadback") is True,
            "not every fetched provider game passed exact persisted readback")
    require(isinstance(verified_plies, int) and verified_plies > 0,
            "provider proof contains no persisted move plies")
    require(isinstance(proof.get("sourcePgnBytes"), int)
            and proof["sourcePgnBytes"] > 0,
            "provider proof did not bind source PGN bytes")
    require(re.fullmatch(r"[0-9a-f]{64}",
                         str(proof.get("sourcePgnSha256", ""))) is not None,
            "provider proof did not bind source PGN SHA256")

    player = find_player(client, args.user)
    player_id = str(player.get("id", ""))
    require(re.fullmatch(r"[0-9a-fA-F]{32}", player_id) is not None,
            "provider player id is not canonical Hash128")

    profile = client.request(f"/v1/chess/players/{player_id}")
    provider_profiles = profile.get("profiles")
    require(isinstance(provider_profiles, list),
            "player profile readback is malformed")
    require(any(
        str(item.get("provider", "")).casefold() == args.site.casefold()
        and str(item.get("provider_id", "")).casefold() == args.user.casefold()
        for item in provider_profiles),
        "provider identity profile was not persisted on the player")

    latest = latest_readback(client, player_id)
    sample = playable_readback(client, player_id)
    receipt = {
        "schema": "laplace.chess-provider-live-acceptance/v1",
        "status": "passed",
        "apiBase": args.api_base,
        "jobId": job_id,
        "provider": args.site,
        "providerUser": args.user,
        "elapsedSeconds": round(time.monotonic() - started, 3),
        "recordingProof": proof,
        "player": {
            "id": player_id.lower(),
            "name": player.get("name"),
            "games": player.get("games"),
        },
        "latestReadback": latest,
        "playableReadback": sample,
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".pending")
    temporary.write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.replace(output)
    print(json.dumps(receipt, sort_keys=True))

    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write("\n## Real chess provider recording proof\n\n")
            stream.write(
                f"- Provider/user: {args.site} / {args.user}\n")
            stream.write(
                f"- Fetched and exact-readback verified: "
                f"{verified} games / {verified_plies} plies\n")
            stream.write(
                f"- New games: {proof.get('newlyAppliedGames')} · "
                f"repaired: {proof.get('repairedGames')}\n")
            stream.write(
                f"- Source PGN: {proof.get('sourcePgnBytes')} bytes · "
                f"SHA256 {proof.get('sourcePgnSha256')}\n")
            stream.write(
                f"- Latest readback: {latest['gameId']} · {latest['playedOn']} · "
                f"{latest['termination']} · {latest['plies']} plies\n")
            stream.write(
                f"- Independent playable readback: {sample['gameId']} · "
                f"{sample['plies']} plies\n")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"chess provider live acceptance failed: {error}", file=sys.stderr)
        raise

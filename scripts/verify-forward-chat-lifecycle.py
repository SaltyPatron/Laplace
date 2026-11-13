#!/usr/bin/env python3
"""Produce an executable receipt for a complete multi-turn Laplace chat lifecycle.

This is a proof harness, not a substitute chat implementation. It drives the ordinary
OpenAI-compatible endpoint twice in one session, and then reads the Native substrate
(public.entity, public.attestation) for what the Engine's forward program recorded.

Every chat turn is the Engine's (`laplace turn`): it admits the prompt as content, reads the
session's earlier turns as discourse, answers, and witnesses the turn: the participant's claim
[session, turn] at the turn's ordinal under UserPromptContent, and Laplace's claim
[response, prompt] under ResponseContent. The session is [participant, session key].

The receipt proves:
  * the client does not resend transcript history;
  * each turn's prompt, response and turn [prompt, response] are recorded as content;
  * the participant's [session, turn] claim is attested at the turn's ordinal, in the database;
  * the second turn read the first from the record (one turn before it, at ordinal two); and
  * the response returned over HTTP is the response the Engine witnessed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

MAX_BYTES = 16 * 1024 * 1024
MODEL = "laplace-converse-001"
HEX = re.compile(r"^[0-9a-f]{32}$")


def require(ok: bool, message: str) -> None:
    if not ok:
        raise RuntimeError(message)


def literal(text: str) -> str:
    return "'" + text.replace("'", "''") + "'"


def psql(database: str, sql: str, timeout: int) -> object:
    env = dict(os.environ)
    env["PGOPTIONS"] = f"-c statement_timeout={timeout * 1000}"
    proc = subprocess.run(
        ["psql", "-X", "-qAt", "-v", "ON_ERROR_STOP=1", "-d", database, "-c", sql],
        text=True, capture_output=True, timeout=timeout + 5, env=env)
    if proc.returncode:
        raise RuntimeError(proc.stderr.strip() or "psql failed")
    raw = proc.stdout.strip()
    return json.loads(raw) if raw else None


def recorded(database: str, proof: dict, timeout: int) -> dict:
    """What the database holds of one turn, read through the Native functions: the content the turn
    recorded, and the participant's [session, turn] claim with its place and trust."""
    ids = [proof["prompt_id"], proof["response_id"], proof["turn_id"]]
    sql = f"""
WITH handle AS (
  SELECT laplace_compose(ARRAY[laplace_text_id({literal(proof["participant"])}),
                               laplace_text_id({literal(proof["session"])})]) AS id
), claim AS (
  SELECT laplace_compose(ARRAY[handle.id, '{proof["turn_id"]}'::blake3]) AS id FROM handle
)
SELECT json_build_object(
 'entities',(SELECT count(*) FROM entity WHERE id = ANY(ARRAY[{",".join(f"'{x}'::blake3" for x in ids)}])),
 'claim',(SELECT id::text FROM claim),
 'attestations',COALESCE((SELECT json_agg(json_build_object('position',a."position",'trust',a.trust,'witness',a.witness::text)
                           ORDER BY a.witness) FROM attestation a, claim WHERE a.claim = claim.id),'[]'::json));
"""
    result = psql(database, sql, timeout)
    require(isinstance(result, dict), "the turn's record is not an object")
    return result


def http_turn(base: str, tenant: str, session: str, prompt: str, timeout: int) -> dict:
    body = {"model": MODEL, "messages": [{"role": "user", "content": prompt}],
            "session": session, "stream": False}
    headers = {"Content-Type": "application/json", "X-Laplace-Tenant": tenant}
    if os.environ.get("LAPLACE_API_KEY"):
        headers["Authorization"] = "Bearer " + os.environ["LAPLACE_API_KEY"]
    if os.environ.get("LAPLACE_QUOTE_ID"):
        headers["X-Laplace-Quote-Id"] = os.environ["LAPLACE_QUOTE_ID"]
    req = urllib.request.Request(base.rstrip("/") + "/v1/chat/completions",
                                 data=json.dumps(body).encode(), headers=headers, method="POST")
    try:
        response = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(MAX_BYTES + 1)
        require(len(raw) <= MAX_BYTES, "HTTP response exceeds proof envelope")
        payload = json.loads(raw)
        return {"status": response.code,
                "session_header": response.headers.get("X-Laplace-Session"),
                "request": body, "response": payload}


def validate_http(turn: dict, session: str) -> dict:
    error = turn["response"].get("error")
    code = error.get("code") if isinstance(error, dict) else None
    suffix = f" ({code})" if isinstance(code, str) and re.fullmatch(r"[a-z_]{1,80}", code) else ""
    require(turn["status"] == 200, f"ordinary chat returned HTTP {turn['status']}{suffix}")
    require(turn["session_header"] == session, "ordinary response changed session key")
    body = turn["response"]
    require(body.get("object") == "chat.completion" and body.get("model") == MODEL,
            "ordinary endpoint returned another contract")
    require(body.get("metadata", {}).get("session") == session, "metadata lost session key")
    proof = body.get("metadata", {}).get("laplace", {}).get("forward_proof")
    require(isinstance(proof, dict), "the response carries no receipt of the turn that produced it")
    require(proof.get("session") == session, "the receipt names another session")
    content = body["choices"][0]["message"]["content"]
    require(content, f"the forward program emitted nothing ({proof.get('disposition')}): there is no turn to witness")
    require(proof.get("witnessed") is True, f"the Engine did not witness the turn: {proof.get('reason')}")
    for key in ("prompt_id", "response_id", "turn_id"):
        require(HEX.match(proof.get(key) or ""), f"the receipt's {key} is not an ID")
    require(content == "".join(step["text"] for step in proof["steps"]),
            "the response is not what the receipt's steps emitted")
    return proof


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    ap.add_argument("--base", default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:8080"))
    ap.add_argument("--tenant", default=os.environ.get("LAPLACE_PROOF_TENANT", "ci"))
    ap.add_argument("--session", default="forward-life-" + uuid.uuid4().hex)
    ap.add_argument("--first", default="what is a dog?")
    ap.add_argument("--second", default="what about its senses?")
    ap.add_argument("--timeout-seconds", type=int, default=600)
    ap.add_argument("--receipt", type=Path, required=True)
    args = ap.parse_args()
    report = {"schema": "laplace.forward-chat-lifecycle-proof/v2", "disposition": "failed",
              "tenant": args.tenant, "session": args.session,
              "prompts": [args.first, args.second], "model": MODEL}
    started = time.monotonic_ns()
    try:
        # The ordinary endpoint is the writer. No proof-only insert or alternate chat path.
        first = http_turn(args.base, args.tenant, args.session, args.first, args.timeout_seconds)
        report["http_turns"] = [first]
        first_proof = validate_http(first, args.session)
        first_record = recorded(args.database, first_proof, args.timeout_seconds)
        report["first_record"] = first_record

        second = http_turn(args.base, args.tenant, args.session, args.second, args.timeout_seconds)
        report["http_turns"].append(second)
        second_proof = validate_http(second, args.session)
        second_record = recorded(args.database, second_proof, args.timeout_seconds)
        report["second_record"] = second_record

        require(first_proof["participant"] == second_proof["participant"],
                "the two turns of one session were witnessed by different participants")
        require(second_proof["turns_before"] == first_proof["turns_before"] + 1
                and second_proof["ordinal"] == first_proof["ordinal"] + 1,
                "the second turn did not read the first from the record")
        for name, proof, record in (("first", first_proof, first_record), ("second", second_proof, second_record)):
            require(record["entities"] == 3, f"the {name} turn's prompt, response and turn are not all recorded")
            places = [a["position"] for a in record["attestations"]]
            require(places == [proof["ordinal"]],
                    f"the {name} turn's [session, turn] claim is not attested once at its ordinal: {places}")

        report["proof"] = {
            "ordinary_endpoint_only": True,
            "client_history_resent": False,
            "turn_content_recorded_each_turn": True,
            "session_turn_claim_at_its_ordinal": True,
            "second_turn_read_the_first_from_the_record": True,
            "response_is_what_was_witnessed": True}
        canonical = json.dumps(report["proof"], sort_keys=True, separators=(",", ":")).encode()
        report["proof_fingerprint_sha256"] = hashlib.sha256(canonical).hexdigest()
        report["disposition"] = "verified"
        code = 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, IndexError, subprocess.SubprocessError) as error:
        report["error"] = str(error)
        code = 1
    report["wall_milliseconds"] = (time.monotonic_ns() - started) // 1_000_000
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: report[k] for k in ("schema", "disposition", "error", "wall_milliseconds") if k in report}))
    print(f"FORWARD_CHAT_LIFECYCLE_RECEIPT={args.receipt}")
    return code


if __name__ == "__main__":
    raise SystemExit(main())

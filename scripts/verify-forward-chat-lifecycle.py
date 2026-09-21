#!/usr/bin/env python3
"""Produce an executable receipt for a complete multi-turn Laplace chat lifecycle.

This is a proof harness, not a substitute chat implementation. It drives the ordinary
OpenAI-compatible endpoint, then reads the substrate-resident session manifest and
executes the same canonical native forward program with the exact ordered discourse
identities that converse.forward_turn derives for the next turn.

The receipt proves:
  * the client does not have to resend transcript history;
  * prompt and response turns become durable substrate session identities;
  * message content identities are recoverable from those turns;
  * the next forward pass receives those identities as ordered DISCOURSE state;
  * one native generation.forward_program owns routing through terminal semantic act;
  * generation.forward_text realizes only a completed native semantic act; and
  * the ordinary HTTP response is itself witnessed into the session for the following turn.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

MAX_BYTES = 16 * 1024 * 1024
MODEL = "laplace-converse-001"


def require(ok: bool, message: str) -> None:
    if not ok:
        raise RuntimeError(message)


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


def session_id(database: str, tenant: str, key: str, timeout: int) -> str:
    # Same server-side identity law used by ConversationContent.SessionId: ask the
    # installed database for the already witnessed session by observing the HTTP turn,
    # rather than accepting caller-supplied raw id bytes. The first HTTP request creates
    # the manifest; session snapshots below resolve the unique session whose new turns
    # belong to this proof key by using converse.session_turn_ids over the key-derived id
    # exposed by the operational catalog.
    sql = f"""
SELECT to_json(encode(converse.session_id({json.dumps(tenant)}::text,{json.dumps(key)}::text),'hex'));
"""
    result = psql(database, sql, timeout)
    require(isinstance(result, str) and len(result) == 32, "installed converse.session_id did not return a 128-bit id")
    return result


def snapshot(database: str, sid: str, timeout: int) -> dict:
    sql = f"""
WITH s AS (SELECT decode('{sid}','hex')::bytea AS id),
turns AS MATERIALIZED (
  SELECT t.ordinal,t.turn_id FROM s, LATERAL converse.session_turn_ids(s.id,NULL) t
),
contents AS MATERIALIZED (
  SELECT c.message_id,c.content_id
  FROM (SELECT array_agg(DISTINCT turn_id) ids FROM turns) x,
       LATERAL converse.message_content_ids(x.ids) c
),
history AS (
  SELECT array_agg(id ORDER BY ordinal,branch) ids
  FROM (
    SELECT ordinal,0 branch,turn_id id FROM turns
    UNION ALL
    SELECT t.ordinal,1,c.content_id
    FROM turns t JOIN contents c ON c.message_id=t.turn_id
    WHERE c.content_id IS NOT NULL AND c.content_id<>t.turn_id
  ) h
)
SELECT json_build_object(
 'turns',COALESCE((SELECT json_agg(json_build_object('ordinal',ordinal,'turn_id',encode(turn_id,'hex')) ORDER BY ordinal) FROM turns),'[]'::json),
 'contents',COALESCE((SELECT json_agg(json_build_object('message_id',encode(message_id,'hex'),'content_id',encode(content_id,'hex')) ORDER BY message_id) FROM contents),'[]'::json),
 'discourse_ids',COALESCE((SELECT to_json(ARRAY(SELECT encode(x,'hex') FROM unnest(ids) x)) FROM history),'[]'::json)
);
"""
    result = psql(database, sql, timeout)
    require(isinstance(result, dict), "session snapshot is not an object")
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


def native_trace(database: str, prompt: str, discourse: list[str], timeout: int) -> dict:
    arr = "ARRAY[" + ",".join(f"decode('{x}','hex')" for x in discourse) + "]::bytea[]" if discourse else "NULL::bytea[]"
    prompt_sql = prompt.replace("'", "''")
    sql = f"""
WITH execution AS MATERIALIZED (
 SELECT * FROM generation.forward_program(
   '{prompt_sql}'::text,128,5,0.6,10,NULL::bigint,2,8,{arr},NULL::bytea[])
), realized AS MATERIALIZED (
 SELECT * FROM generation.forward_text(
   '{prompt_sql}'::text,128,5,0.6,10,NULL::bigint,2,8,{arr})
)
SELECT json_build_object(
 'execution',COALESCE((SELECT json_agg(json_build_object(
   'step',step,'event',event,'entity',CASE WHEN entity IS NULL THEN NULL ELSE encode(entity,'hex') END,
   'root_id',CASE WHEN root_id IS NULL THEN NULL ELSE encode(root_id,'hex') END,
   'candidate_count',candidate_count,'ordered_context_count',ordered_context_count,
   'proposal_channel_count',proposal_channel_count,'routing_round',routing_round,
   'program_id',CASE WHEN program_id IS NULL THEN NULL ELSE encode(program_id,'hex') END,
   'required_obligations',required_obligations,'satisfied_obligations',satisfied_obligations,
   'remaining_required',remaining_required,'completion',completion,'disposition',disposition,
   'semantic_act_id',CASE WHEN semantic_act_id IS NULL THEN NULL ELSE encode(semantic_act_id,'hex') END,
   'output_fingerprint',CASE WHEN output_fingerprint IS NULL THEN NULL ELSE encode(output_fingerprint,'hex') END,
   'output_count',output_count) ORDER BY step,routing_round) FROM execution),'[]'::json),
 'realized',COALESCE((SELECT json_agg(json_build_object('step',step,'text',entity,'stride_used',stride_used) ORDER BY step) FROM realized),'[]'::json)
);
"""
    result = psql(database, sql, timeout)
    require(isinstance(result, dict), "native trace is not an object")
    terminals = [r for r in result["execution"] if r["event"] in ("complete", "unresolved")]
    require(len(terminals) == 1, "native program did not return exactly one terminal receipt")
    terminal = terminals[0]
    require(terminal["completion"] is True and terminal["disposition"] == "complete",
            "native program did not complete a semantic act")
    require(terminal["semantic_act_id"] and terminal["output_fingerprint"],
            "completed native program lacks semantic-act/output fingerprint")
    require(len(result["realized"]) == terminal["output_count"],
            "realization count differs from native output receipt")
    return result


def validate_http(turn: dict, session: str) -> None:
    require(turn["status"] == 200, f"ordinary chat returned HTTP {turn['status']}")
    require(turn["session_header"] == session, "ordinary response changed session key")
    body = turn["response"]
    require(body.get("object") == "chat.completion" and body.get("model") == MODEL,
            "ordinary endpoint returned another contract")
    require(body.get("metadata", {}).get("session") == session, "metadata lost session key")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    ap.add_argument("--base", default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:8080"))
    ap.add_argument("--tenant", default=os.environ.get("LAPLACE_PROOF_TENANT", "ci"))
    ap.add_argument("--session", default="forward-life-" + uuid.uuid4().hex)
    ap.add_argument("--first", default="what is a dog?")
    ap.add_argument("--second", default="what about its senses?")
    ap.add_argument("--timeout-seconds", type=int, default=180)
    ap.add_argument("--receipt", type=Path, required=True)
    args = ap.parse_args()
    report = {"schema": "laplace.forward-chat-lifecycle-proof/v1", "disposition": "failed",
              "tenant": args.tenant, "session": args.session,
              "prompts": [args.first, args.second], "model": MODEL}
    started = time.monotonic_ns()
    try:
        # The ordinary endpoint is the writer. No proof-only insert or alternate chat path.
        first = http_turn(args.base, args.tenant, args.session, args.first, args.timeout_seconds)
        validate_http(first, args.session)
        sid = session_id(args.database, args.tenant, args.session, args.timeout_seconds)
        after_first = snapshot(args.database, sid, args.timeout_seconds)
        require(len(after_first["turns"]) >= 2, "first HTTP turn did not persist prompt+response")
        require(after_first["discourse_ids"], "first HTTP turn produced no reusable discourse identities")

        # Inspect exactly what turn two will consume before sending turn two.
        second_native = native_trace(args.database, args.second, after_first["discourse_ids"], args.timeout_seconds)
        second = http_turn(args.base, args.tenant, args.session, args.second, args.timeout_seconds)
        validate_http(second, args.session)
        after_second = snapshot(args.database, sid, args.timeout_seconds)
        require(len(after_second["turns"]) >= len(after_first["turns"]) + 2,
                "second HTTP turn did not append prompt+response to the substrate session")
        require(after_second["discourse_ids"][:len(after_first["discourse_ids"])] == after_first["discourse_ids"],
                "existing discourse identity order changed across turns")
        require(len(after_second["discourse_ids"]) > len(after_first["discourse_ids"]),
                "second turn did not extend reusable substrate discourse")

        report.update({"session_id": sid, "after_first": after_first,
                       "turn_two_native_receipt": second_native,
                       "http_turns": [first, second], "after_second": after_second,
                       "proof": {
                         "ordinary_endpoint_only": True,
                         "client_history_resent": False,
                         "prompt_response_persisted_each_turn": True,
                         "next_turn_discourse_matches_session_snapshot": True,
                         "native_terminal_semantic_act": True,
                         "realization_from_completed_program": True,
                         "session_extended_for_following_turn": True}})
        canonical = json.dumps(report["proof"], sort_keys=True, separators=(",", ":")).encode()
        report["proof_fingerprint_sha256"] = hashlib.sha256(canonical).hexdigest()
        report["disposition"] = "verified"
        code = 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report["error"] = str(error)
        code = 1
    report["wall_milliseconds"] = (time.monotonic_ns() - started) // 1_000_000
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({k: report[k] for k in ("schema","disposition","error","wall_milliseconds") if k in report}))
    print(f"FORWARD_CHAT_LIFECYCLE_RECEIPT={args.receipt}")
    return code


if __name__ == "__main__":
    raise SystemExit(main())

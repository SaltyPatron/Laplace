#!/usr/bin/env python3
"""Run legacy repair against branch-native SQL in pr-db-proof's private cluster.

This is executable PostgreSQL evidence, not a substitute for live corpus
readback. The fixtures deliberately contain old invalid Content physicalities.
Only the newly created, uniquely named fixture database may be mutated.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import unittest
import uuid


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from lib.repair_transaction import RepairProtocolError, preserve_and_apply, write_new_json  # noqa: E402

SPEC = importlib.util.spec_from_file_location("legacy_content_repair", ROOT / "scripts/repair-legacy-content.py")
assert SPEC and SPEC.loader
REPAIR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPAIR)
QUIESCENCE_SPEC = importlib.util.spec_from_file_location("repair_quiescence", ROOT / "scripts/quiesce-managed-database.py")
assert QUIESCENCE_SPEC and QUIESCENCE_SPEC.loader
QUIESCENCE = importlib.util.module_from_spec(QUIESCENCE_SPEC)
QUIESCENCE_SPEC.loader.exec_module(QUIESCENCE)


FIXTURE = r"""
CREATE EXTENSION postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;
CREATE SCHEMA repair_test;
CREATE TABLE repair_test.ids(name text PRIMARY KEY,id bytea UNIQUE NOT NULL);
INSERT INTO repair_test.ids
SELECT name,realize.canonical_id('legacy-repair-regression/'||name)
FROM unnest(ARRAY['source','m1','m2','p0','p1','p2','name','msg1','msg2','player','session']) name;
INSERT INTO repair_test.ids VALUES
 ('game',public.laplace_hash128_merkle(0::smallint,ARRAY[
   (SELECT id FROM repair_test.ids WHERE name='p0'),
   (SELECT id FROM repair_test.ids WHERE name='m1'),
   (SELECT id FROM repair_test.ids WHERE name='m2')])),
 ('healthy-game',public.laplace_hash128_merkle(0::smallint,ARRAY[
   (SELECT id FROM repair_test.ids WHERE name='p0'),
   (SELECT id FROM repair_test.ids WHERE name='m2'),
   (SELECT id FROM repair_test.ids WHERE name='m1')])),
 ('one-move-game',public.laplace_hash128_merkle(0::smallint,ARRAY[
   (SELECT id FROM repair_test.ids WHERE name='p0'),
   (SELECT id FROM repair_test.ids WHERE name='m1')])),
 ('container',public.laplace_hash128_merkle(0::smallint,ARRAY[
   (SELECT id FROM repair_test.ids WHERE name='player'),
   (SELECT id FROM repair_test.ids WHERE name='m1')]));
CREATE FUNCTION repair_test.id(name text) RETURNS bytea
LANGUAGE sql STABLE STRICT AS $$ SELECT id FROM repair_test.ids WHERE ids.name=$1 $$;
CREATE FUNCTION repair_test.physicality_id(name text,kind smallint DEFAULT 1) RETURNS bytea
LANGUAGE sql STABLE STRICT AS $$
 SELECT public.laplace_hash128_blake3(repair_test.id(name)||
   CASE kind WHEN 1 THEN decode('0100','hex') WHEN 3 THEN decode('0300','hex') END)
$$;
CREATE FUNCTION repair_test.reset() RETURNS void LANGUAGE plpgsql AS $fixture$
BEGIN
 DELETE FROM laplace.attestations WHERE source_id=repair_test.id('source');
 DELETE FROM laplace.physicalities WHERE entity_id IN (SELECT id FROM repair_test.ids);
 DELETE FROM laplace.entities WHERE id IN (SELECT id FROM repair_test.ids);
 INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
 SELECT id,CASE WHEN name IN ('game','healthy-game','session','msg1','msg2') THEN 4
                WHEN name='player' THEN 3 WHEN name LIKE 'p%' OR name='name' THEN 2 ELSE 1 END,
   realize.canonical_id(CASE WHEN name IN ('game','healthy-game') THEN 'Chess_Game'
      WHEN name='player' THEN 'Chess_Player' WHEN name='session' THEN 'Conversation_Session'
      WHEN name IN ('msg1','msg2') THEN 'Conversation_Message'
      WHEN name IN ('p0','p1','p2') THEN 'Chess_Position'
      WHEN name IN ('m1','m2') THEN 'MOVE' ELSE 'Repair_Test_Atom' END),
   repair_test.id('source'),'2026-09-01 12:34:56.123456+00'::timestamptz
 FROM repair_test.ids WHERE name NOT IN ('one-move-game','container');
 INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
     n_constituents,alignment_residual,source_dim,observed_at)
 SELECT repair_test.physicality_id(name),id,1,coord,public.laplace_hilbert_encode(coord),NULL,
   0,'-0'::float8,4,'2026-09-02 01:02:03.654321+00'::timestamptz
 FROM repair_test.ids CROSS JOIN LATERAL (SELECT
   CASE name WHEN 'p0' THEN ST_MakePoint(1,0,0,0)
             WHEN 'm1' THEN ST_MakePoint(0,1,0,0)
             WHEN 'm2' THEN ST_MakePoint(0,0,1,0)
             ELSE ST_MakePoint(0.1,0.2,0.3,0.4) END AS coord) point
 WHERE name NOT IN ('game','healthy-game','player','session','one-move-game','container');
 INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
     n_constituents,alignment_residual,source_dim,observed_at)
 SELECT repair_test.physicality_id(name,kind::smallint),repair_test.id(name),kind,
   coord,public.laplace_hilbert_encode(coord),
   CASE WHEN name='player' THEN public.laplace_mantissa_pack(ids[1],1,1,4)
        WHEN name='session' THEN ST_MakeLine(ARRAY[
          public.laplace_mantissa_pack(ids[1],1,1,8),
          public.laplace_mantissa_pack(ids[2],2,1,8),
          public.laplace_mantissa_pack(ids[3],3,1,8)])
        ELSE public.laplace_trajectory_build(ids) END,
   cardinality(ids),0.125,8,'2026-09-03 03:04:05.678901+00'::timestamptz
 FROM (VALUES
   ('game',1,ARRAY[repair_test.id('m1'),repair_test.id('m2')]),
   ('game',3,ARRAY[repair_test.id('p0'),repair_test.id('p1'),repair_test.id('p2')]),
   ('healthy-game',1,ARRAY[repair_test.id('p0'),repair_test.id('m2'),repair_test.id('m1')]),
   ('player',1,ARRAY[repair_test.id('name')]),
   ('session',1,ARRAY[repair_test.id('msg1'),repair_test.id('msg2'),repair_test.id('msg1')])) owner(name,kind,ids)
 CROSS JOIN LATERAL (SELECT CASE WHEN name IN ('game','healthy-game') AND kind=1 THEN
     public.laplace_karcher_mean_4d((SELECT ST_Collect(p.coord ORDER BY child.ordinal)
       FROM unnest(ids) WITH ORDINALITY child(id,ordinal)
       JOIN laplace.physicalities p ON p.entity_id=child.id AND p.type=1))
     ELSE ST_MakePoint(0.2,0.3,0.4,0.1) END AS coord) point;
 INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,
     outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
 SELECT realize.canonical_id('legacy-repair-regression/witness/'||name),repair_test.id(subject),
   laplace.relation_type_id(relation),repair_test.id(object),repair_test.id('source'),
   repair_test.id(context),2,'2026-09-04 04:05:06.987654+00'::timestamptz,3,3000000000,30000000000
 FROM (VALUES ('alias','player','HAS_NAME_ALIAS','name','player'),
              ('setup','game','HAS_SETUP','p0','game'),
              ('membership-1','msg1','APPEARS_IN','session','session'),
              ('membership-2','msg2','APPEARS_IN','session','session'))
      witness(name,subject,relation,object,context);
END $fixture$;
SELECT repair_test.reset();
"""

# This independent complete row projection lets receipts be checked against the
# actual original database representation, including non-geometric metadata.
PHYSICALITY = """jsonb_build_object(
 'id',encode(p.id,'hex'),'entity_id',encode(p.entity_id,'hex'),'type',p.type,
 'coord_ewkb',encode(ST_AsEWKB(p.coord),'hex'),
 'hilbert_index',encode(p.hilbert_index,'hex'),
 'trajectory_ewkb',encode(ST_AsEWKB(p.trajectory),'hex'),
 'radius_origin_bits',encode(float8send(p.radius_origin),'hex'),
 'n_constituents',p.n_constituents,
 'alignment_residual_bits',encode(float8send(p.alignment_residual),'hex'),
 'source_dim',p.source_dim,'observed_at',p.observed_at,
 'observed_at_binary',encode(timestamptz_send(p.observed_at),'hex'))"""

PROJECTION_TARGETS = """
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
    n_constituents,alignment_residual,source_dim,observed_at)
SELECT public.laplace_hash128_blake3(entity_id||decode('0300','hex')),entity_id,3,
  coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at
FROM laplace.physicalities WHERE type=1
  AND entity_id IN (repair_test.id('player'),repair_test.id('session'));
"""

PLAYER_ALIAS_TARGET = """
DELETE FROM laplace.physicalities WHERE entity_id IN
  (SELECT id FROM repair_test.ids WHERE name IN ('alias-old','alias-target','alias-atom-old','alias-atom-target'));
DELETE FROM laplace.entities WHERE id IN
  (SELECT id FROM repair_test.ids WHERE name IN ('alias-old','alias-target','alias-atom-old','alias-atom-target'));
DELETE FROM repair_test.ids WHERE name IN ('alias-old','alias-target','alias-atom-old','alias-atom-target');
CREATE TEMP TABLE repair_alias_roots AS
SELECT * FROM converse.text_root_placements(ARRAY['λ','棋']);
INSERT INTO repair_test.ids(name,id)
SELECT CASE ord WHEN 1 THEN 'alias-old' ELSE 'alias-target' END,root_id
FROM repair_alias_roots ON CONFLICT(name) DO NOTHING;
DELETE FROM laplace.physicalities WHERE entity_id IN (SELECT root_id FROM repair_alias_roots);
DELETE FROM laplace.entities WHERE id IN (SELECT root_id FROM repair_alias_roots);
INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
SELECT root_id,tier,realize.canonical_id('Repair_Test_Atom'),repair_test.id('source'),
  '2026-09-01 12:34:56.123456+00'::timestamptz FROM repair_alias_roots;
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
  n_constituents,alignment_residual,source_dim,observed_at)
SELECT public.laplace_hash128_blake3(root_id||decode('0100','hex')),root_id,1,
  ST_MakePoint(coord[1],coord[2],coord[3],coord[4]),hilbert_index,NULL,0,NULL,NULL,
  '2026-09-02 01:02:03.654321+00'::timestamptz FROM repair_alias_roots;
UPDATE laplace.physicalities player SET
  coord=ST_MakePoint(root.coord[1],root.coord[2],root.coord[3],root.coord[4]),
  hilbert_index=root.hilbert_index,trajectory=public.laplace_mantissa_pack(root.root_id,1,1,0::bigint),
  n_constituents=1,alignment_residual=NULL,source_dim=NULL
FROM repair_alias_roots root WHERE root.ord=1 AND player.id=repair_test.physicality_id('player');
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
  n_constituents,alignment_residual,source_dim,observed_at)
SELECT repair_test.physicality_id('player',3::smallint),repair_test.id('player'),3,
  ST_MakePoint(coord[1],coord[2],coord[3],coord[4]),hilbert_index,
  public.laplace_mantissa_pack(root_id,1,1,0::bigint),1,NULL,NULL,
  '2026-09-05 01:02:03.654321+00'::timestamptz FROM repair_alias_roots WHERE ord=2;
DELETE FROM laplace.attestations WHERE subject_id=repair_test.id('player')
  AND type_id=laplace.relation_type_id('HAS_NAME_ALIAS');
INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,
  outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
SELECT realize.canonical_id('legacy-repair-regression/independent-alias/'||ord),repair_test.id('player'),
  laplace.relation_type_id('HAS_NAME_ALIAS'),root_id,repair_test.id('source'),NULL,
  2,'2026-09-04 04:05:06.987654+00'::timestamptz,3,3000000000,30000000000
FROM repair_alias_roots;
"""

# Exercise a genuine non-null Content trajectory as well as atom aliases. The
# native composer determines both root identities/placements; the retained
# manifest contains two occurrences of each native atom, including duplicates.
PLAYER_COMPOSITE_ALIAS_TARGET = PLAYER_ALIAS_TARGET.replace(
    "ARRAY['λ','棋']", "ARRAY['λλ','棋棋']", 1).replace(
    "hilbert_index,NULL,0,NULL,NULL,", """hilbert_index,
    public.laplace_trajectory_build(ARRAY[
      laplace.content_id(convert_to(CASE ord WHEN 1 THEN 'λ' ELSE '棋' END,'UTF8')),
      laplace.content_id(convert_to(CASE ord WHEN 1 THEN 'λ' ELSE '棋' END,'UTF8'))]),2,NULL,NULL,""", 1) + """
CREATE TEMP TABLE repair_alias_atoms AS
SELECT * FROM converse.text_root_placements(ARRAY['λ','棋']);
DELETE FROM laplace.physicalities WHERE entity_id IN (SELECT root_id FROM repair_alias_atoms);
DELETE FROM laplace.entities WHERE id IN (SELECT root_id FROM repair_alias_atoms);
INSERT INTO repair_test.ids(name,id)
SELECT CASE ord WHEN 1 THEN 'alias-atom-old' ELSE 'alias-atom-target' END,root_id
FROM repair_alias_atoms ON CONFLICT(name) DO UPDATE SET id=excluded.id;
INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
SELECT root_id,tier,realize.canonical_id('Repair_Test_Atom'),repair_test.id('source'),
  '2026-09-01 12:34:56.123456+00'::timestamptz FROM repair_alias_atoms
ON CONFLICT(id,tier) DO NOTHING;
INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
  n_constituents,alignment_residual,source_dim,observed_at)
SELECT public.laplace_hash128_blake3(root_id||decode('0100','hex')),root_id,1,
  ST_MakePoint(coord[1],coord[2],coord[3],coord[4]),hilbert_index,NULL,0,NULL,NULL,
  '2026-09-02 01:02:03.654321+00'::timestamptz FROM repair_alias_atoms
ON CONFLICT(id) DO NOTHING;
"""


class PriorReceiptContracts(unittest.TestCase):
    """Exercise historical journal admission without requiring PostgreSQL."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def receipt(self, name, *, records=None, manifest_fields=None):
        directory = self.root / name
        directory.mkdir()
        if records is None:
            records = [
                {"kind": "context", "database": "fixture"},
                {"kind": "native-input", "entity_id": "01" * 16,
                 "entity": {"id": "01" * 16}, "content": {"id": "02" * 16}},
                {"kind": "physicality", "action": "rewrite", "original": {"id": "03" * 16},
                 "proposed": {"id": "04" * 16}},
                {"kind": "plan", "count": 1, "native_input_count": 1,
                 "rewritten_rows": 1, "reused_projection_rows": 0, "unresolved": 0},
            ]
        raw = b"".join((json.dumps(record, sort_keys=True) + "\n").encode() for record in records)
        (directory / "plan.jsonl").write_bytes(raw)
        manifest = {"schema": "laplace.legacy-content-repair-plan/v1", "plan_bytes": len(raw),
                    "plan_sha256": hashlib.sha256(raw).hexdigest(),
                    "planned_rows": sum(record["kind"] == "physicality" for record in records),
                    "native_input_rows": sum(record["kind"] == "native-input" for record in records)}
        manifest.update(manifest_fields or {})
        write_new_json(directory / "manifest.json", manifest)
        return directory, manifest, records, raw

    def test_prior_journal_authenticates_every_record_before_compact_replay(self):
        directory, manifest, records, raw = self.receipt("valid")
        replay = REPAIR.reconciliation_input(directory / "plan.jsonl")
        projected = [json.loads(line) for line in replay.read_bytes().splitlines()]
        self.assertEqual(projected[:-1], [r for r in records if r["kind"] != "native-input"])
        self.assertEqual(projected[-1], {"kind": "verified-native-input-count", "count": 1,
            "source_plan_sha256": manifest["plan_sha256"], "source_plan_bytes": len(raw)})
        self.assertEqual((directory / "plan.jsonl").read_bytes(), raw)
        (directory / "plan.jsonl").write_bytes(raw.replace(b'"01', b'"ff', 1))
        with self.assertRaisesRegex(ValueError, "evidence changed"):
            REPAIR.reconciliation_input(directory / "plan.jsonl")

    def test_prior_record_and_action_counts_must_match_authenticated_summary(self):
        _, _, records, _ = self.receipt("template")
        for label, bad_records in (
            ("no-summary", records[:-1]),
            ("native-count", records[:-1] + [records[-1] | {"native_input_count": 2}]),
            ("action-count", records[:-1] + [records[-1] | {"reused_projection_rows": 1}]),
            ("after-summary", records + [{"kind": "native-input"}]),
        ):
            with self.subTest(label=label):
                directory, _, _, _ = self.receipt(label, records=bad_records)
                with self.assertRaises(ValueError):
                    REPAIR.verified_plan(directory)

    def test_new_alias_action_has_its_own_authenticated_count(self):
        _, _, records, _ = self.receipt("count-template")
        alias_records = [dict(record) for record in records]
        alias_records[2]["action"] = "retain-witnessed-player-alias-projection"
        alias_records[-1].update(rewritten_rows=0, retained_player_alias_rows=1)
        directory, manifest, _, _ = self.receipt("alias-count", records=alias_records)
        self.assertEqual(REPAIR.verified_plan(directory)[0], manifest)
        alias_records[-1]["retained_player_alias_rows"] = 0
        invalid, _, _, _ = self.receipt("wrong-alias-count", records=alias_records)
        with self.assertRaisesRegex(ValueError, "action count changed"):
            REPAIR.verified_plan(invalid)

    def test_metadata_line_recorded_and_current_bounds_are_enforced(self):
        directory, manifest, _, _ = self.receipt("finite-272")
        self.assertEqual(REPAIR.verified_plan(directory)[0], manifest)
        with self.assertRaisesRegex(ValueError, "current byte bound"):
            REPAIR.verified_plan(directory, max_bytes=manifest["plan_bytes"] - 1)
        with self.assertRaisesRegex(ValueError, "line bound"):
            REPAIR.verified_plan(directory, max_line_bytes=8)
        null_policy, _, _, _ = self.receipt("null-policy", manifest_fields={"max_bytes": None})
        with self.assertRaisesRegex(ValueError, "positive integers"):
            REPAIR.verified_plan(null_policy)
        oversized = self.root / "oversized.json"
        oversized.write_bytes(b" " * (REPAIR.MAX_METADATA_BYTES + 1))
        with self.assertRaisesRegex(ValueError, "metadata exceeds"):
            REPAIR.read_metadata(oversized)

    def test_one_prior_budget_covers_discovery_replay_and_closure(self):
        directory, manifest, _, _ = self.receipt("pending")
        write_new_json(directory / "submission.json", {})
        budget = REPAIR.PriorReceiptBudget(3 * manifest["plan_bytes"])
        self.assertEqual(REPAIR.unresolved_submissions(self.root, budget=budget), [directory / "plan.jsonl"])
        self.assertEqual(budget.bytes_read, manifest["plan_bytes"])
        REPAIR.reconciliation_input(directory / "plan.jsonl", budget=budget)
        self.assertEqual(budget.bytes_read, 2 * manifest["plan_bytes"])
        current, current_manifest, _, _ = self.receipt("current", records=[
            {"kind": "context", "prior_submission_reconciliation": [
                {"receipt": str((directory / "plan.jsonl").resolve()), "disposition": "originals-confirmed"}]},
            {"kind": "plan", "count": 0, "native_input_count": 0},
        ])
        write_new_json(current / "outcome.json", {"disposition": "commit-confirmed",
            "plan_sha256": current_manifest["plan_sha256"], "planned_rows": 0, "applied": {"count": 0}})
        current_budget = REPAIR.PriorReceiptBudget(current_manifest["plan_bytes"])
        REPAIR.close_reconciled_submissions(self.root, current, budget=budget, current_budget=current_budget)
        self.assertEqual(current_budget.bytes_read, current_manifest["plan_bytes"])
        self.assertEqual(budget.bytes_read, 3 * manifest["plan_bytes"])
        with self.assertRaisesRegex(ValueError, "aggregate byte bound"):
            REPAIR.verified_plan(directory, budget=budget)

    def test_legacy_retirement_projection_preserves_evidence_and_explicit_contract(self):
        original = {"id": "01" * 16, "entity_id": "02" * 16, "type": 1, "coord_ewkb": "exact"}
        target = original | {"id": "03" * 16, "type": 3}
        record = {"kind": "physicality", "operation": "retire-redundant-content",
                  "repair_kind": "player-projection", "original": original, "proposed": target,
                  "evidence": {"occupied_projections": [target]}}
        result = REPAIR.normalized_prior_physicality(record)
        self.assertNotIn("action", record)
        self.assertEqual(result["action"], "reuse-existing-projection")
        self.assertEqual(result["existing_target"], target)
        self.assertEqual(result["migration_proposal"], original | {"id": target["id"], "type": 3})
        with self.assertRaisesRegex(ValueError, "inconsistent"):
            REPAIR.normalized_prior_physicality(record | {"action": "rewrite"})
        with self.assertRaisesRegex(ValueError, "exact retained target"):
            REPAIR.normalized_prior_physicality(record | {"evidence": {"occupied_projections": []}})

    def test_prior_deadline_remains_expired_before_any_read(self):
        directory, _, _, _ = self.receipt("expired")
        budget = REPAIR.PriorReceiptBudget(REPAIR.MAX_BYTES)
        with self.assertRaisesRegex(TimeoutError, "maintenance deadline"):
            REPAIR.verified_plan(directory, budget=budget, deadline=time.monotonic() - 1)
        self.assertEqual(budget.bytes_read, 0)


class NativeRepairProof:
    def __init__(self, psql: Path, database: str, receipts: Path):
        self.command = [str(psql), "-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-d", database]
        self.receipts = receipts
        self.source_sha = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT,
                                         check=True, text=True, capture_output=True).stdout.strip()
        self.completed: list[str] = []

    def sql(self, sql: str) -> str:
        try:
            result = subprocess.run(self.command, input="SET timezone='UTC';\n" + sql,
                                    check=True, text=True, capture_output=True, timeout=180)
        except subprocess.CalledProcessError as error:
            print(error.stderr, file=sys.stderr)
            raise
        return result.stdout.strip()

    def state(self) -> dict:
        return json.loads(self.sql(f"""SELECT jsonb_build_object(
          'physicalities',COALESCE((SELECT jsonb_object_agg(encode(p.id,'hex'),{PHYSICALITY})
            FROM laplace.physicalities p),'{{}}'::jsonb),
          'entities',COALESCE((SELECT jsonb_agg(to_jsonb(e) ORDER BY e.id,e.tier)
            FROM laplace.entities e),'[]'::jsonb),
          'attestations',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id,a.type_id,a.subject_id)
            FROM laplace.attestations a),'[]'::jsonb));"""))

    def run(self, name: str, *, apply: str | None = None,
            prior: list[Path] | None = None, max_rows: int = 100,
            measurement_only: bool = False, auxiliary_reserve_bytes: int = 0,
            producer_generation: dict | None = None,
            **resource_limits) -> dict:
        return preserve_and_apply(self.command, REPAIR.plan_sql(max_rows, prior or [],
            producer_generation=producer_generation, resource_preamble=True),
            REPAIR.apply_sql() if apply is None else apply, self.receipts / name,
            source_sha=self.source_sha, max_rows=max_rows, timeout=180,
            receipt_sql=REPAIR.receipt_stream_sql(), measurement_only=measurement_only,
            auxiliary_reserve_bytes=auxiliary_reserve_bytes, **resource_limits)

    def records(self, name: str) -> list[dict]:
        directory = self.receipts / name
        contents = (directory / "plan.jsonl").read_bytes()
        manifest = json.loads((directory / "manifest.json").read_text())
        assert manifest["plan_bytes"] == len(contents), "receipt byte count changed"
        assert manifest["plan_sha256"] == hashlib.sha256(contents).hexdigest(), "receipt digest changed"
        assert manifest["source_sha"] == self.source_sha, "receipt lost source identity"
        assert contents.endswith(b"\n"), "retained JSONL lost its final newline"
        lines = contents.split(b"\n")[:-1]
        records = [json.loads(line) for line in lines]
        assert REPAIR.verified_plan(directory) == (manifest, records[0])
        native_inputs = [row for row in records if row["kind"] == "native-input"]
        assert manifest["native_input_rows"] == len(native_inputs), "native input receipt count changed"
        assert records[-1]["native_input_count"] == len(native_inputs), "native input plan count changed"
        resources_bytes = (directory / "resources.json").read_bytes()
        resources = json.loads(resources_bytes)
        assert resources["kind"] == "resources"
        assert resources["schema"] == "laplace.legacy-content-repair-resources/v1"
        assert resources["plan_bytes"] == sum(len(line) + 1 for line in lines) == len(contents)
        assert resources["max_line_bytes"] == max(map(len, lines)), "header lost UTF-8 line byte bound"
        assert resources["max_jsonl_line_bytes"] == max(len(line) + 1 for line in lines)
        for field, kind in (("physicality_rows", "physicality"), ("native_input_rows", "native-input"),
                            ("context_rows", "context"), ("summary_rows", "plan")):
            assert resources[field] == sum(record["kind"] == kind for record in records)
        assert resources["context_rows"] == resources["summary_rows"] == 1
        context_fields = ("database", "database_oid", "system_identifier", "transaction", "observed_at",
                          "producer_generation", "chess_coordinate_recipe", "write_epoch_before",
                          "substrate_extension_version", "geometry_extension_version")
        assert resources["plan_context"] == {field: records[0][field] for field in context_fields}, \
            "resource header did not preserve its frozen receipt context"
        assert resources["plan_summary"] == records[-1], "resource header did not preserve its frozen plan summary"
        assert manifest["resources_sha256"] == hashlib.sha256(resources_bytes).hexdigest()
        assert manifest["receipt_sql_sha256"] == hashlib.sha256(REPAIR.receipt_stream_sql().encode()).hexdigest()
        return records

    def native_inputs(self, records: list[dict], before: dict, names: list[str]) -> None:
        expected_ids = set(json.loads(self.sql("SELECT jsonb_agg(encode(id,'hex')) "
            "FROM repair_test.ids WHERE name=ANY(ARRAY[" +
            ",".join("'" + name + "'" for name in names) + "]);")))
        inputs = [record for record in records if record["kind"] == "native-input"]
        assert {record["entity_id"] for record in inputs} == expected_ids
        assert len(inputs) == len(expected_ids), "native input snapshots duplicated or missing"
        for record in inputs:
            assert record["entity"] in before["entities"], "native input lost its exact entity row"
            content = record["content"]
            assert content == before["physicalities"][content["id"]], "native input lost exact Content bytes"
            assert content["entity_id"] == record["entity_id"] and content["type"] == 1

    def readback(self, name: str, before: dict, after: dict) -> None:
        def digest(state: dict) -> str:
            return hashlib.sha256(json.dumps(state, sort_keys=True).encode()).hexdigest()
        write_new_json(self.receipts / name / "fresh-connection-readback.json", {
            "schema": "laplace.legacy-content-native-readback/v1",
            "source_sha": self.source_sha, "before_sha256": digest(before),
            "after_sha256": digest(after), "tables_unchanged": before == after,
            "after": after})

    def measurement_only(self) -> None:
        self.sql("SELECT repair_test.reset();")
        before = self.state()
        epoch_sql = "SELECT jsonb_build_object('last_value',last_value,'is_called',is_called) FROM laplace.apply_write_epoch;"
        epoch_before = self.sql(epoch_sql)
        name = "measurement-over-receipt-envelopes"
        result = self.run(name, max_rows=3, measurement_only=True,
            max_native_inputs=1, max_bytes=1, max_line_bytes=1,
            apply="DO $$ BEGIN RAISE EXCEPTION 'Measurement unexpectedly executed APPLY'; END $$;")
        after = self.state()
        assert after == before, "measurement changed the complete substrate estate"
        assert self.sql(epoch_sql) == epoch_before, "measurement advanced the write epoch"
        assert result["disposition"] == "measurement-only-rollback-confirmed"
        assert result["envelope_admitted"] is False
        assert result["max_rows"] == 3 and result["max_native_inputs"] == 1
        assert result["max_bytes"] == result["max_line_bytes"] == 1
        assert result["auxiliary_reserve_bytes"] == 0
        resources = result["resources"]
        assert resources["physicality_rows"] == 3 and resources["native_input_rows"] == 8
        assert resources["context_rows"] == resources["summary_rows"] == 1
        assert resources["plan_summary"]["count"] == 3
        assert resources["plan_summary"]["native_input_count"] == 8
        assert resources["plan_summary"]["unresolved"] == 0
        assert resources["plan_bytes"] > 1 and resources["max_line_bytes"] > 1
        assert resources["max_jsonl_line_bytes"] == resources["max_line_bytes"] + 1
        for field in ("native_input_rows", "plan_bytes", "max_line_bytes"):
            assert any(rejection.startswith(field + "=") for rejection in result["resource_rejections"])
        directory = self.receipts / name
        resource_bytes = (directory / "resources.json").read_bytes()
        assert json.loads(resource_bytes) == resources
        assert result["resources_sha256"] == hashlib.sha256(resource_bytes).hexdigest()
        assert json.loads((directory / "measurement.json").read_text()) == result
        for filename in ("plan.jsonl", "manifest.json", "submission.json", "outcome.json", "failure.json"):
            assert not (directory / filename).exists(), f"measurement unexpectedly retained {filename}"
        self.readback(name, before, after)
        self.completed.append("measurement-over-tiny-caps-retains-complete-header-and-confirms-rollback")

    def success(self) -> None:
        before = self.state()
        # One healthy game is outside the repair envelope: the bound applies
        # after failed Content identity classification, not to all owners.
        fixture_producer = {"fixture": "λ棋"}
        outcome = self.run("positive", max_rows=3, producer_generation=fixture_producer)
        after = self.state()
        records = self.records("positive")
        assert records[0]["producer_generation"] == fixture_producer
        utf8_receipt = (self.receipts / "positive" / "plan.jsonl").read_bytes()
        assert "λ棋".encode("utf-8") in utf8_receipt, "fixture Unicode was not emitted as UTF-8"
        assert len(utf8_receipt) > len(utf8_receipt.decode("utf-8")), "byte-count fixture remained ASCII-only"
        rows = [record for record in records if record["kind"] == "physicality"]
        assert outcome["disposition"] == "commit-confirmed"
        assert outcome["applied"]["count"] == 3
        assert records[-1]["native_input_count"] == outcome["native_input_rows"] == 8
        recipe = records[0]["chess_coordinate_recipe"]
        assert recipe["sql_function"] == "public.laplace_karcher_mean_4d"
        assert recipe["native_kernel"] == "math4d_karcher_mean"
        assert recipe["tolerance"] == 1e-12 and recipe["max_iterations"] == 64
        assert {row["repair_kind"] for row in rows} == {
            "chess-line", "player-projection", "session-projection"}
        self.native_inputs(records, before, ["m1", "m2", "p0", "p1", "p2", "name", "msg1", "msg2"])
        expected = dict(before["physicalities"])
        for row in rows:
            assert row["disposition"] == "eligible"
            assert row["action"] == "rewrite" and row["existing_target"] is None
            assert row["migration_proposal"] == row["proposed"]
            if row["repair_kind"] != "chess-line":
                assert row["evidence"]["position_projection"] is None, "absent optional row became an all-null snapshot"
                assert row["evidence"]["start_content"] is None
            old, new = row["original"], row["proposed"]
            assert expected.pop(old["id"]) == old, "receipt did not preserve complete original bytes"
            expected[new["id"]] = new
            evidence = row["evidence"]
            assert evidence["entity"] in before["entities"]
            for key in ("name_witnesses", "setup_witnesses", "membership_witnesses"):
                assert all(witness in before["attestations"] for witness in evidence[key] or [])
            if row["repair_kind"] == "chess-line":
                assert old["id"] == new["id"] and new["type"] == 1
                assert new["n_constituents"] == old["n_constituents"] + 1
                assert old["coord_ewkb"] != new["coord_ewkb"], "line placement was not repaired"
                for key in ("position_projection", "start_content"):
                    assert evidence[key] == before["physicalities"][evidence[key]["id"]]
            else:
                assert old["id"] != new["id"] and new["type"] == 3
                for key in old.keys() - {"id", "type"}:
                    assert old[key] == new[key], f"projection rewrite changed retained {key}"
        assert after["physicalities"] == expected, "repair changed an unplanned physicality"
        assert after["entities"] == before["entities"], "repair changed canonical entity evidence"
        assert after["attestations"] == before["attestations"], "repair changed testimony"
        self.readback("positive", before, after)
        assert self.sql("""SELECT
          array_agg(v.entity_id ORDER BY v.ordinal)=ARRAY[
            repair_test.id('p0'),repair_test.id('m1'),repair_test.id('m2')]
          AND public.laplace_hash128_merkle(0::smallint,array_agg(v.entity_id ORDER BY v.ordinal))
             =repair_test.id('game')
          AND bool_and(v.flags=0)
          FROM laplace.physicalities p CROSS JOIN LATERAL
            public.laplace_trajectory_expanded_constituents(p.trajectory) v
          WHERE p.id=repair_test.physicality_id('game');""") == "t"
        expected_input = self.sql("""SELECT encode(ST_AsEWKB(ST_Collect(array_agg(p.coord ORDER BY v.ordinal))),'hex')
          FROM unnest(ARRAY[repair_test.id('p0'),repair_test.id('m1'),repair_test.id('m2')])
            WITH ORDINALITY v(id,ordinal)
          JOIN laplace.physicalities p ON p.entity_id=v.id AND p.type=1;""")
        line = next(row for row in rows if row["repair_kind"] == "chess-line")
        assert line["evidence"]["native_placement_input_ewkb"] == expected_input
        assert self.sql(f"""WITH input AS (SELECT ST_GeomFromEWKB(decode('{expected_input}','hex')) AS points)
          SELECT ST_AsEWKB(coord)=ST_AsEWKB(public.laplace_karcher_mean_4d(points))
            AND public.laplace_distance_4d(coord,ST_MakePoint(
              1.0/sqrt(3.0),1.0/sqrt(3.0),1.0/sqrt(3.0),0))<1e-12
            AND public.laplace_distance_4d(coord,public.laplace_centroid_4d(points))>0.1
            AND hilbert_index=public.laplace_hilbert_encode(coord)
            AND abs(public.laplace_radius_origin(coord)-1)<1e-12
          FROM laplace.physicalities CROSS JOIN input WHERE id=repair_test.physicality_id('game');""") == "t"
        assert self.sql("""SELECT array_agg(v.entity_id ORDER BY v.ordinal)=ARRAY[
            repair_test.id('msg1'),repair_test.id('msg2'),repair_test.id('msg1')]
            AND bool_and(v.flags=8)
          FROM laplace.physicalities p CROSS JOIN LATERAL
            public.laplace_trajectory_expanded_constituents(p.trajectory) v
          WHERE p.id=repair_test.physicality_id('session',3::smallint);""") == "t"
        self.completed.append("positive-native-line-player-session-exact-receipt")

        # A retained unknown submission may be reconciled as already committed;
        # it does not grant permission to rewrite either the receipt or the rows.
        prior = self.receipts / "positive" / "plan.jsonl"
        original_receipt = prior.read_bytes()
        repeat = self.run("repeat", prior=[prior], max_rows=1)
        assert repeat["applied"]["count"] == 0 and repeat["applied"]["epoch"] is None
        assert self.state() == after and prior.read_bytes() == original_receipt
        self.readback("repeat", after, self.state())
        reconciliation = self.records("repeat")[0]["prior_submission_reconciliation"]
        assert len(reconciliation) == 1 and reconciliation[0]["disposition"] == "prior-commit-confirmed"
        replay_files = list(prior.parent.glob("reconciliation-input-*.jsonl"))
        assert len(replay_files) == 1
        replay_rows = [json.loads(line) for line in replay_files[0].read_bytes().splitlines()]
        assert all(row["kind"] != "native-input" for row in replay_rows)
        assert replay_rows[-1] == {"kind": "verified-native-input-count", "count": 8,
            "source_plan_sha256": hashlib.sha256(original_receipt).hexdigest(),
            "source_plan_bytes": len(original_receipt)}
        assert replay_rows[:-1] == [row for row in records if row["kind"] != "native-input"]
        self.completed.append("repeat-no-op-and-prior-commit-reconciliation")

        self.sql("SELECT repair_test.reset();")
        retried = self.run("prior-originals-confirmed", prior=[prior])
        assert retried["applied"]["count"] == 3 and self.state() == after
        reconciliation = self.records("prior-originals-confirmed")[0]["prior_submission_reconciliation"]
        assert len(reconciliation) == 1 and reconciliation[0]["disposition"] == "originals-confirmed"
        assert prior.read_bytes() == original_receipt
        self.completed.append("prior-originals-confirmed-exact-retry")

        # A mixture of original and proposed rows is not either atomic outcome.
        # In particular a retained old Content must not hide an occupied target.
        self.sql("""SELECT repair_test.reset();
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
              n_constituents,alignment_residual,source_dim,observed_at)
          SELECT repair_test.physicality_id('player',3::smallint),entity_id,3,coord,
            hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('player');""")
        self.planning_failure("prior-original-and-proposed-coexist", prior=[prior],
                              expected_error="Unknown repair submission diverged")

        self.sql("SELECT repair_test.reset();")
        self.run("restore-proposed-for-conflict")
        self.sql("""INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,
              trajectory,n_constituents,alignment_residual,source_dim,observed_at)
          SELECT repair_test.physicality_id('player'),entity_id,1,coord,hilbert_index,
            trajectory,n_constituents,alignment_residual,source_dim,observed_at
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('player',3::smallint);""")
        self.planning_failure("prior-proposed-and-original-coexist", prior=[prior],
                              expected_error="Unknown repair submission diverged")

    def reuse_existing_projections(self) -> None:
        timestamp_fields = {"observed_at", "observed_at_binary"}
        for name, different_timestamps in (("reuse-identical-projections", False),
                                            ("reuse-projection-timestamps", True)):
            setup = PROJECTION_TARGETS
            if different_timestamps:
                setup += """UPDATE laplace.physicalities SET observed_at=observed_at+interval '2 days'
                  WHERE id IN (repair_test.physicality_id('player',3::smallint),
                    repair_test.physicality_id('session',3::smallint));"""
            self.sql("SELECT repair_test.reset();\n" + setup)
            before = self.state()
            outcome = self.run(name, max_rows=3)
            after = self.state()
            records = self.records(name)
            rows = [row for row in records if row["kind"] == "physicality"]
            assert outcome["applied"]["count"] == len(rows) == 3
            assert outcome["applied"]["rewritten_rows"] == 1
            assert outcome["applied"]["reused_projection_rows"] == 2
            assert sum(row["action"] == "reuse-existing-projection" for row in rows) == 2
            expected = dict(before["physicalities"])
            for row in rows:
                assert row["disposition"] == "eligible"
                old, proposed = row["original"], row["proposed"]
                migration = row["migration_proposal"]
                assert expected.pop(old["id"]) == old
                if row["action"] == "reuse-existing-projection":
                    assert row["repair_kind"] in {"player-projection", "session-projection"}
                    target = row["existing_target"]
                    assert target == before["physicalities"][target["id"]] == proposed
                    assert migration == old | {"id": target["id"], "type": 3}
                    assert {key: value for key, value in migration.items() if key not in timestamp_fields} == \
                           {key: value for key, value in target.items() if key not in timestamp_fields}
                    for field in timestamp_fields:
                        assert (migration[field] != target[field]) == different_timestamps
                    assert old["id"] not in after["physicalities"]
                    assert after["physicalities"][target["id"]] == target, "reuse rewrote existing target bytes"
                else:
                    assert row["action"] == "rewrite" and row["repair_kind"] == "chess-line"
                    assert row["existing_target"] is None and migration == proposed
                expected[proposed["id"]] = proposed
            assert after == {**before, "physicalities": expected}
            self.native_inputs(records, before, ["m1", "m2", "p0", "p1", "p2", "name", "msg1", "msg2"])
            self.readback(name, before, after)
            self.completed.append(name)

            prior = self.receipts / name / "plan.jsonl"
            retained_bytes = prior.read_bytes()
            replay_name = name + "-prior-poststate"
            replay = self.run(replay_name, prior=[prior], max_rows=1)
            assert replay["applied"]["count"] == 0 and replay["applied"]["epoch"] is None
            reconciliation = self.records(replay_name)[0]["prior_submission_reconciliation"]
            assert len(reconciliation) == 1 and reconciliation[0]["disposition"] == "prior-commit-confirmed"
            assert self.state() == after and prior.read_bytes() == retained_bytes
            self.readback(replay_name, after, self.state())
            self.completed.append(replay_name)

            # A reuse receipt names both exact rows in its legitimate prestate.
            # The older rewrite-receipt coexistence cases above remain rejected.
            self.sql("SELECT repair_test.reset();\n" + setup)
            assert self.state() == before
            retry_name = name + "-prior-prestate"
            retry = self.run(retry_name, prior=[prior], max_rows=3)
            reconciliation = self.records(retry_name)[0]["prior_submission_reconciliation"]
            assert retry["applied"]["count"] == 3
            assert len(reconciliation) == 1 and reconciliation[0]["disposition"] == "originals-confirmed"
            assert self.state() == after and prior.read_bytes() == retained_bytes
            self.readback(retry_name, before, self.state())
            self.completed.append(retry_name)

        self.rollback("reuse-changed-target-after-receipt", """
          UPDATE laplace.physicalities SET observed_at=observed_at+interval '1 second'
          WHERE id=repair_test.physicality_id('player',3::smallint);
          """, "Existing projection changed after its durable receipt", setup=PROJECTION_TARGETS)
        self.rollback("reuse-abort-after-content-delete", """
          CREATE FUNCTION pg_temp.abort_reuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN
            IF OLD.id=repair_test.physicality_id('player') THEN
              RAISE EXCEPTION 'Injected failure after old Content deletion';
            END IF;
            RETURN OLD;
          END $$;
          CREATE TRIGGER repair_delete_failure AFTER DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.abort_reuse_delete();
          """, "Injected failure after old Content deletion", setup=PROJECTION_TARGETS)

    def witnessed_player_alias_projection(self) -> None:
        self.sql("SELECT repair_test.reset();\n" + PLAYER_ALIAS_TARGET)
        before = self.state()
        outcome = self.run("witnessed-player-alias")
        after = self.state()
        records = self.records("witnessed-player-alias")
        self.assert_alias_retention(records, before, after)
        assert outcome["applied"]["retained_player_alias_rows"] == 1
        assert outcome["applied"]["rewritten_rows"] == 2
        assert outcome["applied"]["reused_projection_rows"] == 0
        assert records[-1]["retained_player_alias_rows"] == 1
        self.native_inputs(records, before, ["m1", "m2", "p0", "p1", "p2", "alias-old", "alias-target", "msg1", "msg2"])
        self.readback("witnessed-player-alias", before, after)
        self.completed.append("distinct-aliases-native-composition-and-exact-target-retention")

        prior = self.receipts / "witnessed-player-alias" / "plan.jsonl"
        prior_bytes = prior.read_bytes()
        repeated = self.run("witnessed-alias-prior-poststate", prior=[prior], max_rows=1)
        assert repeated["applied"]["count"] == 0 and self.state() == after
        assert self.records("witnessed-alias-prior-poststate")[0]["prior_submission_reconciliation"][0]["disposition"] == "prior-commit-confirmed"
        self.sql("SELECT repair_test.reset();\n" + PLAYER_ALIAS_TARGET)
        assert self.state() == before
        repeated = self.run("witnessed-alias-prior-prestate", prior=[prior])
        assert repeated["applied"]["count"] == 3 and self.state() == after
        assert self.records("witnessed-alias-prior-prestate")[0]["prior_submission_reconciliation"][0]["disposition"] == "originals-confirmed"
        assert prior.read_bytes() == prior_bytes
        self.completed.append("distinct-alias-prior-reconciliation-preserves-both-facts")

        self.sql("UPDATE laplace.physicalities SET observed_at=observed_at+interval '1 microsecond' WHERE id=repair_test.physicality_id('player',3::smallint);")
        self.planning_failure("witnessed-alias-prior-target-diverged", prior=[prior], expected_error="Prior witnessed player alias receipt")
        self.sql("SELECT repair_test.reset();\n" + PLAYER_ALIAS_TARGET + """
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
            n_constituents,alignment_residual,source_dim,observed_at)
          SELECT realize.canonical_id('legacy-repair-regression/additional-alias-projection'),entity_id,type,
            coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('player',3::smallint);
          """)
        self.planning_failure("witnessed-alias-prior-extra-projection", prior=[prior], expected_error="Prior witnessed player alias receipt")
        self.sql("SELECT repair_test.reset();\n" + PLAYER_ALIAS_TARGET + "DELETE FROM laplace.attestations WHERE object_id=repair_test.id('alias-target');")
        self.planning_failure("witnessed-alias-prior-proof-diverged", prior=[prior], expected_error="Prior witnessed player alias receipt")

        # The packed builder can retain a singleton LineString, but PostGIS
        # rejects its EWKB when the shared proof reconstructs the carrier.
        # This is a planning rejection before resource measurement or capture.
        self.sql("SELECT repair_test.reset();\n" + PLAYER_ALIAS_TARGET + """
          UPDATE laplace.physicalities
          SET trajectory=public.laplace_trajectory_build(ARRAY[repair_test.id('alias-target')])
          WHERE id=repair_test.physicality_id('player',3::smallint);
          """)
        name = "alias-onepoint-linestring-target"
        self.planning_failure(name, expected_error="LineString must have at least two points")
        directory = self.receipts / name
        failure = json.loads((directory / "failure.json").read_text())
        assert failure["phase_at_failure"] == "planning_and_capture"
        assert failure["received_bytes"] == failure["retained_bytes"] == 0
        for filename in ("resources.json", "resource-admission.json", "plan.jsonl",
                         "manifest.json", "submission.json", "outcome.json"):
            assert not (directory / filename).exists(), (name, filename)

        for name, mutation in (
            ("missing-target-witness", "DELETE FROM laplace.attestations WHERE object_id=repair_test.id('alias-target');"),
            ("refuted-target-witness", "UPDATE laplace.attestations SET outcome=0,sum_score_fp1e9=0 WHERE object_id=repair_test.id('alias-target');"),
            ("context-only-target-witness", "UPDATE laplace.attestations SET context_id=repair_test.id('player') WHERE object_id=repair_test.id('alias-target');"),
            ("context-only-source-witness", "UPDATE laplace.attestations SET context_id=repair_test.id('player') WHERE object_id=repair_test.id('alias-old');"),
            ("target-alignment", "UPDATE laplace.physicalities SET alignment_residual=0.5 WHERE id=repair_test.physicality_id('player',3::smallint);"),
            ("target-source-dimension", "UPDATE laplace.physicalities SET source_dim=4 WHERE id=repair_test.physicality_id('player',3::smallint);"),
            ("nonzero-target-flags", "UPDATE laplace.physicalities SET trajectory=public.laplace_mantissa_pack(repair_test.id('alias-target'),1,1,4) WHERE id=repair_test.physicality_id('player',3::smallint);"),
            ("changed-target-trajectory-srid", "UPDATE laplace.physicalities SET trajectory=ST_SetSRID(trajectory,4326) WHERE id=repair_test.physicality_id('player',3::smallint);"),
            ("changed-source-trajectory-srid", "UPDATE laplace.physicalities SET trajectory=ST_SetSRID(trajectory,4326) WHERE id=repair_test.physicality_id('player');"),
            ("shared-wrong-native-target-placement", """UPDATE laplace.physicalities SET coord=ST_MakePoint(0,0,0,0),
                 hilbert_index=public.laplace_hilbert_encode(ST_MakePoint(0,0,0,0))
                 WHERE id IN (repair_test.physicality_id('alias-target'),repair_test.physicality_id('player',3::smallint));"""),
            ("shared-wrong-native-source-placement", """UPDATE laplace.physicalities SET coord=ST_MakePoint(0,0,0,0),
                 hilbert_index=public.laplace_hilbert_encode(ST_MakePoint(0,0,0,0))
                 WHERE id IN (repair_test.physicality_id('alias-old'),repair_test.physicality_id('player'));"""),
        ):
            self.rejected("alias-" + name, PLAYER_ALIAS_TARGET + mutation, "projection-semantic-mismatch")

        self.rejected("alias-twopoint-linestring-target", PLAYER_ALIAS_TARGET + """
          UPDATE laplace.physicalities SET trajectory=ST_MakeLine(ARRAY[
            public.laplace_mantissa_pack(repair_test.id('alias-target'),1,1,0::bigint),
            public.laplace_mantissa_pack(repair_test.id('alias-target'),2,1,0::bigint)])
          WHERE id=repair_test.physicality_id('player',3::smallint);
          """, "projection-semantic-mismatch")
        assert self.sql("""SELECT ST_GeometryType(decoded)='ST_LineString'
            AND ST_NPoints(decoded)=2 AND n_constituents=1
          FROM (SELECT ST_GeomFromEWKB(ST_AsEWKB(trajectory)) AS decoded,n_constituents
            FROM laplace.physicalities
            WHERE id=repair_test.physicality_id('player',3::smallint)) target;
          """) == "t", "non-Point carrier must decode before its full-journal semantic rejection"

        self.rollback("alias-target-changed-after-receipt", """UPDATE laplace.physicalities
          SET observed_at=observed_at+interval '1 microsecond'
          WHERE id=repair_test.physicality_id('player',3::smallint);""",
          "Existing projection changed after its durable receipt", setup=PLAYER_ALIAS_TARGET)
        self.rollback("alias-target-input-changed-after-receipt", """UPDATE laplace.physicalities
          SET observed_at=observed_at+interval '1 microsecond' WHERE id=repair_test.physicality_id('alias-target');""",
          "Native input changed after its durable receipt", setup=PLAYER_ALIAS_TARGET)
        self.rollback("alias-target-witness-changed-after-receipt", """UPDATE laplace.attestations
          SET observation_count=observation_count+1,sum_score_fp1e9=sum_score_fp1e9+1000000000
          WHERE object_id=repair_test.id('alias-target');""",
          "Witnessed player alias evidence changed after its durable receipt", setup=PLAYER_ALIAS_TARGET)
        self.rollback("alias-delete-trigger-changes-target-witness", """
          CREATE FUNCTION pg_temp.change_alias_witness() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN
            UPDATE laplace.attestations SET observation_count=observation_count+1
              WHERE object_id=repair_test.id('alias-target');
            RETURN OLD;
          END $$;
          CREATE TRIGGER alias_delete_corruption AFTER DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.change_alias_witness();
          """, "Witnessed player alias evidence changed after its durable receipt", setup=PLAYER_ALIAS_TARGET)

    def native_transaction_statuses(self) -> None:
        # A finished xid only permits the following exact row reconciliation;
        # it does not authorize replay or permit restoring managed writers.
        committed = self.receipts / "witnessed-player-alias" / "plan.jsonl"
        aborted = self.receipts / "alias-target-input-changed-after-receipt" / "plan.jsonl"
        paths = [committed, aborted]
        expected_bytes = sum(path.stat().st_size for path in paths)
        budget = REPAIR.PriorReceiptBudget(expected_bytes)
        statuses = QUIESCENCE.database_submission_statuses(paths, command=self.command,
            deadline=time.monotonic() + 60, max_bytes=REPAIR.MAX_BYTES,
            max_line_bytes=REPAIR.MAX_LINE_BYTES, budget=budget)
        assert {row["receipt"]: row["status"] for row in statuses} == {
            str(committed.resolve()): "committed", str(aborted.resolve()): "aborted"}
        assert all(row["database_identity_matches"] is True for row in statuses)
        assert budget.bytes_read == expected_bytes, "native status reauthentication escaped the prior read budget"
        QUIESCENCE.require_finished_database_submissions(statuses)
        write_new_json(committed.parent / "native-transaction-statuses.json", {
            "schema": "laplace.legacy-content-native-transaction-status-proof/v1",
            "source_sha": self.source_sha, "statuses": statuses,
            "scope": "pg_xact_status on authenticated receipts and exact database identity; row reconciliation remains required"})
        self.completed.append("native-transaction-status-committed-and-aborted-receipts")

    def composite_player_alias_projection(self) -> None:
        self.sql("SELECT repair_test.reset();\n" + PLAYER_COMPOSITE_ALIAS_TARGET)
        before = self.state()
        outcome = self.run("composite-witnessed-alias")
        after = self.state()
        records = self.records("composite-witnessed-alias")
        self.assert_alias_retention(records, before, after, aliases=("λλ", "棋棋"))
        assert outcome["applied"]["retained_player_alias_rows"] == 1
        row = next(row for row in records if row.get("repair_kind") == "player-projection")
        proof = row["evidence"]["player_alias_retention"]
        for role in ("source_alias", "target_alias"):
            assert proof[role]["content"]["trajectory_ewkb"] is not None
            assert proof[role]["content"]["n_constituents"] == 2
            assert proof[role]["content_structure_valid"] is True
        pair = json.dumps({"original": row["original"], "existing_target": row["existing_target"]}).replace("'", "''")
        equality = json.loads(self.sql(f"""
          CREATE FUNCTION pg_temp.repair_snapshot(p laplace.physicalities) RETURNS jsonb
          LANGUAGE sql IMMUTABLE STRICT AS $snapshot$
            SELECT CASE WHEN p.id IS NULL THEN NULL::jsonb ELSE {REPAIR.snapshot_expression('p')} END
          $snapshot$;
          {REPAIR.player_alias_proof_sql()}
          WITH single AS MATERIALIZED (
            SELECT * FROM pg_temp.repair_player_alias_proofs(jsonb_build_array('{pair}'::jsonb))),
          repeated AS MATERIALIZED (
            SELECT * FROM pg_temp.repair_player_alias_proofs(jsonb_build_array('{pair}'::jsonb,'{pair}'::jsonb)))
          SELECT jsonb_build_object('single_count',(SELECT count(*) FROM single),
            'repeated_count',(SELECT count(*) FROM repeated),
            'valid',(SELECT bool_and(valid) FROM repeated),
            'same_evidence',(SELECT jsonb_agg(to_jsonb(single)) FROM single)=
                            (SELECT jsonb_agg(to_jsonb(repeated)) FROM repeated));
          """))
        assert equality == {"single_count": 1, "repeated_count": 1, "valid": True, "same_evidence": True}
        self.readback("composite-witnessed-alias", before, after)
        self.completed.append("composite-alias-native-identity-placement-and-duplicate-proof-stability")

    def assert_alias_retention(self, records: list[dict], before: dict, after: dict,
                               aliases: tuple[str, str] = ("λ", "棋")) -> None:
        rows = [row for row in records if row["kind"] == "physicality"]
        expected = dict(before["physicalities"])
        for row in rows:
            old, target = row["original"], row["proposed"]
            assert expected.pop(old["id"]) == old
            if row["repair_kind"] == "player-projection":
                assert row["action"] == "retain-witnessed-player-alias-projection"
                assert target == row["existing_target"] == before["physicalities"][target["id"]]
                assert row["migration_proposal"] == old | {"id": target["id"], "type": 3}
                assert old["trajectory_ewkb"] != target["trajectory_ewkb"]
                proof = row["evidence"]["player_alias_retention"]
                assert proof["original"] == old and proof["existing_target"] == target
                assert proof["source_alias"]["child_id"] != proof["target_alias"]["child_id"]
                assert {proof[role]["canonical_utf8_hex"] for role in ("source_alias", "target_alias")} == {
                    alias.encode().hex() for alias in aliases}
            expected[target["id"]] = target
        assert after == {**before, "physicalities": expected}, "alias retention changed independent evidence"

    def legacy_projection_receipt_compatibility(self) -> None:
        setup = """
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
              n_constituents,alignment_residual,source_dim,observed_at)
          SELECT public.laplace_hash128_blake3(entity_id||decode('0300','hex')),entity_id,3,
            coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,
            CASE WHEN entity_id=repair_test.id('session') THEN observed_at+interval '1 day'
                 ELSE observed_at END
          FROM laplace.physicalities WHERE id IN (repair_test.physicality_id('player'),
            repair_test.physicality_id('session'));
          """
        self.sql("SELECT repair_test.reset();" + setup)
        before = self.state()
        result = self.run("compatible-projection-retirement")
        after = self.state()
        records = self.records("compatible-projection-retirement")
        assert result["applied"]["count"] == 3
        assert result["applied"]["rewritten_rows"] == 1 and result["applied"]["reused_projection_rows"] == 2
        assert records[-1]["rewritten_rows"] == 1 and records[-1]["reused_projection_rows"] == 2
        rows = [row for row in records if row["kind"] == "physicality"]
        expected = dict(before["physicalities"])
        for row in rows:
            old, new = row["original"], row["proposed"]
            assert expected.pop(old["id"]) == old
            if row["repair_kind"] == "chess-line":
                assert row["action"] == "rewrite"
                expected[new["id"]] = new
            else:
                assert row["action"] == "reuse-existing-projection"
                assert row["evidence"]["occupied_projections"] == [new]
                assert row["existing_target"] == new
                assert new == before["physicalities"][new["id"]] == after["physicalities"][new["id"]]
                assert old["trajectory_ewkb"] == new["trajectory_ewkb"], "packed flags changed"
                if row["repair_kind"] == "session-projection":
                    assert old["observed_at_binary"] != new["observed_at_binary"], "independent observation lost"
        assert after["physicalities"] == expected
        assert after["entities"] == before["entities"] and after["attestations"] == before["attestations"]
        self.readback("compatible-projection-retirement", before, after)
        self.completed.append("compatible-projection-retirement-preserves-both-originals-and-target-time")

        # Reproduce the published 272 wire format from an authenticated fixture:
        # retain the original/target/evidence, spell the operation as 272 did,
        # and omit the additive explicit action fields. Full source bytes/counts
        # still authenticate before the locked semantic reuse checks.
        legacy_directory = self.receipts / "published-272-prior-retirement"
        legacy_directory.mkdir()
        legacy_records = []
        for record in records:
            record = dict(record)
            if record["kind"] == "physicality":
                record["operation"] = ("retire-redundant-content" if record.pop("action") == "reuse-existing-projection"
                                       else "rewrite-physicality")
                record.pop("existing_target")
                record.pop("migration_proposal")
            legacy_records.append(record)
        prior = legacy_directory / "plan.jsonl"
        prior_bytes = b"".join((json.dumps(record, sort_keys=True) + "\n").encode() for record in legacy_records)
        prior.write_bytes(prior_bytes)
        original_manifest = json.loads((self.receipts / "compatible-projection-retirement" / "manifest.json").read_text())
        manifest = original_manifest | {"plan_bytes": len(prior_bytes), "plan_sha256": hashlib.sha256(prior_bytes).hexdigest()}
        write_new_json(legacy_directory / "manifest.json", manifest)
        repeat = self.run("retirement-prior-applied", prior=[prior], max_rows=1)
        assert repeat["applied"]["count"] == 0 and self.state() == after
        assert self.records("retirement-prior-applied")[0]["prior_submission_reconciliation"][0]["disposition"] == "prior-commit-confirmed"
        self.completed.append("retirement-prior-applied-exact-target")

        self.sql("SELECT repair_test.reset();" + setup)
        assert self.state() == before
        retry = self.run("retirement-prior-originals", prior=[prior])
        assert retry["applied"]["count"] == 3 and self.state() == after
        assert self.records("retirement-prior-originals")[0]["prior_submission_reconciliation"][0]["disposition"] == "originals-confirmed"
        assert prior.read_bytes() == prior_bytes
        self.completed.append("retirement-prior-originals-both-coexisting-rows")

        self.sql("UPDATE laplace.physicalities SET observed_at=observed_at+interval '1 microsecond' WHERE id=repair_test.physicality_id('session',3::smallint);")
        self.planning_failure("retirement-prior-target-diverged", prior=[prior],
                              expected_error="Unknown repair submission diverged")
        self.sql("SELECT repair_test.reset();" + setup + "DELETE FROM laplace.physicalities WHERE id=repair_test.physicality_id('session');")
        self.planning_failure("retirement-prior-partial", prior=[prior],
                              expected_error="Unknown repair submission diverged")
        self.rollback("retirement-target-changed-after-receipt", "UPDATE laplace.physicalities SET observed_at=observed_at+interval '1 microsecond' WHERE id=repair_test.physicality_id('session',3::smallint);",
                      "Existing projection changed after its durable receipt", setup=setup)
        self.rollback("retirement-trigger-changes-native-child", """
          CREATE FUNCTION pg_temp.corrupt_native_dependency() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN
            UPDATE laplace.physicalities SET alignment_residual=0.875
            WHERE id=repair_test.physicality_id('m1');
            RETURN OLD;
          END $$;
          CREATE TRIGGER repair_retirement_dependency AFTER DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.corrupt_native_dependency();
          """, "Native input changed after its durable receipt", setup=setup)
        self.rollback("retirement-trigger-changes-owner-entity", """
          CREATE FUNCTION pg_temp.corrupt_owner_entity() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN
            UPDATE laplace.entities SET created_at=created_at+interval '1 microsecond'
            WHERE id=repair_test.id('session');
            RETURN OLD;
          END $$;
          CREATE TRIGGER repair_retirement_owner AFTER DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.corrupt_owner_entity();
          """, "Owner or position evidence changed after its durable receipt", setup=setup)
        self.rollback("retirement-trigger-changes-testimony", """
          CREATE FUNCTION pg_temp.corrupt_membership_testimony() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN
            UPDATE laplace.attestations SET observation_count=observation_count+1
            WHERE source_id=repair_test.id('source');
            RETURN OLD;
          END $$;
          CREATE TRIGGER repair_retirement_testimony AFTER DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.corrupt_membership_testimony();
          """, "Applicable testimony changed after its durable receipt", setup=setup)
        self.rollback("retirement-delete-trigger-keeps-content", """
          CREATE FUNCTION pg_temp.keep_redundant_content() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN RETURN NULL; END $$;
          CREATE TRIGGER repair_retirement_corruption BEFORE DELETE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.keep_redundant_content();
          """, "Repair updated", setup=setup)


    def one_move(self) -> None:
        self.sql("SELECT repair_test.reset();")
        self.run("one-move-base", max_rows=3)
        self.sql("""
          INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
          SELECT repair_test.id('one-move-game'),tier,type_id,first_observed_by,created_at
          FROM laplace.entities WHERE id=repair_test.id('game');
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
              n_constituents,alignment_residual,source_dim,observed_at)
          SELECT repair_test.physicality_id('one-move-game',kind::smallint),
            repair_test.id('one-move-game'),kind,coord,public.laplace_hilbert_encode(coord),
            CASE WHEN kind=1 THEN public.laplace_mantissa_pack(repair_test.id('m1'),1,1,0::bigint)
                 ELSE public.laplace_trajectory_build(ARRAY[repair_test.id('p0'),repair_test.id('p1')]) END,
            CASE WHEN kind=1 THEN 1 ELSE 2 END,0.125,8,
            '2026-09-03 03:04:05.678901+00'::timestamptz
          FROM (VALUES(1),(3)) kinds(kind)
          CROSS JOIN LATERAL (SELECT CASE WHEN kind=1 THEN public.laplace_karcher_mean_4d(
            (SELECT coord FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1')))
            ELSE ST_MakePoint(0.2,0.3,0.4,0.1) END AS coord) placement;
          INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,
              outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
          SELECT realize.canonical_id('legacy-repair-regression/one-move-setup'),
            repair_test.id('one-move-game'),type_id,object_id,source_id,repair_test.id('one-move-game'),
            outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9
          FROM laplace.attestations WHERE subject_id=repair_test.id('game')
            AND type_id=laplace.relation_type_id('HAS_SETUP');
          """)
        before = self.state()
        assert self.sql("""SELECT ST_GeometryType(trajectory)='ST_Point'
            AND ST_NPoints(trajectory)=1 AND n_constituents=1
            AND ST_AsEWKB(coord)=ST_AsEWKB(public.laplace_karcher_mean_4d(
              (SELECT coord FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1'))))
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('one-move-game');""") == "t"
        outcome = self.run("one-move-pointzm", max_rows=1)
        after = self.state()
        records = self.records("one-move-pointzm")
        rows = [row for row in records if row["kind"] == "physicality"]
        assert outcome["applied"]["count"] == len(rows) == 1
        row = rows[0]
        old, new = row["original"], row["proposed"]
        assert row["disposition"] == "eligible" and row["repair_kind"] == "chess-line"
        assert old["id"] == new["id"] and old["n_constituents"] == 1 and new["n_constituents"] == 2
        assert old["coord_ewkb"] != new["coord_ewkb"]
        expected = dict(before["physicalities"])
        assert expected.pop(old["id"]) == old
        expected[new["id"]] = new
        assert after["physicalities"] == expected
        assert after["entities"] == before["entities"] and after["attestations"] == before["attestations"]
        self.native_inputs(records, before, ["p0", "p1", "m1"])
        assert self.sql("""SELECT ST_GeometryType(p.trajectory)='ST_LineString'
            AND ST_NPoints(p.trajectory)=2 AND p.n_constituents=2
            AND (SELECT array_agg(v.entity_id ORDER BY v.ordinal)=ARRAY[
                repair_test.id('p0'),repair_test.id('m1')]
                AND public.laplace_hash128_merkle(0::smallint,array_agg(v.entity_id ORDER BY v.ordinal))
                  =repair_test.id('one-move-game')
              FROM public.laplace_trajectory_expanded_constituents(p.trajectory) v)
            AND ST_AsEWKB(p.coord)=ST_AsEWKB(public.laplace_karcher_mean_4d(ST_Collect(ARRAY[
              ST_MakePoint(1,0,0,0),ST_MakePoint(0,1,0,0)])))
            AND public.laplace_distance_4d(p.coord,ST_MakePoint(
              1.0::float8/sqrt(2.0::float8),1.0::float8/sqrt(2.0::float8),0,0))<1e-12
          FROM laplace.physicalities p WHERE p.id=repair_test.physicality_id('one-move-game');""") == "t"
        self.readback("one-move-pointzm", before, after)
        self.completed.append("one-move-pointzm-exact-karcher-and-independent-target")

    def planning_failure(self, name: str, *, expected_error: str,
                         prior: list[Path] | None = None, max_rows: int = 100) -> None:
        before = self.state()
        try:
            self.run(name, prior=prior, max_rows=max_rows)
        except RepairProtocolError:
            pass
        else:
            raise AssertionError(f"{name}: invalid plan was accepted")
        after = self.state()
        assert after == before, f"{name}: rejected planning changed rows"
        self.readback(name, before, after)
        directory = self.receipts / name
        assert expected_error in (directory / "database-errors.log").read_text()
        assert not (directory / "submission.json").exists()
        assert json.loads((directory / "failure.json").read_text())["disposition"] == "not-submitted"
        self.completed.append(name)

    def rejected(self, name: str, mutation: str, disposition: str) -> list[dict]:
        self.sql("SELECT repair_test.reset();\n" + mutation)
        before = self.state()
        try:
            self.run(name)
        except RepairProtocolError:
            pass
        else:
            raise AssertionError(f"{name}: unsafe repair was accepted")
        after = self.state()
        assert after == before, f"{name}: failed plan changed database rows"
        self.readback(name, before, after)
        records = self.records(name)
        rows = [record for record in records if record["kind"] == "physicality"]
        assert any(row["disposition"] == disposition for row in rows), (name, records)
        assert any(row["disposition"] == "eligible" for row in rows), "mixed-plan control missing"
        assert records[-1]["unresolved"] > 0
        for row in rows:
            assert row["original"] == before["physicalities"][row["original"]["id"]]
            for key in ("name_witnesses", "setup_witnesses", "membership_witnesses"):
                assert all(witness in before["attestations"] for witness in row["evidence"][key] or [])
        directory = self.receipts / name
        assert not (directory / "submission.json").exists(), "unresolved plan submitted mutation"
        assert json.loads((directory / "failure.json").read_text())["disposition"] == "not-submitted"
        self.completed.append(name)
        return records

    def rollback(self, name: str, inject: str, expected_error: str, *, setup: str = "") -> None:
        self.sql("SELECT repair_test.reset();\n" + setup)
        before = self.state()
        try:
            self.run(name, apply=inject + "\n" + REPAIR.apply_sql())
        except RepairProtocolError:
            pass
        else:
            raise AssertionError(f"{name}: invalid update committed")
        after = self.state()
        assert after == before, f"{name}: abort failed to restore complete table bytes"
        self.readback(name, before, after)
        directory = self.receipts / name
        assert expected_error in (directory / "database-errors.log").read_text()
        assert (directory / "submission.json").exists()
        assert not (directory / "outcome.json").exists()
        # The transport keeps an unacknowledged submission unknown. The separate
        # fresh connection above supplies the exact database rollback evidence.
        assert json.loads((directory / "failure.json").read_text())["disposition"] == "submission-outcome-unknown"
        self.records(name)
        self.completed.append(name)

    def negative_cases(self) -> None:
        self.sql("SELECT repair_test.reset();")
        self.planning_failure("declared-owner-envelope", max_rows=1,
                              expected_error="exceeds its declared owner envelope")
        cases = [
            ("missing-position-projection", "DELETE FROM laplace.physicalities WHERE id=repair_test.physicality_id('game',3::smallint);",
             "missing-exact-position-projection"),
            ("wrong-retained-start", "UPDATE laplace.physicalities SET trajectory=public.laplace_trajectory_build(ARRAY[repair_test.id('p1'),repair_test.id('p0'),repair_test.id('p2')]) WHERE id=repair_test.physicality_id('game',3::smallint);",
             "retained-start-does-not-recover-line-id"),
            ("conflicting-confirmed-setup", "UPDATE laplace.attestations SET object_id=repair_test.id('p1') WHERE type_id=laplace.relation_type_id('HAS_SETUP');",
             "conflicting-confirmed-setup"),
            ("missing-name-confirmation", "UPDATE laplace.attestations SET outcome=0 WHERE type_id=laplace.relation_type_id('HAS_NAME_ALIAS');",
             "missing-exact-confirmed-name"),
            ("missing-session-context", "UPDATE laplace.attestations SET context_id=repair_test.id('player') WHERE subject_id=repair_test.id('msg2');",
             "missing-exact-confirmed-session-membership"),
            ("unresolved-move", "DELETE FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1');",
             "unresolved-or-malformed-original-manifest"),
            ("invalid-native-child-hilbert", "UPDATE laplace.physicalities SET hilbert_index=decode(repeat('00',16),'hex') WHERE id=repair_test.physicality_id('m1');",
             "invalid-native-input-content"),
            ("malformed-logical-count", "UPDATE laplace.physicalities SET n_constituents=3 WHERE id=repair_test.physicality_id('game');",
             "unresolved-or-malformed-original-manifest"),
            ("nonzero-move-flags", "UPDATE laplace.physicalities SET trajectory=ST_MakeLine(ARRAY[public.laplace_mantissa_pack(repair_test.id('m1'),1,1,2),public.laplace_mantissa_pack(repair_test.id('m2'),2,1,0)]) WHERE id=repair_test.physicality_id('game');",
             "nonzero-move-occurrence-flags"),
            ("mismatched-player-projection", "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT repair_test.physicality_id('player',3::smallint),entity_id,3,coord,hilbert_index,trajectory,n_constituents,0.75,source_dim,observed_at FROM laplace.physicalities WHERE id=repair_test.physicality_id('player');",
             "projection-semantic-mismatch"),
            ("mismatched-session-occurrence-order", "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT repair_test.physicality_id('session',3::smallint),entity_id,3,coord,hilbert_index,ST_MakeLine(ARRAY[public.laplace_mantissa_pack(repair_test.id('msg1'),1,1,8),public.laplace_mantissa_pack(repair_test.id('msg1'),2,1,8),public.laplace_mantissa_pack(repair_test.id('msg2'),3,1,8)]),n_constituents,alignment_residual,source_dim,observed_at FROM laplace.physicalities WHERE id=repair_test.physicality_id('session');",
             "projection-semantic-mismatch"),
            ("occupied-noncanonical-player-projection", "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT realize.canonical_id('legacy-repair-regression/noncanonical-player-projection'),entity_id,3,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at FROM laplace.physicalities WHERE id=repair_test.physicality_id('player');",
             "occupied-projection-target"),
            ("non-descending-line", "UPDATE laplace.entities SET tier=4 WHERE id=repair_test.id('m1');",
             "non-descending-repaired-content"),
            ("unrecognized-legacy-game-placement", "UPDATE laplace.physicalities SET coord=ST_MakePoint(0.2,0.3,0.4,0.1),hilbert_index=public.laplace_hilbert_encode(ST_MakePoint(0.2,0.3,0.4,0.1)) WHERE id=repair_test.physicality_id('game');",
             "unexpected-legacy-game-placement"),
        ]
        for name, mutation, disposition in cases:
            self.rejected(name, mutation, disposition)
        for name, relation, subject, outcome, repair_kind, evidence_key, disposition in (
            ("opposing-name", "HAS_NAME_ALIAS", "player", 0, "player-projection",
             "name_witnesses", "opposing-name-testimony"),
            ("opposing-setup", "HAS_SETUP", "game", 0, "chess-line",
             "setup_witnesses", "opposing-setup-testimony"),
            ("opposing-membership", "APPEARS_IN", "msg1", 1, "session-projection",
             "membership_witnesses", "opposing-session-membership"),
        ):
            records = self.rejected(name, f"""
              INSERT INTO laplace.attestations(id,subject_id,type_id,object_id,source_id,context_id,
                  outcome,last_observed_at,observation_count,sum_score_fp1e9,opponent_rd_fp1e9)
              SELECT realize.canonical_id('legacy-repair-regression/{name}'),subject_id,type_id,
                object_id,source_id,context_id,{outcome},last_observed_at,observation_count,
                {outcome}::bigint*observation_count*500000000,opponent_rd_fp1e9
              FROM laplace.attestations WHERE subject_id=repair_test.id('{subject}')
                AND type_id=laplace.relation_type_id('{relation}');
              """, disposition)
            row = next(row for row in records if row["kind"] == "physicality"
                       and row["repair_kind"] == repair_kind)
            witnesses = row["evidence"][evidence_key]
            assert {witness["outcome"] for witness in witnesses} == {2, outcome}, \
                f"{name}: opposing applicable testimony was filtered out of retained evidence"
        incoming = self.rejected("incoming-content-dependency", """
          INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
          VALUES(repair_test.id('container'),5,realize.canonical_id('Repair_Test_Container'),
            repair_test.id('source'),'2026-09-01 12:34:56.123456+00'::timestamptz);
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
              n_constituents,alignment_residual,source_dim,observed_at)
          SELECT repair_test.physicality_id('container'),repair_test.id('container'),1,
            coord,public.laplace_hilbert_encode(coord),public.laplace_trajectory_build(ARRAY[
              repair_test.id('player'),repair_test.id('m1')]),2,0.125,4,
            '2026-09-03 03:04:05.678901+00'::timestamptz
          FROM (SELECT public.laplace_centroid_4d(ST_Collect(coord)) AS coord
            FROM laplace.physicalities WHERE id IN (repair_test.physicality_id('player'),
              repair_test.physicality_id('m1'))) placement;
          """, "incoming-content-requires-coupled-recovery")
        row = next(row for row in incoming if row["kind"] == "physicality"
                   and row["repair_kind"] == "player-projection")
        container_id = self.sql("SELECT encode(repair_test.physicality_id('container'),'hex');")
        assert self.state()["physicalities"][container_id] in row["evidence"]["incoming_content"]
        self.rollback("changed-after-receipt", "UPDATE laplace.physicalities SET alignment_residual=0.75 WHERE id=repair_test.physicality_id('player');",
                      "Original physicality changed after its durable receipt")
        self.rollback("changed-native-child-after-receipt", """
          UPDATE laplace.physicalities SET coord=ST_MakePoint(0,0,0,1),
            hilbert_index=public.laplace_hilbert_encode(ST_MakePoint(0,0,0,1))
          WHERE id=repair_test.physicality_id('m1');
          """, "Native input changed after its durable receipt")
        self.rollback("duplicate-native-child-after-receipt", """
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,
              n_constituents,alignment_residual,source_dim,observed_at)
          SELECT realize.canonical_id('legacy-repair-regression/duplicate-m1'),entity_id,type,
            coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1');
          """, "Native input changed after its durable receipt")
        self.rollback("post-update-corruption", """
          CREATE FUNCTION pg_temp.corrupt_repair() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN NEW.alignment_residual:=0.875; RETURN NEW; END $$;
          CREATE TRIGGER repair_corruption BEFORE UPDATE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.corrupt_repair();
          """, "Repair exact row readback failed")

    def hash_correct_missing_child(self) -> None:
        self.sql("SELECT repair_test.reset();")
        assert self.run("canonical-parent-control")["applied"]["count"] == 3
        self.sql("DELETE FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1');")
        before = self.state()
        name = "hash-correct-content-with-missing-child"
        try:
            self.run(name)
        except RepairProtocolError:
            pass
        else:
            raise AssertionError("hash-correct dangling Content was incorrectly accepted as healthy")
        assert self.state() == before
        self.readback(name, before, self.state())
        records = self.records(name)
        rows = [row for row in records if row["kind"] == "physicality"]
        assert len(rows) == 2
        assert all(row["disposition"] == "unresolved-or-malformed-original-manifest" for row in rows)
        assert all(row["original"] == before["physicalities"][row["original"]["id"]] for row in rows)
        assert records[-1]["unresolved"] == 2
        assert not (self.receipts / name / "submission.json").exists()
        self.completed.append("hash-correct-dangling-content-is-not-a-healthy-no-op")

    def healthy_corpus_over_envelope(self) -> None:
        self.sql("""SELECT repair_test.reset();
          CREATE TABLE repair_test.healthy_extra AS
          SELECT public.laplace_hash128_merkle(0::smallint,ids) AS id,ids,
            public.laplace_karcher_mean_4d(ST_Collect(coords)) AS coord
          FROM generate_series(3,12) repetitions
          CROSS JOIN LATERAL (SELECT ARRAY[repair_test.id('p0')]
            ||array_fill(repair_test.id('m1'),ARRAY[repetitions])
            ||ARRAY[repair_test.id('m2')] AS ids) structure
          CROSS JOIN LATERAL (SELECT array_agg(p.coord ORDER BY v.ordinal) AS coords
            FROM unnest(ids) WITH ORDINALITY v(id,ordinal)
            JOIN laplace.physicalities p ON p.entity_id=v.id AND p.type=1) placement;
          INSERT INTO laplace.entities(id,tier,type_id,first_observed_by,created_at)
          SELECT id,4,realize.canonical_id('Chess_Game'),repair_test.id('source'),
            '2026-09-01 12:34:56.123456+00'::timestamptz FROM repair_test.healthy_extra;
          INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,
              trajectory,n_constituents,alignment_residual,source_dim,observed_at)
          SELECT public.laplace_hash128_blake3(id||decode('0100','hex')),id,1,
            coord,public.laplace_hilbert_encode(coord),public.laplace_trajectory_build(ids),
            cardinality(ids),0.125,4,'2026-09-03 03:04:05.678901+00'::timestamptz
          FROM repair_test.healthy_extra;""")
        before = self.state()
        outcome = self.run("healthy-corpus-over-envelope", max_rows=3)
        assert outcome["applied"]["count"] == 3
        records = self.records("healthy-corpus-over-envelope")
        assert records[0]["typed_content_owners_screened"] == 14
        expected = dict(before["physicalities"])
        for row in records:
            if row["kind"] == "physicality":
                assert expected.pop(row["original"]["id"]) == row["original"]
                expected[row["proposed"]["id"]] = row["proposed"]
        after = self.state()
        assert after == {**before, "physicalities": expected}, "healthy corpus changed outside exact plan"
        self.readback("healthy-corpus-over-envelope", before, after)
        self.completed.append("healthy-corpus-larger-than-envelope-with-exact-residual-repair")
        repeat = self.run("healthy-corpus-no-op-over-envelope", max_rows=1)
        assert repeat["applied"]["count"] == 0
        assert self.records("healthy-corpus-no-op-over-envelope")[0]["typed_content_owners_screened"] == 12
        assert self.state() == after
        self.readback("healthy-corpus-no-op-over-envelope", after, self.state())
        self.completed.append("healthy-corpus-larger-than-envelope-no-op")
        self.sql("""DELETE FROM laplace.physicalities WHERE entity_id IN
            (SELECT id FROM repair_test.healthy_extra);
          DELETE FROM laplace.entities WHERE id IN (SELECT id FROM repair_test.healthy_extra);
          DROP TABLE repair_test.healthy_extra;""")

    def compressed_run(self) -> None:
        self.sql("""SELECT repair_test.reset();
          DELETE FROM laplace.attestations WHERE subject_id=repair_test.id('game');
          DELETE FROM laplace.physicalities WHERE entity_id=repair_test.id('game');
          DELETE FROM laplace.entities WHERE id=repair_test.id('game');
          UPDATE repair_test.ids SET id=public.laplace_hash128_merkle(0::smallint,
            ARRAY[repair_test.id('p0'),repair_test.id('m1'),repair_test.id('m1')]) WHERE name='game';
          SELECT repair_test.reset();
          UPDATE laplace.physicalities SET trajectory=public.laplace_trajectory_build(
            ARRAY[repair_test.id('m1'),repair_test.id('m1')]),
            coord=placement.old_mean,hilbert_index=public.laplace_hilbert_encode(placement.old_mean)
          FROM (SELECT public.laplace_karcher_mean_4d(ST_Collect(ARRAY[coord,coord])) AS old_mean
            FROM laplace.physicalities WHERE id=repair_test.physicality_id('m1')) placement
          WHERE id=repair_test.physicality_id('game');""")
        assert self.sql("""SELECT count(*)=1 AND bool_and(v.run_length=2)
          FROM laplace.physicalities p CROSS JOIN LATERAL
            public.laplace_trajectory_constituents(p.trajectory) v
          WHERE p.id=repair_test.physicality_id('game');""") == "t", "fixture did not exercise native RLE"
        before = self.state()
        outcome = self.run("compressed-move-run")
        assert outcome["applied"]["count"] == 3
        records = self.records("compressed-move-run")
        assert records[-1]["native_input_count"] == outcome["native_input_rows"] == 7
        self.native_inputs(records, before, ["m1", "p0", "p1", "p2", "name", "msg1", "msg2"])
        assert self.sql("""SELECT array_agg(v.entity_id ORDER BY v.ordinal)=ARRAY[
            repair_test.id('p0'),repair_test.id('m1'),repair_test.id('m1')]
            AND public.laplace_hash128_merkle(0::smallint,array_agg(v.entity_id ORDER BY v.ordinal))
              =repair_test.id('game')
          FROM laplace.physicalities p CROSS JOIN LATERAL
            public.laplace_trajectory_expanded_constituents(p.trajectory) v
          WHERE p.id=repair_test.physicality_id('game');""") == "t"
        expected_input = self.sql("""SELECT encode(ST_AsEWKB(ST_Collect(array_agg(p.coord ORDER BY v.ordinal))),'hex')
          FROM unnest(ARRAY[repair_test.id('p0'),repair_test.id('m1'),repair_test.id('m1')])
            WITH ORDINALITY v(id,ordinal)
          JOIN laplace.physicalities p ON p.entity_id=v.id AND p.type=1;""")
        line = next(row for row in records if row.get("repair_kind") == "chess-line")
        assert line["evidence"]["native_placement_input_ewkb"] == expected_input
        assert self.sql(f"""WITH input AS (SELECT ST_GeomFromEWKB(decode('{expected_input}','hex')) AS points)
          SELECT ST_AsEWKB(coord)=ST_AsEWKB(public.laplace_karcher_mean_4d(points))
            AND public.laplace_distance_4d(coord,ST_MakePoint(0.5,sqrt(3.0)/2.0,0,0))<1e-12
            AND public.laplace_distance_4d(coord,public.laplace_centroid_4d(points))>0.1
            AND ST_NPoints(points)=3 AND n_constituents=3
            AND hilbert_index=public.laplace_hilbert_encode(coord)
          FROM laplace.physicalities CROSS JOIN input WHERE id=repair_test.physicality_id('game');""") == "t"
        after = self.state()
        expected = dict(before["physicalities"])
        for row in records:
            if row["kind"] == "physicality":
                assert expected.pop(row["original"]["id"]) == row["original"]
                expected[row["proposed"]["id"]] = row["proposed"]
        assert after == {**before, "physicalities": expected}
        self.readback("compressed-move-run", before, after)
        self.completed.append("native-compressed-move-run-and-deduplicated-input-receipt")


def main() -> int:
    contracts = unittest.TextTestRunner(verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(PriorReceiptContracts))
    if not contracts.wasSuccessful():
        return 1
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pgdata", required=True, type=Path)
    parser.add_argument("--database-stem", required=True)
    parser.add_argument("--receipt-root", required=True, type=Path)
    args = parser.parse_args()
    private = args.pgdata.parent
    if not private.name.startswith("laplace-pr-db-proof.") or args.pgdata.name != "pgdata" \
            or Path(os.environ.get("PGHOST", "")).resolve() != (private / "socket").resolve() \
            or os.environ.get("PGPORT") != "55432" \
            or not re.fullmatch(r"laplace_pr_[A-Za-z0-9_]+", args.database_stem):
        parser.error("native repair regression requires pr-db-proof's isolated postmaster")
    prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18")) / "bin"
    actual = subprocess.run([str(prefix / "psql"), "-XAt", "-d", "postgres", "-v", "ON_ERROR_STOP=1",
                             "-c", "SHOW data_directory"], check=True, text=True, capture_output=True).stdout.strip()
    if Path(actual).resolve() != args.pgdata.resolve():
        parser.error("connected postmaster is not the private branch proof cluster")
    # PostgreSQL truncates identifiers at 63 bytes. Reserve the unique suffix so
    # concurrent regression clients cannot accidentally address the same DB.
    database = args.database_stem[:34] + "_repair_" + uuid.uuid4().hex[:16]
    receipts = args.receipt_root / database
    receipts.mkdir(parents=True, exist_ok=False)
    subprocess.run([str(prefix / "createdb"), database], check=True, timeout=180)
    try:
        proof = NativeRepairProof(prefix / "psql", database, receipts)
        proof.sql(FIXTURE)
        proof.measurement_only()
        proof.success()
        proof.one_move()
        proof.reuse_existing_projections()
        proof.legacy_projection_receipt_compatibility()
        proof.witnessed_player_alias_projection()
        proof.native_transaction_statuses()
        proof.composite_player_alias_projection()
        proof.negative_cases()
        proof.hash_correct_missing_child()
        proof.healthy_corpus_over_envelope()
        proof.compressed_run()
        result = {"schema": "laplace.legacy-content-native-regression/v1", "source_sha": proof.source_sha,
                  "database": database, "passed": proof.completed,
                  "canonical_database_mutations": 0, "native_postgresql_executed": True}
        write_new_json(receipts / "result.json", result)
        print(json.dumps(result), flush=True)
    except BaseException:
        print(f"Native legacy repair regression failed; exact receipts retained at {receipts}", file=sys.stderr)
        raise
    finally:
        subprocess.run([str(prefix / "dropdb"), database], check=True, timeout=180)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

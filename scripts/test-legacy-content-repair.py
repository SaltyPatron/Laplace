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
import uuid


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from lib.repair_transaction import RepairProtocolError, preserve_and_apply, write_new_json  # noqa: E402

SPEC = importlib.util.spec_from_file_location("legacy_content_repair", ROOT / "scripts/repair-legacy-content.py")
assert SPEC and SPEC.loader
REPAIR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPAIR)


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
            prior: list[Path] | None = None, max_rows: int = 100) -> dict:
        return preserve_and_apply(self.command, REPAIR.plan_sql(max_rows, prior or []),
            REPAIR.apply_sql() if apply is None else apply, self.receipts / name,
            source_sha=self.source_sha, max_rows=max_rows, timeout=180)

    def records(self, name: str) -> list[dict]:
        directory = self.receipts / name
        contents = (directory / "plan.jsonl").read_bytes()
        manifest = json.loads((directory / "manifest.json").read_text())
        assert manifest["plan_bytes"] == len(contents), "receipt byte count changed"
        assert manifest["plan_sha256"] == hashlib.sha256(contents).hexdigest(), "receipt digest changed"
        assert manifest["source_sha"] == self.source_sha, "receipt lost source identity"
        records = [json.loads(line) for line in contents.splitlines()]
        native_inputs = [row for row in records if row["kind"] == "native-input"]
        assert manifest["native_input_rows"] == len(native_inputs), "native input receipt count changed"
        assert records[-1]["native_input_count"] == len(native_inputs), "native input plan count changed"
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

    def success(self) -> None:
        before = self.state()
        # One healthy game is outside the repair envelope: the bound applies
        # after failed Content identity classification, not to all owners.
        outcome = self.run("positive", max_rows=3)
        after = self.state()
        records = self.records("positive")
        rows = [record for record in records if record["kind"] == "physicality"]
        assert outcome["disposition"] == "commit-confirmed"
        assert outcome["applied"]["count"] == 3
        assert records[-1]["native_input_count"] == outcome["native_input_rows"] == 8
        assert {row["repair_kind"] for row in rows} == {
            "chess-line", "player-projection", "session-projection"}
        self.native_inputs(records, before, ["m1", "m2", "p0", "p1", "p2", "name", "msg1", "msg2"])
        expected = dict(before["physicalities"])
        for row in rows:
            assert row["disposition"] == "eligible"
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
        assert self.sql("""SELECT ST_AsEWKB(coord)=ST_AsEWKB(
            public.laplace_karcher_mean_4d(ST_Collect(ARRAY[
              ST_MakePoint(1,0,0,0),ST_MakePoint(0,1,0,0),ST_MakePoint(0,0,1,0)])))
            AND public.laplace_distance_4d(coord,ST_MakePoint(
              1.0::float8/sqrt(3.0::float8),1.0::float8/sqrt(3.0::float8),
              1.0::float8/sqrt(3.0::float8),0))<1e-12
            AND hilbert_index=public.laplace_hilbert_encode(coord)
            AND abs(public.laplace_radius_origin(coord)-1)<1e-12
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('game');""") == "t"
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

    def rollback(self, name: str, inject: str, expected_error: str) -> None:
        self.sql("SELECT repair_test.reset();")
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
            ("malformed-logical-count", "UPDATE laplace.physicalities SET n_constituents=3 WHERE id=repair_test.physicality_id('game');",
             "unresolved-or-malformed-original-manifest"),
            ("nonzero-move-flags", "UPDATE laplace.physicalities SET trajectory=ST_MakeLine(ARRAY[public.laplace_mantissa_pack(repair_test.id('m1'),1,1,2),public.laplace_mantissa_pack(repair_test.id('m2'),2,1,0)]) WHERE id=repair_test.physicality_id('game');",
             "nonzero-move-occurrence-flags"),
            ("occupied-player-projection", "INSERT INTO laplace.physicalities(id,entity_id,type,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at) SELECT repair_test.physicality_id('player',3::smallint),entity_id,3,coord,hilbert_index,trajectory,n_constituents,alignment_residual,source_dim,observed_at FROM laplace.physicalities WHERE id=repair_test.physicality_id('player');",
             "occupied-projection-target"),
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
        self.rollback("post-update-corruption", """
          CREATE FUNCTION pg_temp.corrupt_repair() RETURNS trigger LANGUAGE plpgsql AS $$
          BEGIN NEW.alignment_residual:=0.875; RETURN NEW; END $$;
          CREATE TRIGGER repair_corruption BEFORE UPDATE ON laplace.physicalities
            FOR EACH ROW EXECUTE FUNCTION pg_temp.corrupt_repair();
          """, "Repair exact row readback failed")

    def healthy_corpus_over_envelope(self) -> None:
        self.sql("""SELECT repair_test.reset();
          CREATE TABLE repair_test.healthy_extra AS
          SELECT public.laplace_hash128_merkle(0::smallint,ids) AS id,ids,
            public.laplace_centroid_4d(ST_Collect(coords)) AS coord
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
        assert self.sql("""SELECT ST_AsEWKB(coord)=ST_AsEWKB(
            public.laplace_karcher_mean_4d(ST_Collect(ARRAY[
              ST_MakePoint(1,0,0,0),ST_MakePoint(0,1,0,0),ST_MakePoint(0,1,0,0)])))
            AND public.laplace_distance_4d(coord,ST_MakePoint(0.5,sqrt(3.0::float8)/2,0,0))<1e-12
            AND n_constituents=3 AND hilbert_index=public.laplace_hilbert_encode(coord)
          FROM laplace.physicalities WHERE id=repair_test.physicality_id('game');""") == "t"
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
        proof.success()
        proof.one_move()
        proof.negative_cases()
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

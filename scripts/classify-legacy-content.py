#!/usr/bin/env python3
"""Exhaustively classify Content identity failures without changing the database.

The native expanded trajectory reader and canonical Merkle implementation own
decoding/hashing. This cold operator audit issues one set-sized snapshot query.
Examples are bounded within each entity-type/source stratum; counts are not.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

from lib.legacy_content_snapshot import snapshot_expression

ROOT = Path(__file__).resolve().parents[1]
PROJECTION_LINEAGE_EXAMPLE_LIMIT = 10
PROJECTION_LINEAGE_CONSTITUENT_LIMIT = 4096


def load_proof():
    spec = importlib.util.spec_from_file_location(
        "laplace_live_recursive_proof", ROOT / "scripts/prove-live-recursive-substrate.py")
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def failure_ctes() -> str:
    """Complete ordered expansion, including malformed empty trajectories."""
    return """
parents AS MATERIALIZED (
  SELECT p.id AS physicality_id,p.entity_id AS parent_id,p.n_constituents,p.trajectory
  FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
),
expanded AS MATERIALIZED (
  SELECT p.physicality_id,p.parent_id,p.n_constituents,u.ordinal,u.entity_id AS child_id
  FROM parents p
  LEFT JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) u ON true
),
per_parent AS MATERIALIZED (
  SELECT physicality_id,parent_id,max(n_constituents) AS n_constituents,
         count(child_id)::bigint AS expanded_count,
         array_agg(child_id ORDER BY ordinal) FILTER (WHERE child_id IS NOT NULL) AS child_ids
  FROM expanded GROUP BY physicality_id,parent_id
),
checked AS MATERIALIZED (
  SELECT *,CASE WHEN expanded_count=1 THEN child_ids[1]
                WHEN expanded_count>1 THEN public.laplace_hash128_merkle(0::smallint,child_ids)
                ELSE NULL::bytea END AS recomputed_id
  FROM per_parent
),
failed AS MATERIALIZED (
  SELECT * FROM checked
  WHERE expanded_count<>n_constituents OR recomputed_id IS DISTINCT FROM parent_id
)
"""


def recovery_dependency_ctes() -> str:
    """Complete dependency counts before repair eligibility/resource filters.

    Packed membership supplies distinct dependency IDs; logical occurrences stay
    in the existing exhaustive identity classification. Counts do not assert
    that a recipe is eligible. Snapshot byte counts use the canonical repair
    representation and include one UTF-8 LF per native-input record.
    """
    return """
recovery_types AS MATERIALIZED (
  SELECT public.laplace_hash128_blake3('Chess_Game'::bytea) AS game,
         public.laplace_hash128_blake3('Chess_Player'::bytea) AS player,
         public.laplace_hash128_blake3('Conversation_Session'::bytea) AS session
),
recovery_failed_parents AS MATERIALIZED (
  SELECT f.physicality_id,f.parent_id,
         bool_or(e.type_id=t.game) AS is_game,
         bool_or(e.type_id=t.player) AS is_player,
         bool_or(e.type_id=t.session) AS is_session
  FROM failed f JOIN laplace.entities e ON e.id=f.parent_id
  CROSS JOIN recovery_types t WHERE e.type_id IN (t.game,t.player,t.session)
  GROUP BY f.physicality_id,f.parent_id
),
recovery_parent_ids AS MATERIALIZED (
  SELECT parent_id,bool_or(is_game) AS is_game,bool_or(is_player) AS is_player,
         bool_or(is_session) AS is_session
  FROM recovery_failed_parents GROUP BY parent_id
),
recovery_carriers AS MATERIALIZED (
  SELECT p.id,p.entity_id,p.type,p.trajectory
  FROM recovery_parent_ids owner JOIN laplace.physicalities p ON p.entity_id=owner.parent_id
  WHERE p.type=1 OR ((owner.is_game OR owner.is_player) AND p.type=3)
),
recovery_carrier_members AS MATERIALIZED (
  SELECT p.id AS carrier_id,member.child_id
  FROM recovery_carriers p CROSS JOIN LATERAL
    unnest(public.laplace_trajectory_constituent_ids(p.trajectory)) member(child_id)
),
recovery_needed_ids AS MATERIALIZED (
  SELECT DISTINCT child_id FROM recovery_carrier_members
),
recovery_needed_entities AS MATERIALIZED (
  SELECT n.child_id,count(e.id)::bigint AS entity_rows
  FROM recovery_needed_ids n LEFT JOIN laplace.entities e ON e.id=n.child_id
  GROUP BY n.child_id
),
recovery_needed_content AS MATERIALIZED (
  SELECT n.child_id,count(p.id)::bigint AS content_rows,
         count(p.id) FILTER(WHERE p.id<>public.laplace_hash128_blake3(
           n.child_id||decode('0100','hex')))::bigint AS noncanonical_content_rows
  FROM recovery_needed_ids n LEFT JOIN laplace.physicalities p ON p.entity_id=n.child_id AND p.type=1
  GROUP BY n.child_id
),
recovery_needed_rows AS MATERIALIZED (
  SELECT e.child_id,e.entity_rows,p.content_rows,p.noncanonical_content_rows
  FROM recovery_needed_entities e JOIN recovery_needed_content p USING(child_id)
),
recovery_game_projections AS MATERIALIZED (
  SELECT owner.parent_id,count(p.id)::bigint AS projection_rows,
    count(p.id) FILTER(WHERE p.id<>public.laplace_hash128_blake3(
      owner.parent_id||decode('0300','hex')))::bigint AS noncanonical_projection_rows
  FROM recovery_parent_ids owner LEFT JOIN laplace.physicalities p
    ON p.entity_id=owner.parent_id AND p.type=3
  WHERE owner.is_game GROUP BY owner.parent_id
),
recovery_projection_destinations AS MATERIALIZED (
  SELECT owner.parent_id,p.id,p.entity_id,p.type,
    p.id=public.laplace_hash128_blake3(owner.parent_id||decode('0300','hex')) AS canonical_target
  FROM recovery_parent_ids owner JOIN laplace.physicalities p
    ON (p.entity_id=owner.parent_id AND p.type=3)
       OR p.id=public.laplace_hash128_blake3(owner.parent_id||decode('0300','hex'))
  WHERE owner.is_player OR owner.is_session
),
recovery_incoming_content AS MATERIALIZED (
  SELECT p.id,p.entity_id,public.laplace_trajectory_constituent_ids(p.trajectory) AS member_ids
  FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
    AND public.laplace_trajectory_constituent_ids(p.trajectory)
        && ARRAY(SELECT parent_id FROM recovery_parent_ids)
),
recovery_incoming_matches AS MATERIALIZED (
  SELECT p.id AS container_id,owner.parent_id
  FROM recovery_incoming_content p JOIN recovery_parent_ids owner
    ON owner.parent_id=ANY(p.member_ids)
)
"""


def recovery_envelope_ctes(sample_limit: int) -> str:
    """Exact prospective bytes and binary field comparisons; never repair rows."""
    source_snapshot = snapshot_expression("source")
    target_snapshot = snapshot_expression("target")
    input_snapshot = snapshot_expression("content")
    return f"""
recovery_native_snapshot_line_sizes AS MATERIALIZED (
  SELECT octet_length(convert_to(jsonb_build_object(
      'kind','native-input','entity_id',encode(needed.child_id,'hex'),
      'entity',to_jsonb(entity),'content',{input_snapshot})::text,'UTF8'))::bigint AS line_bytes
  FROM recovery_needed_ids needed JOIN laplace.entities entity ON entity.id=needed.child_id
  JOIN laplace.physicalities content ON content.entity_id=needed.child_id AND content.type=1
),
recovery_native_snapshot_sizes AS MATERIALIZED (
  SELECT count(*) AS records,COALESCE(sum(line_bytes::numeric + 1),0) AS utf8_jsonl_bytes,
    COALESCE(max(line_bytes),0) AS maximum_record_utf8_bytes,
    COALESCE(max(line_bytes + 1),0) AS maximum_jsonl_line_utf8_bytes
  FROM recovery_native_snapshot_line_sizes
),
recovery_projection_snapshots AS MATERIALIZED (
  SELECT parent.parent_id,parent.physicality_id AS content_physicality_id,
    kind.name AS parent_kind,
    public.laplace_hash128_blake3(parent.parent_id||decode('0300','hex')) AS target_id,
    target.id IS NOT NULL AS target_occupied,
    {source_snapshot} || jsonb_build_object(
      'id',encode(public.laplace_hash128_blake3(parent.parent_id||decode('0300','hex')),'hex'),
      'type',3) AS proposed,
    {target_snapshot} AS existing
  FROM recovery_failed_parents parent
  JOIN laplace.physicalities source ON source.id=parent.physicality_id
  CROSS JOIN LATERAL (VALUES ('player',parent.is_player),('session',parent.is_session)) kind(name,in_scope)
  LEFT JOIN laplace.physicalities target
    ON target.id=public.laplace_hash128_blake3(parent.parent_id||decode('0300','hex'))
  WHERE kind.in_scope
),
recovery_projection_comparisons AS MATERIALIZED (
  SELECT *,target_occupied AND
      proposed - ARRAY['observed_at','observed_at_binary'] =
      existing - ARRAY['observed_at','observed_at_binary'] AS semantic_equivalent,
    target_occupied AND proposed->'observed_at_binary' = existing->'observed_at_binary'
      AS metadata_equivalent
  FROM recovery_projection_snapshots
),
recovery_projection_difference_examples AS MATERIALIZED (
  SELECT *,row_number() OVER(PARTITION BY parent_kind ORDER BY parent_id,content_physicality_id)
      AS example_number
  FROM recovery_projection_comparisons WHERE target_occupied AND NOT semantic_equivalent
),
recovery_projection_equivalence_by_kind AS MATERIALIZED (
  SELECT kind.name AS parent_kind,count(c.parent_id) AS proposed_physicalities,
    count(DISTINCT c.parent_id) AS parent_ids,
    count(c.parent_id) FILTER(WHERE c.target_occupied) AS occupied_target_pairs,
    count(DISTINCT c.target_id) FILTER(WHERE c.target_occupied) AS occupied_target_ids,
    count(c.parent_id) FILTER(WHERE NOT c.target_occupied) AS unoccupied_target_pairs,
    count(c.parent_id) FILTER(WHERE c.semantic_equivalent) AS semantic_equivalent_pairs,
    count(c.parent_id) FILTER(WHERE c.semantic_equivalent AND c.metadata_equivalent)
      AS semantic_and_metadata_equivalent_pairs,
    count(c.parent_id) FILTER(WHERE c.semantic_equivalent AND NOT c.metadata_equivalent)
      AS semantic_equivalent_metadata_different_pairs,
    count(c.parent_id) FILTER(WHERE c.target_occupied AND NOT c.semantic_equivalent)
      AS semantic_difference_pairs,
    count(c.parent_id) FILTER(WHERE c.target_occupied AND NOT c.metadata_equivalent)
      AS metadata_difference_pairs
  FROM (VALUES ('player'),('session')) kind(name)
  LEFT JOIN recovery_projection_comparisons c ON c.parent_kind=kind.name GROUP BY kind.name
),
recovery_projection_equivalence AS MATERIALIZED (
  SELECT stats.*,COALESCE((SELECT jsonb_agg(jsonb_build_object(
      'parent_id',encode(example.parent_id,'hex'),
      'content_physicality_id',encode(example.content_physicality_id,'hex'),
      'canonical_target_id',encode(example.target_id,'hex'),
      'target_entity_id',example.existing->'entity_id',
      'target_type',example.existing->'type',
      'metadata_equivalent',example.metadata_equivalent,
      'different_semantic_fields',ARRAY(SELECT field.key
        FROM jsonb_each(example.proposed - ARRAY['observed_at','observed_at_binary']) field
        WHERE field.value IS DISTINCT FROM example.existing->field.key ORDER BY field.key))
        ORDER BY example.example_number)
    FROM recovery_projection_difference_examples example
    WHERE example.parent_kind=stats.parent_kind AND example.example_number<={sample_limit}),'[]'::jsonb)
      AS non_equivalent_examples
  FROM recovery_projection_equivalence_by_kind stats
)
"""


def projection_conflict_lineage_ctes() -> str:
    """Exact bounded witnesses for different player name manifestations.

    This describes the observed singleton-child coordinate recipe. It neither
    elects an alias nor substitutes a mean when a retained row fails that recipe.
    """
    carrier_snapshot = snapshot_expression("carrier")
    content_snapshot = snapshot_expression("content")
    limit = PROJECTION_LINEAGE_CONSTITUENT_LIMIT
    return f"""
recovery_conflict_examples AS MATERIALIZED (
  SELECT * FROM recovery_projection_difference_examples
  WHERE parent_kind='player' AND example_number<={PROJECTION_LINEAGE_EXAMPLE_LIMIT}
),
recovery_conflict_carriers AS MATERIALIZED (
  SELECT example.parent_id,example.content_physicality_id,example.example_number,
    role.name AS role,carrier.*,{carrier_snapshot} AS snapshot,
    carrier.n_constituents BETWEEN 1 AND {limit}
      AND ST_NPoints(carrier.trajectory) BETWEEN 1 AND {limit} AS within_expansion_bound
  FROM recovery_conflict_examples example
  CROSS JOIN LATERAL (VALUES ('old-content',example.content_physicality_id),
                            ('existing-projection',example.target_id)) role(name,id)
  JOIN laplace.physicalities carrier ON carrier.id=role.id
),
recovery_conflict_packed AS MATERIALIZED (
  SELECT carrier.*,packed.packed_rows,packed.logical_count,
    COALESCE(carrier.within_expansion_bound
      AND packed.packed_rows=ST_NPoints(carrier.trajectory)
      AND packed.logical_count=carrier.n_constituents
      AND packed.first_ordinal=1 AND packed.last_ordinal=carrier.n_constituents,false) AS packed_bounds_valid
  FROM recovery_conflict_carriers carrier CROSS JOIN LATERAL (
    SELECT count(*) AS packed_rows,sum(GREATEST(item.run_length,1)) AS logical_count,
      min(item.ordinal) AS first_ordinal,
      max(item.ordinal+GREATEST(item.run_length,1)-1) AS last_ordinal
    FROM public.laplace_trajectory_constituents(
      CASE WHEN carrier.within_expansion_bound THEN carrier.trajectory ELSE NULL END) item
  ) packed
),
recovery_conflict_occurrences AS MATERIALIZED (
  SELECT carrier.parent_id,carrier.content_physicality_id,carrier.id AS carrier_id,carrier.role,
    item.ordinal,item.entity_id AS child_id,item.flags
  FROM recovery_conflict_packed carrier CROSS JOIN LATERAL
    public.laplace_trajectory_expanded_constituents(
      CASE WHEN carrier.packed_bounds_valid THEN carrier.trajectory ELSE NULL END) item
),
recovery_conflict_alias_ids AS MATERIALIZED (
  SELECT DISTINCT child_id FROM recovery_conflict_occurrences
),
recovery_conflict_alias_text AS MATERIALIZED (
  SELECT child_id,realize.reconstruct_content(child_id) AS canonical_utf8
  FROM recovery_conflict_alias_ids
),
recovery_conflict_alias_content AS MATERIALIZED (
  SELECT alias.child_id,content.id AS content_id,content.coord,content.hilbert_index,
    {content_snapshot} AS snapshot,
    jsonb_build_object(
      'canonical_content_physicality_id',content.id=public.laplace_hash128_blake3(alias.child_id||decode('0100','hex')),
      'native_coordinate_radius_bits',encode(float8send(public.laplace_radius_origin(content.coord)),'hex'),
      'native_coordinate_in_unit_ball',public.laplace_radius_origin(content.coord)<=1.0+1e-12,
      'native_hilbert',encode(public.laplace_hilbert_encode(content.coord),'hex'),
      'stored_hilbert_matches_native',content.hilbert_index=public.laplace_hilbert_encode(content.coord),
      'trajectory_is_null',content.trajectory IS NULL,
      'packed_rows',packed.packed_rows,'packed_logical_count',packed.logical_count,
      'expanded_count',proof.expanded_count,'unique_ordinals',proof.unique_ordinals,
      'ordered_native_constituent_ids',proof.ids,'ordered_native_occurrence_flags',proof.flags,
      'logical_manifest_complete',CASE WHEN content.trajectory IS NULL THEN content.n_constituents=0
        ELSE packed.valid AND proof.expanded_count=content.n_constituents
          AND proof.unique_ordinals=content.n_constituents END,
      'native_content_identity',encode(CASE WHEN proof.expanded_count=1 THEN proof.ids[1]
        WHEN proof.expanded_count>1 THEN public.laplace_hash128_merkle(0::smallint,proof.ids) END,'hex'),
      'native_content_identity_matches',CASE WHEN content.trajectory IS NULL THEN NULL
        ELSE (CASE WHEN proof.expanded_count=1 THEN proof.ids[1]
          WHEN proof.expanded_count>1 THEN public.laplace_hash128_merkle(0::smallint,proof.ids) END)=alias.child_id END,
      'atomic_identity_scope','No constituent identity is asserted for a null-trajectory atom.') AS native_checks
  FROM recovery_conflict_alias_ids alias
  JOIN laplace.physicalities content ON content.entity_id=alias.child_id AND content.type=1
  CROSS JOIN LATERAL (
    SELECT count(*) AS packed_rows,sum(GREATEST(item.run_length,1)) AS logical_count,
      COALESCE(content.n_constituents BETWEEN 1 AND {limit}
        AND ST_NPoints(content.trajectory) BETWEEN 1 AND {limit}
        AND count(*)=ST_NPoints(content.trajectory)
        AND sum(GREATEST(item.run_length,1))=content.n_constituents
        AND min(item.ordinal)=1
        AND max(item.ordinal+GREATEST(item.run_length,1)-1)=content.n_constituents,false) AS valid
    FROM public.laplace_trajectory_constituents(CASE
      WHEN content.n_constituents BETWEEN 1 AND {limit}
        AND ST_NPoints(content.trajectory) BETWEEN 1 AND {limit}
      THEN content.trajectory ELSE NULL END) item
  ) packed
  CROSS JOIN LATERAL (
    SELECT count(*) AS expanded_count,count(DISTINCT item.ordinal) AS unique_ordinals,
      array_agg(item.entity_id ORDER BY item.ordinal) AS ids,
      array_agg(item.flags ORDER BY item.ordinal) AS flags
    FROM public.laplace_trajectory_expanded_constituents(
      CASE WHEN packed.valid THEN content.trajectory ELSE NULL END) item
  ) proof
),
recovery_conflict_carrier_evidence AS MATERIALIZED (
  SELECT carrier.parent_id,carrier.content_physicality_id,carrier.role,
    jsonb_build_object('role',carrier.role,'physicality',carrier.snapshot,
      'packed_rows',carrier.packed_rows,'packed_logical_count',carrier.logical_count,
      'packed_bounds_valid',carrier.packed_bounds_valid,
      'expanded_count',proof.expanded_count,'unique_ordinals',proof.unique_ordinals,
      'logical_manifest_complete',carrier.packed_bounds_valid
        AND proof.expanded_count=carrier.n_constituents AND proof.unique_ordinals=carrier.n_constituents,
      'native_coordinate_radius_bits',encode(float8send(public.laplace_radius_origin(carrier.coord)),'hex'),
      'native_coordinate_in_unit_ball',public.laplace_radius_origin(carrier.coord)<=1.0+1e-12,
      'native_hilbert',encode(public.laplace_hilbert_encode(carrier.coord),'hex'),
      'stored_hilbert_matches_native',carrier.hilbert_index=public.laplace_hilbert_encode(carrier.coord),
      'ordered_alias_occurrences',COALESCE((SELECT jsonb_agg(jsonb_build_object(
        'ordinal',occurrence.ordinal,'entity_id',encode(occurrence.child_id,'hex'),'flags',occurrence.flags,
        'native_text',jsonb_build_object('function','realize.reconstruct_content(bytea)',
          'canonical_utf8_hex',(SELECT encode(text.canonical_utf8,'hex') FROM recovery_conflict_alias_text text
            WHERE text.child_id=occurrence.child_id),
          'reconstruction_is_null',(SELECT text.canonical_utf8 IS NULL FROM recovery_conflict_alias_text text
            WHERE text.child_id=occurrence.child_id),
          'scope','Identity-verified canonical UTF8 from the native renderer; NULL means no complete verified reconstruction. Display evidence does not elect a player alias.'),
        'entity_rows',(SELECT count(*) FROM laplace.entities entity WHERE entity.id=occurrence.child_id),
        'entities',COALESCE((SELECT jsonb_agg(to_jsonb(entity) ORDER BY to_jsonb(entity)::text)
          FROM laplace.entities entity WHERE entity.id=occurrence.child_id),'[]'::jsonb),
        'content_rows',(SELECT count(*) FROM recovery_conflict_alias_content content WHERE content.child_id=occurrence.child_id),
        'contents',COALESCE((SELECT jsonb_agg(jsonb_build_object(
          'physicality',content.snapshot,'native_checks',content.native_checks,
          'singleton_carrier_coord_matches_child',CASE WHEN carrier.n_constituents=1 AND proof.expanded_count=1
            THEN ST_AsEWKB(carrier.coord)=ST_AsEWKB(content.coord) END,
          'singleton_carrier_hilbert_matches_child',CASE WHEN carrier.n_constituents=1 AND proof.expanded_count=1
            THEN carrier.hilbert_index=content.hilbert_index END)
          ORDER BY content.content_id) FROM recovery_conflict_alias_content content
          WHERE content.child_id=occurrence.child_id),'[]'::jsonb)) ORDER BY occurrence.ordinal)
        FROM recovery_conflict_occurrences occurrence WHERE occurrence.carrier_id=carrier.id
          AND occurrence.content_physicality_id=carrier.content_physicality_id
          AND occurrence.role=carrier.role),'[]'::jsonb)) AS evidence
  FROM recovery_conflict_packed carrier CROSS JOIN LATERAL (
    SELECT count(*) AS expanded_count,count(DISTINCT occurrence.ordinal) AS unique_ordinals
    FROM recovery_conflict_occurrences occurrence WHERE occurrence.carrier_id=carrier.id
      AND occurrence.content_physicality_id=carrier.content_physicality_id
      AND occurrence.role=carrier.role
  ) proof
),
recovery_conflict_lineage AS MATERIALIZED (
  SELECT example.example_number,jsonb_build_object(
    'parent_id',encode(example.parent_id,'hex'),
    'content_physicality_id',encode(example.content_physicality_id,'hex'),
    'canonical_target_id',encode(example.target_id,'hex'),
    'semantic_equivalent',example.semantic_equivalent,
    'all_carrier_alias_occurrences_captured',(SELECT bool_and(
        COALESCE((carrier.evidence->>'logical_manifest_complete')::boolean,false))
      FROM recovery_conflict_carrier_evidence carrier WHERE carrier.parent_id=example.parent_id
        AND carrier.content_physicality_id=example.content_physicality_id),
    'owner_entities',COALESCE((SELECT jsonb_agg(to_jsonb(entity) ORDER BY to_jsonb(entity)::text)
      FROM laplace.entities entity WHERE entity.id=example.parent_id),'[]'::jsonb),
    'carriers',COALESCE((SELECT jsonb_agg(carrier.evidence ORDER BY carrier.role)
      FROM recovery_conflict_carrier_evidence carrier WHERE carrier.parent_id=example.parent_id
        AND carrier.content_physicality_id=example.content_physicality_id),'[]'::jsonb),
    'has_name_alias_testimony',COALESCE((SELECT jsonb_agg(to_jsonb(witness) ORDER BY witness.id)
      FROM laplace.attestations witness WHERE witness.subject_id=example.parent_id
        AND witness.type_id=laplace.relation_type_id('HAS_NAME_ALIAS')
        AND witness.object_id IN (SELECT occurrence.child_id FROM recovery_conflict_occurrences occurrence
          WHERE occurrence.parent_id=example.parent_id
            AND occurrence.content_physicality_id=example.content_physicality_id)),'[]'::jsonb)) AS evidence
  FROM recovery_conflict_examples example
)
"""


def projection_conflict_lineage_json() -> str:
    return f"""jsonb_build_object(
      'schema','laplace.player-projection-conflict-lineage/v1',
      'scope','Read-only diagnostic of different player Projection manifestations; no alias election, equivalence or repair admissibility is asserted. Full applicable testimony includes all outcomes and sources for the old and target ordered aliases.',
      'coordinate_recipe','ChessVocabulary.AppendPlayerPhysicality: singleton TextEntityBuilder.TryDecomposeRoot(name) ID; use the returned child x/y/z/m directly. Compare retained child Content bytes; do not substitute a mean.',
      'example_limit',{PROJECTION_LINEAGE_EXAMPLE_LIMIT},
      'logical_constituent_limit',{PROJECTION_LINEAGE_CONSTITUENT_LIMIT},
      'conflicting_pairs',(SELECT count(*) FROM recovery_projection_difference_examples WHERE parent_kind='player'),
      'captured_pairs',(SELECT count(*) FROM recovery_conflict_lineage),
      'examples_complete',(SELECT count(*) FROM recovery_conflict_lineage)=
        (SELECT count(*) FROM recovery_projection_difference_examples WHERE parent_kind='player'),
      'examples',COALESCE((SELECT jsonb_agg(evidence ORDER BY example_number)
        FROM recovery_conflict_lineage),'[]'::jsonb))"""


def recovery_dependency_json() -> str:
    return f"""jsonb_build_object(
    'schema','laplace.legacy-content-recovery-dependencies/v2',
    'scope','Complete failed typed parent dependency set before repair eligibility and resource filters; native-input JSONL bytes cover this dependency set only, not other repair receipt records. Counts and equivalence do not establish repair admissibility.',
    'failed_typed_parent_physicalities',(SELECT count(*) FROM recovery_failed_parents),
    'failed_typed_parent_ids',(SELECT count(*) FROM recovery_parent_ids),
    'carriers',jsonb_build_object(
      'physicality_rows',(SELECT count(*) FROM recovery_carriers),
      'content_rows',(SELECT count(*) FROM recovery_carriers WHERE type=1),
      'game_position_projection_rows',(SELECT count(*) FROM recovery_carriers WHERE type=3),
      'null_trajectory_rows',(SELECT count(*) FROM recovery_carriers WHERE trajectory IS NULL),
      'empty_trajectory_rows',(SELECT count(*) FROM recovery_carriers WHERE ST_IsEmpty(trajectory)),
      'distinct_carrier_child_memberships',(SELECT count(*) FROM recovery_carrier_members)),
    'native_inputs',jsonb_build_object(
      'distinct_needed_child_ids',(SELECT count(*) FROM recovery_needed_ids),
      'joined_entity_content_snapshot_rows',(SELECT COALESCE(sum(entity_rows::numeric*content_rows::numeric),0) FROM recovery_needed_rows),
      'prospective_snapshot_records',(SELECT records FROM recovery_native_snapshot_sizes),
      'prospective_snapshot_utf8_jsonl_bytes',(SELECT utf8_jsonl_bytes FROM recovery_native_snapshot_sizes),
      'maximum_snapshot_record_utf8_bytes_excluding_lf',(SELECT maximum_record_utf8_bytes FROM recovery_native_snapshot_sizes),
      'maximum_snapshot_jsonl_line_utf8_bytes_including_lf',(SELECT maximum_jsonl_line_utf8_bytes FROM recovery_native_snapshot_sizes),
      'prospective_snapshot_record_format','PostgreSQL jsonb::text {{kind:native-input,entity_id:hex,entity:to_jsonb(entity),content:canonical repair snapshot_expression}}; UTF8 bytes plus one LF per joined entity/Content row.',
      'entity_rows',(SELECT COALESCE(sum(entity_rows),0) FROM recovery_needed_rows),
      'content_rows',(SELECT COALESCE(sum(content_rows),0) FROM recovery_needed_rows),
      'missing_entity_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows=0),
      'missing_content_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE content_rows=0),
      'missing_either_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows=0 OR content_rows=0),
      'multiple_entity_row_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows>1),
      'multiple_content_row_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE content_rows>1),
      'noncanonical_content_rows',(SELECT COALESCE(sum(noncanonical_content_rows),0) FROM recovery_needed_rows)),
    'game_position_projections',jsonb_build_object(
      'game_ids',(SELECT count(*) FROM recovery_game_projections),
      'missing_projection_game_ids',(SELECT count(*) FROM recovery_game_projections WHERE projection_rows=0),
      'multiple_projection_game_ids',(SELECT count(*) FROM recovery_game_projections WHERE projection_rows>1),
      'noncanonical_projection_rows',(SELECT COALESCE(sum(noncanonical_projection_rows),0) FROM recovery_game_projections)),
    'player_session_destinations',jsonb_build_object(
      'parent_ids',(SELECT count(*) FROM recovery_parent_ids WHERE is_player OR is_session),
      'occupied_parent_ids',(SELECT count(DISTINCT parent_id) FROM recovery_projection_destinations),
      'occupied_parent_row_pairs',(SELECT count(*) FROM recovery_projection_destinations),
      'distinct_occupied_physicalities',(SELECT count(DISTINCT id) FROM recovery_projection_destinations),
      'canonical_target_rows',(SELECT count(*) FROM recovery_projection_destinations WHERE canonical_target),
      'noncanonical_projection_rows',(SELECT count(*) FROM recovery_projection_destinations WHERE NOT canonical_target),
      'canonical_target_identity_or_type_conflicts',(SELECT count(*) FROM recovery_projection_destinations
        WHERE canonical_target AND (entity_id<>parent_id OR type<>3)),
      'canonical_target_equivalence_scope','Each failed player/session Content proposal changes only canonical physicality ID and type=3; all semantic physicality fields compare through exact binary snapshots. observed_at is metadata and is counted separately. Parent kinds may overlap only if entity typing is duplicated.',
      'canonical_target_equivalence_by_kind',(SELECT jsonb_agg(to_jsonb(e) ORDER BY parent_kind)
        FROM recovery_projection_equivalence e),
      'player_projection_conflict_lineage',{projection_conflict_lineage_json()}),
    'incoming_content',jsonb_build_object(
      'container_physicalities',(SELECT count(*) FROM recovery_incoming_content),
      'container_entity_ids',(SELECT count(DISTINCT entity_id) FROM recovery_incoming_content),
      'affected_failed_parent_ids',(SELECT count(DISTINCT parent_id) FROM recovery_incoming_matches),
      'container_parent_pairs',(SELECT count(*) FROM recovery_incoming_matches))
  )"""


def classification_sql(sample_limit: int) -> str:
    if not 1 <= sample_limit <= 100:
        raise ValueError("sample_limit must be between 1 and 100")
    return "WITH " + failure_ctes() + f""",
entity_metadata AS MATERIALIZED (
  SELECT f.parent_id,count(e.id)::int AS entity_rows,
         (array_agg(e.type_id ORDER BY e.tier))[1] AS type_id,
         (array_agg(e.first_observed_by ORDER BY e.tier))[1] AS source_id,
         min(e.created_at) AS first_created_at,max(e.created_at) AS last_created_at,
         COALESCE(jsonb_agg(to_jsonb(e) ORDER BY e.tier)
             FILTER (WHERE e.id IS NOT NULL),'[]'::jsonb) AS entity_records
  FROM (SELECT DISTINCT parent_id FROM failed) f
  LEFT JOIN laplace.entities e ON e.id=f.parent_id
  GROUP BY f.parent_id
),
classified AS MATERIALIZED (
  SELECT f.*,m.entity_rows,m.type_id,m.source_id,m.first_created_at,
         m.last_created_at,m.entity_records,CASE
    WHEN m.entity_rows<>1 THEN 'missing_or_duplicate_entity'
    WHEN f.expanded_count<>f.n_constituents THEN 'expansion_count_mismatch'
    WHEN f.expanded_count=0 THEN 'empty_content_trajectory'
    WHEN m.type_id=public.laplace_hash128_blake3('Chess_Game'::bytea)
      THEN 'chess_line_identity_mismatch'
    WHEN m.type_id=public.laplace_hash128_blake3('Chess_Player'::bytea)
      AND f.expanded_count=1 THEN 'player_singleton_content'
    WHEN m.type_id=public.laplace_hash128_blake3('Conversation_Session'::bytea)
      THEN 'stable_session_content'
    ELSE 'unclassified_identity_mismatch' END AS failure_class
  FROM failed f JOIN entity_metadata m USING (parent_id)
),
numbered AS MATERIALIZED (
  SELECT *,row_number() OVER (
    PARTITION BY type_id,source_id,failure_class ORDER BY physicality_id) AS example_number
  FROM classified
),
examples AS MATERIALIZED (
  SELECT n.type_id,n.source_id,n.failure_class,n.example_number,
    jsonb_build_object(
      'parent_id',encode(n.parent_id,'hex'),
      'physicality_id',encode(n.physicality_id,'hex'),
      'recomputed_id',encode(n.recomputed_id,'hex'),
      'n_constituents',n.n_constituents,'expanded_count',n.expanded_count,
      'entity_records',n.entity_records,
      'physicality_fingerprint',encode(public.laplace_hash128_blake3(
        convert_to(to_jsonb(p)::text,'UTF8')),'hex')) AS example
  FROM numbered n JOIN laplace.physicalities p ON p.id=n.physicality_id
  WHERE n.example_number<={sample_limit}
),
strata AS MATERIALIZED (
  SELECT type_id,source_id,failure_class,count(*)::bigint AS failures,
    min(first_created_at) AS earliest_entity_created_at,
    max(last_created_at) AS latest_entity_created_at,
    min(expanded_count) AS minimum_expanded_count,
    max(expanded_count) AS maximum_expanded_count
  FROM classified GROUP BY type_id,source_id,failure_class
),
{recovery_dependency_ctes()},
{recovery_envelope_ctes(sample_limit)},
{projection_conflict_lineage_ctes()}
SELECT jsonb_build_object(
  'schema','laplace.legacy-content-classification/v1',
  'database',current_database(),'observed_at',clock_timestamp(),
  'transaction_read_only',current_setting('transaction_read_only'),
  'transaction_isolation',current_setting('transaction_isolation'),
  'server_version',current_setting('server_version'),
  'substrate_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'geometry_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_geom'),
  'parents_checked',(SELECT count(*) FROM checked),
  'identity_or_expansion_failures',(SELECT count(*) FROM failed),
  'examples_per_stratum',{sample_limit},
  'recovery_dependency_envelope',{recovery_dependency_json()},
  'strata',COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'entity_type_id',encode(s.type_id,'hex'),'source_id',encode(s.source_id,'hex'),
    'failure_class',s.failure_class,'failures',s.failures,
    'earliest_entity_created_at',s.earliest_entity_created_at,
    'latest_entity_created_at',s.latest_entity_created_at,
    'minimum_expanded_count',s.minimum_expanded_count,
    'maximum_expanded_count',s.maximum_expanded_count,
    'examples',(SELECT jsonb_agg(e.example ORDER BY e.example_number) FROM examples e
       WHERE e.type_id IS NOT DISTINCT FROM s.type_id
         AND e.source_id IS NOT DISTINCT FROM s.source_id
         AND e.failure_class=s.failure_class))
    ORDER BY s.type_id,s.source_id,s.failure_class) FROM strata s),'[]'::jsonb)
)::text;
"""


def retained_json(path: Path, value: dict) -> None:
    """Create an immutable receipt, flushing its bytes before reporting success."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def validate_recovery_envelope(envelope: dict) -> None:
    native_inputs = envelope["native_inputs"]
    if native_inputs["prospective_snapshot_records"] != native_inputs["joined_entity_content_snapshot_rows"]:
        raise RuntimeError("native-input snapshot byte scope does not cover all joined dependency rows")
    for kind in envelope["player_session_destinations"]["canonical_target_equivalence_by_kind"]:
        if kind["occupied_target_pairs"] != kind["semantic_equivalent_pairs"] + kind["semantic_difference_pairs"]:
            raise RuntimeError("canonical target equivalence counts do not cover every occupied target pair")
    destinations = envelope["player_session_destinations"]
    lineage = destinations.get("player_projection_conflict_lineage")
    if lineage is None:
        return  # Earlier immutable v2 receipts predate the additive diagnostic.
    conflicting = sum(kind["semantic_difference_pairs"]
        for kind in destinations["canonical_target_equivalence_by_kind"] if kind["parent_kind"] == "player")
    examples = lineage["examples"]
    if lineage.get("schema") != "laplace.player-projection-conflict-lineage/v1" \
            or lineage["conflicting_pairs"] != conflicting \
            or lineage["example_limit"] != PROJECTION_LINEAGE_EXAMPLE_LIMIT \
            or lineage["captured_pairs"] != len(examples) \
            or len(examples) != min(conflicting, PROJECTION_LINEAGE_EXAMPLE_LIMIT) \
            or lineage["examples_complete"] is not (len(examples) == conflicting):
        raise RuntimeError("player Projection conflict lineage does not reconcile with the complete conflict set")
    identities = set()
    for example in examples:
        identity = (example["parent_id"], example["content_physicality_id"], example["canonical_target_id"])
        if identity in identities or example["semantic_equivalent"] is not False:
            raise RuntimeError("player Projection conflict lineage repeats or relabels an example")
        identities.add(identity)
        carriers = example["carriers"]
        if len(carriers) != 2 or {carrier["role"] for carrier in carriers} != {"old-content", "existing-projection"}:
            raise RuntimeError("player Projection conflict lineage omitted an old or target physicality")
        for carrier in carriers:
            expected_id = example["content_physicality_id"] if carrier["role"] == "old-content" else example["canonical_target_id"]
            if carrier["physicality"]["id"] != expected_id \
                    or len(carrier["ordered_alias_occurrences"]) != carrier["expanded_count"]:
                raise RuntimeError("player Projection conflict lineage lost its exact row or occurrence inventory")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", nargs="?", default=os.environ.get("PGDATABASE", "laplace"))
    parser.add_argument("--examples-per-stratum", type=int, default=10)
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--emit-sql", action="store_true", help="Print the read-only query without connecting")
    args = parser.parse_args(argv)
    if args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    try:
        sql = classification_sql(args.examples_per_stratum)
    except ValueError as exc:
        parser.error(str(exc))
    sql = ("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"
           "SET LOCAL timezone='UTC';\nSET LOCAL extra_float_digits=3;\n"
           f"SET LOCAL statement_timeout='{args.timeout_seconds}s';\n" + sql + "\nCOMMIT;\n")
    if args.emit_sql:
        print(sql)
        return 0
    proof = load_proof()
    path = args.receipt or ROOT / "build/test-receipts" / f"legacy-content-classification-{time.time_ns()}.json"
    if path.exists():
        parser.error(f"receipt already exists: {path}")
    report = {
        "schema": "laplace.legacy-content-classification-run/v1",
        "source_sha": proof.git_sha(),
        "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "sql_sha256": hashlib.sha256(sql.encode()).hexdigest(),
        "started_unix_nanoseconds": time.time_ns(),
        "scope": "Read-only exhaustive Content identity classification; classes are diagnostic hypotheses, not repair authorization.",
    }
    exit_code = 1
    try:
        query_started = time.monotonic()
        proc = subprocess.run(proof.psql_argv(args.database) + ["-w"], input=sql,
            cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=args.timeout_seconds + 30)
        report["psql_elapsed_seconds"] = time.monotonic() - query_started
        report["psql_exit_code"] = proc.returncode
        if proc.returncode:
            raise RuntimeError(proc.stderr.strip() or "classification query failed")
        result = proof.parse_single_json_document(proc.stdout)
        if result.get("transaction_read_only") != "on":
            raise RuntimeError("classification did not attest a read-only transaction")
        if sum(row["failures"] for row in result["strata"]) != result["identity_or_expansion_failures"]:
            raise RuntimeError("classification stratum counts do not cover the exhaustive failure count")
        validate_recovery_envelope(result["recovery_dependency_envelope"])
        report.update(status="classified", live=result)
        exit_code = 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired, ValueError, KeyError) as exc:
        report["psql_elapsed_seconds"] = time.monotonic() - query_started
        report.update(status="failed", error=str(exc))
    report["finished_unix_nanoseconds"] = time.time_ns()
    retained_json(path, report)
    print(f"legacy Content classification: {report['status']}; receipt={path}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())

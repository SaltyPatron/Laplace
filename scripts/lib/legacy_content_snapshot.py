"""Exact legacy-repair physicality snapshot framing, shared by read-only diagnostics.

The expression is unchanged from repair-legacy-content.py at 49604f4a. The
recovery executor can import it without changing its snapshot or replay format.
"""
from __future__ import annotations

import re


def snapshot_expression(alias: str) -> str:
    if not re.fullmatch(r"[a-z_]+", alias):
        raise ValueError("snapshot alias must be a fixed SQL identifier")
    p = alias
    return f"""jsonb_build_object(
      'id',encode({p}.id,'hex'),'entity_id',encode({p}.entity_id,'hex'),'type',{p}.type,
      'coord_ewkb',encode(ST_AsEWKB({p}.coord),'hex'),
      'hilbert_index',encode({p}.hilbert_index,'hex'),
      'trajectory_ewkb',encode(ST_AsEWKB({p}.trajectory),'hex'),
      'radius_origin_bits',encode(float8send({p}.radius_origin),'hex'),
      'n_constituents',{p}.n_constituents,
      'alignment_residual_bits',encode(float8send({p}.alignment_residual),'hex'),
      'source_dim',{p}.source_dim,
      'observed_at',{p}.observed_at,
      'observed_at_binary',encode(timestamptz_send({p}.observed_at),'hex'))"""

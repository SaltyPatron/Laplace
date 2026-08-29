-- Dense model factor/testimony walks are not substrate geometry.
--
-- The model lane already folds bounded weighted testimony into consensus. Older
-- factors/structure passes also persisted the complete vocab x factor/token walk
-- as PhysicalityType.Projection trajectories. That is the payload class guarded
-- by scripts/model-payload-gate-check.py: TinyLlama alone left ~1.2 GB / 38M
-- vertices after a failed ingest.
--
-- Keep the useful S3 placement (coord + Hilbert) and the relational testimony;
-- remove only the dense calculated trajectory payload. Model sources are derived
-- from the lane's own TOKEN_MAPS_TO provenance, not a hard-coded model name/id.
-- Idempotent: a clean substrate updates zero rows.
WITH model_sources AS MATERIALIZED (
    SELECT DISTINCT a.source_id
    FROM laplace.attestations a
    WHERE a.type_id = laplace.relation_type_id('TOKEN_MAPS_TO')
),
targets AS MATERIALIZED (
    SELECT p.id
    FROM laplace.physicalities p
    JOIN laplace.entities e ON e.id = p.entity_id
    JOIN model_sources s ON s.source_id = e.first_observed_by
    WHERE p.type = 3 -- PhysicalityType.Projection
      AND p.trajectory IS NOT NULL
)
UPDATE laplace.physicalities p
SET trajectory = NULL,
    n_constituents = 0,
    source_dim = NULL
FROM targets t
WHERE p.id = t.id;

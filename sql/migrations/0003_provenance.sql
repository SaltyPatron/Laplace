-- 0003_provenance.sql — provenance attribution (Phase 2 / Track C1).
--
-- Provenance records WHO observed/asserted an entity or edge, WHEN, and
-- with what attestation context. The source itself is a substrate entity
-- (e.g., the WordNet source-entity, an AI-model source-entity), so its
-- own Glicko-2 rating from significance_source drives weighted attestation.

BEGIN;

CREATE TABLE IF NOT EXISTS provenance (
    provenance_hash   bytea       NOT NULL PRIMARY KEY,
    -- The source entity making the assertion (an entity_hash from
    -- the entity table — usually a tier >= 1 composition naming the source).
    source_hash       bytea       NOT NULL,
    -- Optional: a within-source location entity (e.g., the file/URL/section).
    location_hash     bytea,
    recorded_at       timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS provenance_by_source
    ON provenance (source_hash);

-- Many-to-many between entities and provenances (an entity can be observed
-- by many sources; a single provenance can attribute many entities).
CREATE TABLE IF NOT EXISTS entity_provenance (
    entity_hash       bytea     NOT NULL,
    entity_tier       smallint  NOT NULL,
    provenance_hash   bytea     NOT NULL,
    PRIMARY KEY (entity_hash, entity_tier, provenance_hash),
    FOREIGN KEY (entity_hash, entity_tier) REFERENCES entity (entity_hash, tier),
    FOREIGN KEY (provenance_hash)          REFERENCES provenance (provenance_hash)
);

CREATE INDEX IF NOT EXISTS entity_provenance_by_provenance
    ON entity_provenance (provenance_hash);

CREATE TABLE IF NOT EXISTS edge_provenance (
    edge_hash         bytea  NOT NULL,
    edge_type_hash    bytea  NOT NULL,
    provenance_hash   bytea  NOT NULL,
    PRIMARY KEY (edge_hash, edge_type_hash, provenance_hash),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash),
    FOREIGN KEY (provenance_hash)           REFERENCES provenance (provenance_hash)
);

CREATE INDEX IF NOT EXISTS edge_provenance_by_provenance
    ON edge_provenance (provenance_hash);

COMMIT;

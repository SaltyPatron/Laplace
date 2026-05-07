-- 0002_edge.sql — edges and edge members (Phase 2 / Track C1).
--
-- Edges are content-addressed. edge_hash = BLAKE3 of (edge_type_hash ||
-- role-ordered (role_hash, role_position, participant_hash) triples).
-- Edge type AND role are themselves substrate entities — NEVER hardcoded
-- enums. Same edge type + same role-ordered participants ⇒ same hash ⇒
-- same row, deduped across decomposers.
--
-- Hash partitioning by edge_type_hash gives even distribution and lets
-- type-restricted queries pin partitions.

BEGIN;

CREATE TABLE IF NOT EXISTS edge (
    edge_hash        bytea       NOT NULL,
    edge_type_hash   bytea       NOT NULL,  -- entity_hash of the edge-type entity
    member_count     smallint    NOT NULL CHECK (member_count > 0),
    created_at       timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (edge_hash, edge_type_hash)
)
PARTITION BY HASH (edge_type_hash);

-- 16 hash partitions for edges (configurable; powers-of-two scale well
-- with PG's hash-partition routing).
DO $$
DECLARE i int;
BEGIN
    FOR i IN 0..15 LOOP
        EXECUTE format(
            'CREATE TABLE IF NOT EXISTS edge_p%s PARTITION OF edge '
            'FOR VALUES WITH (modulus 16, remainder %s)', i, i);
    END LOOP;
END$$;

-- Edge members. role_hash is itself a substrate entity (e.g., the
-- role-entity for "subject" is the composition of [s,u,b,j,e,c,t] codepoint
-- LINESTRING). role_position is the position within that role (for n-ary
-- edges with multiple participants in the same role). participant_hash
-- references any tier of entity.
CREATE TABLE IF NOT EXISTS edge_member (
    edge_hash         bytea    NOT NULL,
    edge_type_hash    bytea    NOT NULL,
    role_hash         bytea    NOT NULL,
    role_position     smallint NOT NULL DEFAULT 0,
    participant_hash  bytea    NOT NULL,
    PRIMARY KEY (edge_hash, edge_type_hash, role_hash, role_position),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash)
);

CREATE INDEX IF NOT EXISTS edge_member_by_participant
    ON edge_member (participant_hash);

CREATE INDEX IF NOT EXISTS edge_member_by_role
    ON edge_member (role_hash);

COMMIT;

-- 0004_significance.sql — three-layer Glicko-2 significance (Phase 2 / Track C1).
--
-- Source / Entity / Edge each carry their own (mu, phi, sigma, games).
-- Same Glicko-2 arithmetic for all three layers (the math lives in the
-- native Glicko2Service, B18). The layers differ in WHO carries the rating
-- and WHICH events update it:
--   significance_source : updated when a source's assertions are corroborated
--                         or contradicted by other sources
--   significance_entity : updated when entity attestations from rated sources
--                         accumulate (rated-source attestation, not
--                         negative sampling)
--   significance_edge   : updated likewise for edges
--
-- Pre-rejected substitution: ELO. Pre-rejected: rating-from-accuracy. The
-- substrate uses RATED-SOURCE ATTESTATION exclusively.

BEGIN;

-- Source ratings — the source itself is a substrate entity, so source_hash
-- references entity.entity_hash.
CREATE TABLE IF NOT EXISTS significance_source (
    source_hash    bytea            NOT NULL PRIMARY KEY,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,  -- (350 / 173.7178), Glickman default RD
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS significance_entity (
    entity_hash    bytea            NOT NULL,
    entity_tier    smallint         NOT NULL,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (entity_hash, entity_tier),
    FOREIGN KEY (entity_hash, entity_tier) REFERENCES entity (entity_hash, tier)
);

CREATE TABLE IF NOT EXISTS significance_edge (
    edge_hash      bytea            NOT NULL,
    edge_type_hash bytea            NOT NULL,
    mu             double precision NOT NULL DEFAULT 0.0,
    phi            double precision NOT NULL DEFAULT 2.014761872416,
    sigma          double precision NOT NULL DEFAULT 0.06,
    games          integer          NOT NULL DEFAULT 0,
    last_updated   timestamptz      NOT NULL DEFAULT now(),
    PRIMARY KEY (edge_hash, edge_type_hash),
    FOREIGN KEY (edge_hash, edge_type_hash) REFERENCES edge (edge_hash, edge_type_hash)
);

COMMIT;

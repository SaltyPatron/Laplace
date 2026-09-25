CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

-- A fresh substrate holds no rows. Relation, type and trust vocabulary are
-- registry codes, not seeded entities or testimony.
SELECT count(*) AS entities FROM laplace.entities;
SELECT count(*) AS attestations FROM laplace.attestations;

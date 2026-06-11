-- B2 rebuild: recreate consensus secondary indexes after a bulk model deposit.
-- (Dropped during bulk: fresh-bulk folds are PK-only; live secondaries cost ~10-20x
-- fold throughput via random-BLAKE3 B-tree thrash. The two eff_mu expression btrees
-- MUST match eff_mu()'s inlined expression exactly — see 13_mu_law / ARCHITECTURE.)
SET maintenance_work_mem = '2GB';
CREATE INDEX IF NOT EXISTS consensus_object_btree
    ON laplace.consensus USING btree (object_id) WHERE (object_id IS NOT NULL);
CREATE INDEX IF NOT EXISTS consensus_type_btree
    ON laplace.consensus USING btree (type_id);
CREATE INDEX IF NOT EXISTS consensus_subject_type_btree
    ON laplace.consensus USING btree (subject_id, type_id);
CREATE INDEX IF NOT EXISTS consensus_eff_mu_btree
    ON laplace.consensus USING btree (((rating - (2 * rd))) DESC) WHERE (object_id IS NOT NULL);
CREATE INDEX IF NOT EXISTS consensus_subject_eff_mu_btree
    ON laplace.consensus USING btree (subject_id, ((rating - (2 * rd))) DESC) WHERE (object_id IS NOT NULL);
ANALYZE laplace.consensus;

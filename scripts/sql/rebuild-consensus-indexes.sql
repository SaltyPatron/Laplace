



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

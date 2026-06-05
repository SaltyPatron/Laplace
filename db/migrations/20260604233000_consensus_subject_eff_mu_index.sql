-- 20260604233000_consensus_subject_eff_mu_index.sql
--
-- Generation-walk read correction, same law as 20260604220000: a btree serves
-- an ORDER BY only when its expression matches the read's exactly. The
-- per-subject ranked reads (completions, consensus_out, and the LATERAL inside
-- generate_tree / generate_greedy) filter subject_id = X then order by
-- (rating - 2*rd) DESC LIMIT beam. With only subject_kind_btree the planner
-- gets equality but not order, so EVERY walk node re-sorts its subject's whole
-- fan-out: measured on laplace-dev (153.7M consensus rows),
-- generate_tree(depth 3, beam 4) = 6.4 s, completions(40) = 31 ms — against a
-- spec of "one indexed scan per node".
--
-- Fix: (subject_id, (rating - 2*rd) DESC), partial on object_id IS NOT NULL —
-- the laterals' exact shape: index condition on subject, entries pre-ordered
-- by signed effective μ, kind/cycle predicates filter the ordered stream, scan
-- stops at LIMIT. subject_kind_btree is NOT redundant and stays: kind-scoped
-- equality reads (attestation_response's subject+kind probes) are its job.
--
-- Schema-of-record: extension/laplace_substrate/sql/13_consensus.sql.in
-- (changed in the same commit). This migration converges EXISTING databases.

CREATE INDEX IF NOT EXISTS consensus_subject_eff_mu_btree
    ON laplace.consensus (subject_id, ((rating - 2*rd)) DESC)
    WHERE object_id IS NOT NULL;

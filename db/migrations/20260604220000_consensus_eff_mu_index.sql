-- 20260604220000_consensus_eff_mu_index.sql
--
-- Ranked-μ inference read correction: every consensus read orders by SIGNED
-- effective μ — (rating - 2*rd) DESC (20260603230000, "never naked μ") — but
-- the only ordering index was consensus_rating_btree (rating DESC, rd ASC).
-- A btree serves ORDER BY only when the expression matches exactly, so the
-- global ranked reads degraded to full sorts: measured on laplace-dev,
-- top_relations(5) = 17.2 s parallel seq scan + sort over 153,731,819 rows.
-- The spec for these reads is a sorted index scan (µs), and the per-subject
-- reads already get that via consensus_subject_kind_btree; only the global
-- ordering index had the stale expression.
--
-- Fix: index the read's exact expression, (rating - 2*rd) DESC (bigint math —
-- 2.0*rd float variants filter by subject+kind first and never need it),
-- partial on object_id IS NOT NULL to match the ranked relation reads' WHERE
-- (unary rows are read via subject+kind, never globally ranked). The old
-- (rating DESC, rd ASC) btree is referenced by no read (grep: comments only)
-- and is dropped — same law as the 20260604 uniqueness slimming: structures
-- that serve nothing are duplication, not safety.
--
-- Schema-of-record: extension/laplace_substrate/sql/13_consensus.sql.in +
-- 05_indexes.sql.in (changed in the same commit). This migration converges
-- EXISTING databases; fresh installs never create the stale index.

CREATE INDEX IF NOT EXISTS consensus_eff_mu_btree
    ON laplace.consensus (((rating - 2*rd)) DESC)
    WHERE object_id IS NOT NULL;

DROP INDEX IF EXISTS laplace.consensus_rating_btree;

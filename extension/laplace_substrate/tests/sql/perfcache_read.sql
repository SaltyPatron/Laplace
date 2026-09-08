-- Mapped floor identities need no database constituent lookup. The throwing
-- replacement makes an accidental closure query fail, not merely run slower.
BEGIN;
CREATE OR REPLACE FUNCTION realize.constituents_closure(
    p_roots bytea[], p_max_depth integer DEFAULT 0)
RETURNS TABLE(parent_id bytea, ordinal integer, child_id bytea,
              run_length integer, flags bigint)
LANGUAGE plpgsql STABLE AS $$
BEGIN
    RAISE EXCEPTION 'floor rendering queried the database closure';
END
$$;
SELECT realize.render_text_batch(ARRAY[
           laplace.word_id('A'), laplace.word_id(' '), laplace.word_id('狼')])
       = ARRAY['A',' ','狼'] AS floor_without_database_closure;
ROLLBACK;

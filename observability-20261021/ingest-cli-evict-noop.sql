CREATE OR REPLACE PROCEDURE ops.evict_source(
 p_source bytea,p_relations bytea[] DEFAULT NULL,p_marker_types bytea[] DEFAULT NULL,
 p_batch integer DEFAULT 100000,p_drain boolean DEFAULT true)
 LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$;
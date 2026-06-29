\set ON_ERROR_STOP on
\echo '=== hot-apply lexical peer functions ==='

CREATE OR REPLACE FUNCTION laplace.word_case_class_surface(p_word bytea)
    RETURNS text
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = laplace, public AS $$
    SELECT laplace.word_case_map_surface(p_word, 'lower')
$$;

CREATE OR REPLACE FUNCTION laplace.word_shape_peers(
        p_word bytea,
        p_frechet_max double precision DEFAULT 0.02)
    RETURNS bytea[]
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = laplace, public AS $$
    SELECT COALESCE(array_agg(k.entity_id ORDER BY k.ang, k.fr), ARRAY[]::bytea[])
    FROM (
        SELECT p2.entity_id,
               public.laplace_frechet_4d(p2.trajectory, me.trajectory) AS fr,
               public.laplace_angular_distance_4d(p2.coord, me.coord) AS ang
        FROM (
            SELECT p.trajectory, p.coord, e.type_id, p.n_constituents
            FROM laplace.physicalities p
            JOIN laplace.entities e ON e.id = p.entity_id
            WHERE p.entity_id = p_word
              AND p.type = 1
              AND p.trajectory IS NOT NULL
              AND p.coord IS NOT NULL
            ORDER BY p.id
            LIMIT 1
        ) me
        JOIN laplace.physicalities p2 ON p2.type = 1
                                   AND p2.trajectory IS NOT NULL
                                   AND p2.coord IS NOT NULL
        JOIN laplace.entities e2 ON e2.id = p2.entity_id
        WHERE e2.type_id = me.type_id
          AND p2.n_constituents = me.n_constituents
          AND p2.entity_id <> p_word
        ORDER BY p2.coord <<->> me.coord
        LIMIT 48
    ) k
    WHERE laplace.entity_exists(k.entity_id)
      AND laplace.word_case_class_surface(k.entity_id) = laplace.word_case_class_surface(p_word)
      AND (k.fr <= p_frechet_max OR k.ang <= 0.115)
$$;

CREATE OR REPLACE FUNCTION laplace.lexical_peers(p_word bytea)
    RETURNS bytea[]
    LANGUAGE sql STABLE STRICT PARALLEL SAFE
    SET search_path = laplace, public AS $$
    WITH ucd AS (
        SELECT unnest(laplace.word_case_variant_ids(p_word)) AS id
    ),
    shape AS (
        SELECT unnest(laplace.word_shape_peers(p_word, 0.02::double precision)) AS id
    ),
    merged AS (
        SELECT p_word AS id
        UNION SELECT id FROM ucd
        UNION SELECT id FROM shape WHERE id IS NOT NULL
    )
    SELECT COALESCE(array_agg(DISTINCT id ORDER BY id), ARRAY[p_word])
    FROM merged
    WHERE id IS NOT NULL
$$;

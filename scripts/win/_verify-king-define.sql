\set ON_ERROR_STOP on
\echo '=== connectivity ==='
SELECT 1 AS connectivity_ok;

\echo '=== install sanity ==='
\ir _verify-lexical-peers-installed.sql

\echo '=== seed gate ==='
SELECT laplace.entity_exists(laplace.word_id('King')) AS king_cap_seeded,
       laplace.entity_exists(laplace.word_id('king')) AS king_low_seeded,
       (SELECT count(*) FROM laplace.entities) AS entity_count;

DO $$
DECLARE
    w_cap bytea := laplace.word_id('King');
    w_low bytea := laplace.word_id('king');
    cap_seeded boolean := laplace.entity_exists(w_cap);
    low_seeded boolean := laplace.entity_exists(w_low);
    peer_count int;
    define_cap int;
    define_low int;
    lower_surface text;
BEGIN
    IF NOT cap_seeded AND NOT low_seeded THEN
        RAISE EXCEPTION 'King/king not seeded — run finish-lexical-king.cmd or seed-step wordnet + wiktionary';
    END IF;

    IF NOT low_seeded THEN
        RAISE EXCEPTION 'wordnet king missing — seed wordnet before verifying King peer reads';
    END IF;

    IF w_cap = w_low THEN
        RAISE EXCEPTION 'King and king must be distinct content-addressed ids';
    END IF;

    lower_surface := laplace.word_case_map_surface(w_cap, 'lower');
    IF lower_surface IS DISTINCT FROM 'king' THEN
        RAISE EXCEPTION 'word_case_map_surface(King,lower)= % expected king (unicode UCD edges + constituents required)', lower_surface;
    END IF;

    IF NOT (w_low = ANY(laplace.lexical_peers(w_cap))) THEN
        RAISE EXCEPTION 'king not in lexical_peers(King) — UCD case map or shape peer discovery failed';
    END IF;

    IF NOT (w_cap = ANY(laplace.lexical_peers(w_low))) THEN
        RAISE EXCEPTION 'King not in lexical_peers(king) — shape/angular peer discovery failed (UCD upper maps king→KING not King)';
    END IF;

    SELECT count(*) INTO define_cap FROM laplace.define(w_cap, 5);
    SELECT count(*) INTO define_low FROM laplace.define(w_low, 5);

    IF define_cap = 0 THEN
        RAISE EXCEPTION 'define(King) returned 0 glosses — peer-expanded read path broken';
    END IF;

    IF define_low = 0 THEN
        RAISE EXCEPTION 'define(king) returned 0 glosses — wordnet path broken';
    END IF;

    IF define_cap <> define_low THEN
        RAISE NOTICE 'define row counts differ (cap=%, low=%) — acceptable if Wiktionary-only glosses on one form', define_cap, define_low;
    END IF;

    SELECT count(DISTINCT p) INTO peer_count FROM unnest(laplace.lexical_peers(w_cap)) AS u(p);
    IF peer_count > 6 THEN
        RAISE EXCEPTION 'lexical_peers(King) too broad (% peers) — tighten shape peer threshold', peer_count;
    END IF;

    IF NOT (SELECT bool_or(reply LIKE '%monarch%' OR reply LIKE '%sovereign%' OR reply LIKE '%ruler%' OR reply LIKE '%kingdom%')
            FROM laplace.recall('define King') LIMIT 5) THEN
        RAISE EXCEPTION 'recall(define King) missing expected WordNet king glosses';
    END IF;

    RAISE NOTICE 'lexical_peers(King) count=%', peer_count;
    RAISE NOTICE 'define(King) rows=% define(king) rows=%', define_cap, define_low;
END $$;

\echo '=== resolve_topic exact identity ==='
SELECT laplace.resolve_topic('King', NULL) = laplace.word_id('King') AS topic_king_cap_exact;
SELECT laplace.resolve_topic('king', NULL) = laplace.word_id('king') AS topic_king_low_exact;

\echo '=== recall define King ==='
SELECT reply, eff_mu, witnesses FROM laplace.recall('define King') LIMIT 5;

\echo '=== recall define king ==='
SELECT reply, eff_mu, witnesses FROM laplace.recall('define king') LIMIT 5;

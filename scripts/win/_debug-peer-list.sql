\set ON_ERROR_STOP on
SELECT laplace.render_text(p) AS surface,
       laplace.word_case_map_surface(p, 'lower') AS ucd_lower
FROM unnest(laplace.lexical_peers(laplace.word_id('King'))) AS u(p)
ORDER BY 1;

SELECT laplace.render_text(p) AS surface,
       laplace.word_case_map_surface(p, 'lower') AS ucd_lower
FROM unnest(laplace.lexical_peers(laplace.word_id('king'))) AS u(p)
ORDER BY 1;

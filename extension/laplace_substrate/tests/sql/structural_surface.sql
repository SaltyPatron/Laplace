CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION laplace_geom;
CREATE EXTENSION laplace_substrate;

-- The angular direction index and the exact radius locale.
BEGIN;
INSERT INTO laplace.entities (id, tier, type_id) VALUES
    (public.blake3_send(public.laplace_text_id('test/angular/source')), 0,
     public.blake3_send(public.laplace_text_id('Type')), NULL),
    (public.blake3_send(public.laplace_text_id('test/angular/raw-close')), 42,
     public.blake3_send(public.laplace_text_id('Word')),
     public.blake3_send(public.laplace_text_id('test/angular/source'))),
    (public.blake3_send(public.laplace_text_id('test/angular/angular-close')), 42,
     public.blake3_send(public.laplace_text_id('Word')),
     public.blake3_send(public.laplace_text_id('test/angular/source')));

INSERT INTO laplace.physicalities
    (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents)
VALUES
    (public.blake3_send(public.laplace_text_id('test/angular/raw-close/physicality')),
     public.blake3_send(public.laplace_text_id('test/angular/raw-close')), 1,
     public.ST_SetSRID(public.ST_MakePoint(0.9, 0.1, 0, 0), 0),
     decode(repeat('21', 16), 'hex'), NULL, 0),
    (public.blake3_send(public.laplace_text_id('test/angular/angular-close/physicality')),
     public.blake3_send(public.laplace_text_id('test/angular/angular-close')), 1,
     public.ST_SetSRID(public.ST_MakePoint(100, 1, 0, 0), 0),
     decode(repeat('22', 16), 'hex'), NULL, 0);

SELECT pg_get_indexdef(i.indexrelid) LIKE '%laplace_direction_4d(coord)%'
       AND pg_get_expr(i.indpred, i.indrelid) LIKE '%type = 1%'
       AND pg_get_expr(i.indpred, i.indrelid) LIKE '%laplace_direction_4d(coord) IS NOT NULL%'
       AS angular_knn_index_is_partial
FROM pg_index i
WHERE i.indexrelid = 'laplace.physicalities_direction_gist'::regclass;

-- Locale counts are exact radius counts over the direction index, not an
-- estimate from an arbitrary nearest-3,000 prefix.
INSERT INTO laplace.entities (id, tier, type_id) VALUES
    (public.blake3_send(public.laplace_text_id('localeanchor')), 2, public.blake3_send(public.laplace_text_id('Word')),
     public.blake3_send(public.laplace_text_id('test/angular/source'))),
    (public.blake3_send(public.laplace_text_id('localeneighbor')), 2, public.blake3_send(public.laplace_text_id('Word')),
     public.blake3_send(public.laplace_text_id('test/angular/source')));
INSERT INTO laplace.physicalities
    (id, entity_id, type, coord, hilbert_index, trajectory, n_constituents)
VALUES
    (public.blake3_send(public.laplace_text_id('test/angular/localeanchor/physicality')),
     public.blake3_send(public.laplace_text_id('localeanchor')), 1,
     public.ST_SetSRID(public.ST_MakePoint(1, 0, 0, 0), 0),
     decode(repeat('23', 16), 'hex'), NULL, 0),
    (public.blake3_send(public.laplace_text_id('test/angular/localeneighbor/physicality')),
     public.blake3_send(public.laplace_text_id('localeneighbor')), 1,
     public.ST_SetSRID(public.ST_MakePoint(2, 0, 0, 0), 0),
     decode(repeat('24', 16), 'hex'), NULL, 0);

SELECT nearest = 0 AND within_near >= 1 AND NOT isolated
       AS structural_locale_exact_zero_radius
FROM structural.locale('localeanchor', 0);

SELECT pg_get_functiondef('structural.locale(text,double precision)'::regprocedure)
           NOT LIKE '%LIMIT 3000%'
       AND pg_get_functiondef('structural.locale(text,double precision)'::regprocedure)
           LIKE '%&&&%'
       AND pg_get_functiondef('structural.locale(text,double precision)'::regprocedure)
           LIKE '%laplace_direction_4d%'
       AS structural_locale_uses_exact_indexed_radius;
ROLLBACK;

-- structural_surface complete

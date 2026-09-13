BEGIN;

-- Exercise all current HASH leaves with canonical fixture keys. Presence must
-- preserve input ordinals, including duplicates and missing/invalid ids.
INSERT INTO laplace.physicalities (id, entity_id, type, coord, hilbert_index)
SELECT id, id, 1, ST_SetSRID(ST_MakePoint(1, 0, 0, 0), 0), id
FROM (
    SELECT laplace.word_id('physicality-probe-regression/' || n) AS id
    FROM generate_series(1, 4096) AS n
) AS fixture;

DO $$
DECLARE
    ids bytea[];
    bitmap bytea;
    correct boolean;
BEGIN
    SELECT array_agg(laplace.word_id('physicality-probe-regression/' || (n % 8192))
                     ORDER BY n DESC)
    INTO ids FROM generate_series(1, 16384) AS n;
    bitmap := laplace.physicalities_exist_bitmap(ids);
    SELECT bool_and(get_bit(bitmap, (u.ord - 1)::int) = (p.id IS NOT NULL)::int)
    INTO correct
    FROM unnest(ids) WITH ORDINALITY AS u(id, ord)
    LEFT JOIN laplace.physicalities p ON p.id = u.id;
    IF correct IS DISTINCT FROM true THEN
        RAISE EXCEPTION 'physicality presence bitmap changed input ordinals or membership';
    END IF;
    IF octet_length(laplace.physicalities_exist_bitmap(ARRAY[]::bytea[])) <> 0 THEN
        RAISE EXCEPTION 'empty physicality probe returned a nonempty bitmap';
    END IF;
    IF laplace.physicalities_exist_bitmap(ARRAY[
        decode('01', 'hex'), laplace.word_id('physicality-probe-regression/1'),
        decode('', 'hex')]) <> decode('02', 'hex') THEN
        RAISE EXCEPTION 'malformed physicality ids changed neighboring membership';
    END IF;
END
$$;

ROLLBACK;

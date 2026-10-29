-- The canonical tree's UAX word occurrences are independent of DAG altitude.
-- These reads require no seeded words, source registrations or legacy rows.
BEGIN;
DO $$
DECLARE sample text; expected text[]; words text[]; identities bytea[];
BEGIN
    FOR sample,expected IN
        SELECT * FROM (VALUES
            ('What is a cat?',ARRAY['What','is','a','cat','?']),
            ('Argentina',ARRAY['Argentina']),
            ('a a',ARRAY['a','a']),
            (U&'cafe\0301 cat',ARRAY[U&'cafe\0301','cat']),
            (E'One.\n\nTwo!',ARRAY['One','.','Two','!']),
            ('猫',ARRAY['猫']),
            ('   ',ARRAY[]::text[])
        ) cases(sample,expected)
    LOOP
        SELECT coalesce(array_agg(p.word ORDER BY p.ord),ARRAY[]::text[]),
               coalesce(array_agg(p.id ORDER BY p.ord),ARRAY[]::bytea[])
        INTO words,identities FROM converse.prompt_words(sample) p;
        IF words IS DISTINCT FROM expected THEN
            RAISE EXCEPTION 'UAX occurrence read for %: expected %, got %',sample,expected,words;
        END IF;
        FOR i IN 1..cardinality(words) LOOP
            IF identities[i] IS DISTINCT FROM public.blake3_send(public.laplace_text_id(words[i])) THEN
                RAISE EXCEPTION 'Prompt segment identity differs from standalone content: %',words[i];
            END IF;
        END LOOP;
    END LOOP;
END $$;
ROLLBACK;
SELECT 'prompt segments: words, scalar words, repetition, combining marks, punctuation and whitespace passed';

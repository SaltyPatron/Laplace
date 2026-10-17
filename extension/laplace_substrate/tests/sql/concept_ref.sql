BEGIN;

-- lexical.concept_ref (Laplace#1879): CILI input resolves to its anchor, or
-- fails loudly where it is lost; it never comes back NULL for a caller to
-- COALESCE into a word.
\set VERBOSITY terse

SELECT lexical.concept_ref('i35587') = laplace.word_id('i35587') AS ili_is_its_hash,
       lexical.concept_ref('ili:i35587') = laplace.word_id('i35587') AS cili_prefixed_name_is_the_same_ili,
       lexical.concept_ref('Self_motion') = laplace.word_id('Self_motion') AS other_is_its_hash,
       lexical.concept_ref('  ') IS NULL AS empty_is_null;

-- a WordNet 3.0 key no standing claim maps to an ILI raises, bare or prefixed
SAVEPOINT unmapped;
SELECT lexical.concept_ref('00029677-n');
ROLLBACK TO SAVEPOINT unmapped;
SELECT lexical.concept_ref('pwn30:00029677-n');
ROLLBACK TO SAVEPOINT unmapped;

-- mapped by a standing synset-key claim: the ILI the claim's subject is
INSERT INTO laplace.consensus (id, subject_id, type_id, object_id,
                               rating, rd, volatility, witness_count, last_observed_at)
SELECT laplace.consensus_id(s, t, o), s, t, o, 1700000000000, 30000000000, 60000000, 3, now()
FROM (SELECT laplace.word_id('i35587') AS s,
             laplace.relation_type_id('HAS_SYNSET_KEY') AS t,
             laplace.word_id('00029677-n') AS o) k;
SELECT lexical.concept_ref('00029677-n') = laplace.word_id('i35587') AS wn30_key_is_its_ili,
       lexical.concept_ref('pwn30:00029677-n') = laplace.word_id('i35587') AS cili_pwn30_name_is_the_same;

-- another version's offset is another key (00029677-n is i35586 in WordNet
-- 3.1): it raises rather than read as 3.0's, or as a word
SAVEPOINT other_version;
SELECT lexical.concept_ref('pwn31:00029677-n');
ROLLBACK TO SAVEPOINT other_version;
SELECT lexical.concept_ref('odwn13:06341431-n');
ROLLBACK TO SAVEPOINT other_version;
SELECT lexical.concept_ref('ili:x1');
ROLLBACK TO SAVEPOINT other_version;

ROLLBACK;

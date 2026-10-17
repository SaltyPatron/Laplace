-- Recipe-law audit: read-only counts of the defects the recipe read-path audit targets
-- (issues #1874 #1876, PRs #1872 #1878). Run before and after a fresh seed:
--   psql -X -d laplace-mono -f scripts/queries/recipe-law-audit.sql
-- Every statement is a SELECT; the session is read-only.
\set ON_ERROR_STOP 1
\pset footer off
SET default_transaction_read_only = on;
SET search_path = laplace, public;
-- A type's id is the content id of its label (entity_types.toml), so word_id names a type
-- whether or not the installed manifest still declares it (WordNet_Sense_Key, Source_Record).

\echo ====== 1. entity types: forbidden (WordNet_Sense_Key), text rows (Document), records (Source_Record) ======
SELECT t.name AS type, t.expect,
       (SELECT count(*) FROM entities e WHERE e.type_id = word_id(t.name)) AS entities
FROM (VALUES ('WordNet_Sense_Key', 'must be 0'),
             ('Document',          'only real documents (no recipe rows)'),
             ('Source_Record',     'recipe rows and file content trees'),
             ('SourceFile',        'one per file read'),
             ('Sentence',          'text')) AS t(name, expect);

\echo ====== 2. Document entities by tier (recipe rows were tier >= 2 Documents) ======
SELECT e.tier, count(*) AS documents
FROM entities e WHERE e.type_id = word_id('Document')
GROUP BY e.tier ORDER BY e.tier;

\echo ====== 3. CORRESPONDS_TO objects by type, per witness ======
\echo        (recipe witnesses are per lexicon/file, e.g. "oewn · 2025+"; a sense key recorded as
\echo         content shows up as a Sentence, Word or WordNet_Sense_Key object; must be 0)
WITH per AS (
    SELECT a.source_id, o.type_id, count(*) AS claims
    FROM attestations a
    JOIN entities o ON o.id = a.object_id
    WHERE a.type_id = relation_type_id('CORRESPONDS_TO')
    GROUP BY 1, 2
)
SELECT s.label AS witness, t.label AS object_type, p.claims
FROM per p
JOIN realize.display_label_batch((SELECT array_agg(DISTINCT source_id) FROM per)) s ON s.id = p.source_id
JOIN realize.display_label_batch((SELECT array_agg(DISTINCT type_id) FROM per)) t ON t.id = p.type_id
ORDER BY p.claims DESC LIMIT 60;

\echo ====== 4. sense-key-shaped surfaces recorded as content (CORRESPONDS_TO text objects) ======
WITH objs AS (
    SELECT DISTINCT a.object_id AS id
    FROM attestations a
    JOIN entities o ON o.id = a.object_id
    WHERE a.type_id = relation_type_id('CORRESPONDS_TO')
      AND o.type_id IN (word_id('Sentence'), word_id('Word'), word_id('WordNet_Sense_Key'))
    LIMIT 50000
), rendered AS (
    SELECT r.* FROM realize.display_label_batch((SELECT array_agg(id) FROM objs)) r
)
SELECT count(*) FILTER (WHERE label ~ '%[0-9]:[0-9]{2}:[0-9]{2}') AS sense_key_like,
       count(*) AS sampled,
       (array_agg(label) FILTER (WHERE label ~ '%[0-9]:[0-9]{2}:[0-9]{2}'))[1:5] AS examples
FROM rendered;

\echo ====== 5. display priority: text-typed entities shown as their own text (sample) ======
\echo        (a mismatch is a text entity relabelled by lexicalization or another stage; must be 0)
WITH sample AS (
    SELECT e.id FROM entities e
    WHERE e.type_id = word_id('Sentence') AND e.tier BETWEEN 2 AND 3
    LIMIT 2000
), lab AS (
    SELECT l.id, l.label FROM realize.display_label_batch((SELECT array_agg(id) FROM sample)) l
), txt AS (
    SELECT s.id, t.text
    FROM sample s, LATERAL (SELECT realize.render_batch(ARRAY[s.id]) AS r) q,
         LATERAL (SELECT q.r[1] AS text) t
)
SELECT count(*) AS sampled,
       count(*) FILTER (WHERE lab.label IS DISTINCT FROM txt.text) AS label_differs_from_text,
       (array_agg(lab.label || '  <>  ' || txt.text) FILTER (WHERE lab.label IS DISTINCT FROM txt.text))[1:5] AS examples
FROM lab JOIN txt USING (id);

\echo ====== 6. ingest runs (latest per source) ======
SELECT DISTINCT ON (source_name) source_name, status, started_at, ended_at, entities, attestations,
       left(coalesce(error, ''), 120) AS error
FROM ingest_run_journal ORDER BY source_name, started_at DESC;

\echo ====== 7. Atomic2020 claims by relation (C# decomposer; source id is its name) ======
WITH per AS (
    SELECT a.type_id, count(*) AS claims
    FROM attestations a WHERE a.source_id = source_id('Atomic2020Decomposer')
    GROUP BY 1
)
SELECT r.label AS relation, p.claims
FROM per p
JOIN realize.display_label_batch((SELECT array_agg(type_id) FROM per)) r ON r.id = p.type_id
ORDER BY p.claims DESC;

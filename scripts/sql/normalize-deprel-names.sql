-- Universal UD relation/feature naming.
--
-- UD's deprel/feature heads (DEP_*, EDEP_*, FEAT_*) are seeded by
-- RelationTypeRegistry.SeedDynamic with a content-addressed type_id but NO
-- canonical_names entry, so they are anonymous heads — unlike POS (substrate/pos/*),
-- senses (CILI), languages (ISO 639), characters (Unicode), which are universal NAMED
-- anchors. This registers the universal UD relations under the same convention
-- (substrate/type/<CANON>/v1) so the syntactic heads are knowable and fold across every
-- language/treebank onto one anchor (one nsubj, one Case).
--
-- relation_type_id(name) is the content hash SeedDynamic used, so the id matches the
-- type already in consensus; names ONLY the universal bases present (idempotent). The
-- language-specific subtypes (DEP_NSUBJ_PASS, ...) need their source labels — the durable
-- fix is SeedDynamic registering the name at seed for ALL of them; this backfills the
-- universal bases on the existing substrate.
--
-- Rerunnable:  psql -h localhost -U postgres -d laplace -f scripts/sql/normalize-deprel-names.sql
SET search_path = laplace, public;

-- universal UD deprels (v2 base relations), both plain (DEP_) and enhanced (EDEP_)
INSERT INTO canonical_names (id, name)
SELECT relation_type_id(pf.p || '_' || upper(d)),
       'substrate/type/' || pf.p || '_' || upper(d) || '/v1'
FROM (VALUES ('DEP'), ('EDEP')) AS pf(p)
CROSS JOIN unnest(ARRAY[
  'acl','advcl','advmod','amod','appos','aux','case','cc','ccomp','clf','compound',
  'conj','cop','csubj','dep','det','discourse','dislocated','expl','fixed','flat',
  'goeswith','iobj','list','mark','nmod','nsubj','nummod','obj','obl','orphan',
  'parataxis','punct','root','vocative','xcomp']) AS d
WHERE EXISTS (SELECT 1 FROM consensus c
              WHERE c.type_id = relation_type_id(pf.p || '_' || upper(d)))
ON CONFLICT (id) DO NOTHING;

-- universal UD features (HAS-FEATURE values are FEAT_<NAME>)
INSERT INTO canonical_names (id, name)
SELECT relation_type_id('FEAT_' || upper(f)),
       'substrate/type/FEAT_' || upper(f) || '/v1'
FROM unnest(ARRAY[
  'Case','Number','Gender','Tense','Person','Mood','VerbForm','Aspect','Voice',
  'Definite','PronType','NumType','Poss','Reflex','Degree','Polarity','Animacy',
  'Foreign','Abbr','Typo','AdpType','ConjType','NameType','Style','Variant',
  'PunctType','PunctSide','NounType']) AS f
WHERE EXISTS (SELECT 1 FROM consensus c
              WHERE c.type_id = relation_type_id('FEAT_' || upper(f)))
ON CONFLICT (id) DO NOTHING;

\echo '=== coverage: dynamic (unranked) types now NAMED vs still anonymous ==='
WITH g AS (SELECT DISTINCT type_id FROM consensus WHERE type_id IS NOT NULL)
SELECT count(*) FILTER (WHERE relation_rank(type_id) IS NULL) AS dynamic_types,
       count(*) FILTER (WHERE relation_rank(type_id) IS NULL
                        AND EXISTS (SELECT 1 FROM canonical_names cn WHERE cn.id = type_id)) AS now_named,
       sum(CASE WHEN relation_rank(type_id) IS NULL
               AND NOT EXISTS (SELECT 1 FROM canonical_names cn WHERE cn.id = type_id)
               THEN 1 ELSE 0 END) AS still_anonymous
FROM g;

\echo '=== spot check: a named syntactic head resolves now ==='
SELECT (SELECT name FROM canonical_names WHERE id = relation_type_id('DEP_NSUBJ')) AS dep_nsubj_name,
       render(relation_type_id('DEP_NSUBJ')) AS dep_nsubj_render;




SET search_path = laplace, public;
\pset pager off

\echo '== no surviving content/concept blob names (synset/sense/class/roleset/frame) =='
SELECT count(*) AS leftover_blob_names FROM canonical_names
 WHERE name LIKE 'wordnet/synset/%' OR name LIKE 'wordnet/sense/%'
    OR name LIKE 'verbnet/class/%'  OR name LIKE 'propbank/roleset/%'
    OR name LIKE 'framenet/frame/%';

\echo '== category anchors per resource (IS_A <type>) =='
SELECT
 (SELECT count(DISTINCT subject_id) FROM consensus WHERE type_id=relation_type_id('IS_A')
    AND object_id=canonical_id('WordNet_Synset'))   AS synsets,
 (SELECT count(DISTINCT subject_id) FROM consensus WHERE type_id=relation_type_id('IS_A')
    AND object_id=canonical_id('WordNet_Sense'))    AS senses,
 (SELECT count(DISTINCT subject_id) FROM consensus WHERE type_id=relation_type_id('IS_A')
    AND object_id=canonical_id('VerbNet_Class'))    AS verbnet_classes,
 (SELECT count(DISTINCT subject_id) FROM consensus WHERE type_id=relation_type_id('IS_A')
    AND object_id=canonical_id('PropBank_Roleset')) AS propbank_rolesets,
 (SELECT count(DISTINCT subject_id) FROM consensus WHERE type_id=relation_type_id('IS_A')
    AND object_id=canonical_id('FrameNet_Frame'))   AS framenet_frames;

\echo '== synset: identity reads back as the ILI (supermodel -> i93445); WN+OMW share the anchor =='
SELECT laplace.render(sn.synset_id) AS ili_readback FROM senses(word_id('supermodel')) sn;
SELECT count(*) AS synsets_with_wn_and_omw FROM (
  SELECT s.subject_id FROM consensus s
  WHERE s.type_id=relation_type_id('IS_A') AND s.object_id=canonical_id('WordNet_Synset')
    AND EXISTS (SELECT 1 FROM consensus w WHERE w.object_id=s.subject_id AND w.type_id=relation_type_id('IS_SYNONYM_OF'))
    AND EXISTS (SELECT 1 FROM consensus o WHERE o.object_id=s.subject_id AND o.type_id=relation_type_id('IS_TRANSLATION_OF'))
) x;

\echo '== cross-resource CLASS convergence: VerbNet_Class anchors that also carry a CORRESPONDS_TO (PropBank/SemLink) =='
SELECT count(*) AS converged_classes FROM (
  SELECT s.subject_id FROM consensus s
  WHERE s.type_id=relation_type_id('IS_A') AND s.object_id=canonical_id('VerbNet_Class')
    AND EXISTS (SELECT 1 FROM consensus c
                 WHERE (c.object_id=s.subject_id OR c.subject_id=s.subject_id)
                   AND c.type_id=relation_type_id('CORRESPONDS_TO'))
) x;

\echo '== VerbNet -> WordNet via the SHARED SENSE anchor: senses with BOTH a VerbNet CORRESPONDS_TO (in) and a WordNet IS_SENSE_OF (out) =='
SELECT count(*) AS verbnet_to_wordnet_via_sense FROM (
  SELECT s.subject_id FROM consensus s
  WHERE s.type_id=relation_type_id('IS_A') AND s.object_id=canonical_id('WordNet_Sense')
    AND EXISTS (SELECT 1 FROM consensus v WHERE v.object_id=s.subject_id AND v.type_id=relation_type_id('CORRESPONDS_TO'))
    AND EXISTS (SELECT 1 FROM consensus w WHERE w.subject_id=s.subject_id AND w.type_id=relation_type_id('IS_SENSE_OF'))
) x;

\echo '== STRUCTURAL blob scan: entities with NO physicality (identity = bare hash, NOT decomposed =='
\echo '== content) grouped by type. Closed bootstrap anchors (substrate/type|source|trust_class,    =='
\echo '== ordinal, relation_type) are legitimately content-free; ANY OTHER type here is a surviving  =='
\echo '== blob -- e.g. Ud_Xpos / Ud_Feature (#16), or any *_Synset/_Sense if the de-blob regressed.  =='
SELECT laplace.render(e.type_id) AS entity_type, count(*) AS content_free
  FROM entities e
 WHERE NOT EXISTS (SELECT 1 FROM physicalities p WHERE p.entity_id = e.id)
 GROUP BY e.type_id
 ORDER BY content_free DESC
 LIMIT 50;

\echo '== name-registered leftover content blobs across the remaining targets (#16/#26) =='
SELECT count(*) FILTER (WHERE name LIKE 'xpos:%')            AS ud_xpos_blob,
       count(*) FILTER (WHERE name LIKE 'featval:%')         AS ud_featval_blob,
       count(*) FILTER (WHERE name LIKE 'tatoeba/sentence/%')AS tatoeba_sentence_blob
  FROM canonical_names;

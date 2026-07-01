SET search_path = laplace, public;

SELECT 'entities' AS k, count(*)::bigint AS n FROM entities
UNION ALL SELECT 'physicalities', count(*)::bigint FROM physicalities
UNION ALL SELECT 'attestations', count(*)::bigint FROM attestations
UNION ALL SELECT 'consensus', count(*)::bigint FROM consensus
ORDER BY 1;

SELECT source, evidence, content FROM source_counts()
WHERE source IN (
  'UnicodeDecomposer', 'ISO639Decomposer', 'CILIDecomposer', 'WordNetDecomposer',
  'VerbNetDecomposer', 'PropBankDecomposer', 'FrameNetDecomposer', 'MapNetDecomposer',
  'WordFrameNetDecomposer', 'SemLinkDecomposer', 'UserPrompt'
)
ORDER BY 1;

SELECT step, layer_order, source_name, layer_complete
FROM (
  VALUES
    ('unicode',       0, 'UnicodeDecomposer'),
    ('iso639',        1, 'ISO639Decomposer'),
    ('cili',          2, 'CILIDecomposer'),
    ('wordnet',       2, 'WordNetDecomposer'),
    ('verbnet',       2, 'VerbNetDecomposer'),
    ('propbank',      2, 'PropBankDecomposer'),
    ('framenet',      3, 'FrameNetDecomposer'),
    ('mapnet',        3, 'MapNetDecomposer'),
    ('wordframenet',  3, 'WordFrameNetDecomposer'),
    ('semlink',       3, 'SemLinkDecomposer'),
    ('document',      2, 'UserPrompt')
) AS t(step, layer_order, source_name)
CROSS JOIN LATERAL (
  SELECT laplace.evidence_count(
           laplace.canonical_id('substrate/type/HasLayerCompleted/' || t.layer_order::text || '/v1'),
           laplace.source_id(t.source_name)) > 0 AS layer_complete
) m
ORDER BY 1;

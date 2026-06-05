-- 20260605040000_tokenizer_meta_tier.sql
--
-- The Model_Tokenizer entity was the one writer site the 20260605020000
-- tier-law sweep missed (ModelDecomposer.cs:247 stamped tier 0 on the
-- tokenizer-file entity; fixed to MetaTier.Meta in the same commit).
-- Measured: exactly one tier-0 squatter after the TinyLlama ingest —
-- anchor purity restored by this heal.

UPDATE laplace.entities e
SET    tier = 250
WHERE  e.type_id = laplace.canonical_id('substrate/type/Model_Tokenizer/v1')
  AND  e.tier <> 250;

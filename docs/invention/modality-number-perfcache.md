# Modality number perfcache — current implementation note, not identity law

This file describes a finite derived accelerator that exists in the current implementation. It is subordinate to docs/specs/33_Perfcache_Blob_Law.md and modality-ladder-law.md.

The historical version of this note treated decimal Unicode spelling (255 → 2,5,5) as the universal identity route for image/audio scalar values. That is no longer a binding invention law.

## Current implementation surface

| Piece | Path |
|---|---|
| Format | engine/core/include/laplace/core/modality_number_perfcache_format.h |
| Load / lookup | modality_number_table_* |
| Emit | modality_number_tables_emit → laplace_modality_number_perfcache.bin |
| CMake | laplace_modality_number_perfcache |

Where a currently selected recipe genuinely uses the existing canonical numeric/text representation, this table may accelerate it.

It must not be used to conclude that every sample/channel/tensor value is semantic content, every modality scalar must be converted to a decimal string, numeric display spelling is the identity of physical source data, or a perfcache owns the semantic representation.

## Required preservation

- the blob remains deterministic, versioned and rebuildable;
- lookup matches the canonical/reference recipe it accelerates;
- missing blob falls back to canonical calculation rather than changing identity;
- expanding scalar coverage is an explicit recipe/perfcache generation change;
- source precision/physicality semantics stay outside the cache when the cache does not encode them.

If the selected modality recipe changes away from the historical decimal-number composition, this cache must be regenerated/replaced/retired rather than forcing the new recipe to preserve the cache's old semantics.

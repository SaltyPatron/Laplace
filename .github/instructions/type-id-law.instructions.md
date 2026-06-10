---
applyTo: "{app/Laplace.Decomposers.*/**,app/Laplace.SubstrateCRUD/**,app/Laplace.Engine.Core/**}"
description: "Use when minting type IDs, physicality types, relation types, or substrate row builders."
---

# Laplace Type ID Law

Three type systems — one front door each. Do not mix them.

## Entity type_id (compositional identity)

- Column: `laplace.entities.type_id` (`bytea` hash)
- API: `EntityTypeRegistry.Id("Language")` → `substrate/type/Language/v1`
- Use for: what kind of entity this is (Language, FrameNet_Frame, UD_XPOS, Neuron, …)

## Attestation type_id (relation / evidence class)

- Column: `laplace.attestations.type_id` (`bytea` hash)
- API: `RelationTypeRegistry.RelationTypeId("HAS_PART")` or `RelationTypeRegistry.Resolve(...)` for rank/symmetry
- Use for: relation types, property relations, structural links
- Bootstrap meta-types: `BootstrapIntentBuilder` only (Source, Type, RelationType, HAS_TRUST_CLASS)

## Physicality type (geometric role)

- Column: `laplace.physicalities.type` (`smallint` enum)
- API: `PhysicalityType` enum only — never a hash path
- Production values: `Content = 1`, `Projection = 3` (S3 morph)
- Reserved (not yet emitted): `BuildingBlock = 2`, `ProjectionOutput = 4`

Physicality hash paths (`substrate/physicality_type/{SEG}/v1`) are schema-law identifiers only — not emitted as entity type_ids.

## Forbidden

```csharp
// WRONG in decomposers
Hash128.OfCanonical("substrate/type/Language/v1");

// RIGHT
EntityTypeRegistry.Id("Language");
RelationTypeRegistry.RelationTypeId("HAS_PART");
```

CI enforces: `Hash128.OfCanonical("substrate/type/` may appear only in:
- `RelationTypeRegistry.cs`
- `EntityTypeRegistry.cs`
- `BootstrapIntentBuilder.cs`
- `PosReference.cs` (migrate to EntityTypeRegistry)
- Test fixtures explicitly marked

## PostgreSQL parity

Extension `relation_type_id('HAS_PART')` must match `RelationTypeRegistry.RelationTypeId("HAS_PART")`. Canonical path law is tested in `CanonicalPathLawTests`.

## WordNet / ISO / domain decomposers

- Relation strings → `RelationTypeRegistry` (never local `RelationType()` duplicates)
- Entity kind strings → `EntityTypeRegistry`
- Do not bypass registry for rank/symmetry metadata

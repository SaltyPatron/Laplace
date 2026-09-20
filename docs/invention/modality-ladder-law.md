# Modality ladder law — one open identity floor, typed modality grammars

This is a binding preservation law under docs/INVENTION.md and docs/CAPABILITIES.md.

The historical version of this file incorrectly equated the entire abstract Tier-0 law with the currently selected Unicode codepoint generation, then forced non-text values such as image channels and audio samples through decimal text spellings. That reduction is retired.

## One global identity law; many lawful primitive grammars

Laplace has one content-addressed identity/composition machine, not one private identity namespace per modality.

Tier-0 is an open, countably addressable atom law. Every concrete generation observes/materializes a finite prefix/set. The current Unicode generation is one standards-defined finite materialization of that law; its 1,114,112 codepoint positions are not the capacity or ontology of Tier-0.

Textual surfaces naturally recover Unicode codepoints/graphemes under UAX/normalization rules. That does not imply that an image sample, PCM sample, chess square, machine instruction, ELF field or tensor factor must be converted into the decimal characters spelling its numeric value before it can participate in canonical structure.

The modality/provider grammar determines what physical/source primitives are recovered and the versioned recipe determines their substrate roles.

## Provider → recipe → universal typed structure

Every admitted digital artifact follows the same boundary:

~~~text
artifact/source bytes
-> qualified syntax/container/codec/standards provider
-> exact recovered nodes/records/fields/order/spans/errors
-> versioned source recipe
-> universal typed AST / canonical recursive structures
   + occurrences
   + governed references
   + provenance
   + testimony
   + deterministic calculations
   + reconstruction state
~~~

Tree-sitter is one provider family. XML/UCD parsers, PNG/JPEG decoders, audio codecs, PGN/FEN parsers, ELF/PE/Mach-O readers, JVM classfile decoders and model-container readers are other provider families.

A provider-specific recovered primitive is not automatically canonical content merely because it is easy to hash.

## No private atom namespaces

A modality must not create a disconnected private Tier-0 universe whose identities cannot participate in the common canonical composition law.

But the remedy is not "serialize every value as decimal Unicode text."

A recipe explicitly declares how a recovered value participates:

- canonical atom/content under the common identity law;
- ordered constituent of a higher composition;
- occurrence/reference coordinate;
- typed physicality/factor;
- source/provenance metadata;
- attributed testimony;
- deterministic calculation operand/result;
- packaging/reconstruction state;
- transient decode state that is not persisted as semantic content.

Unknown disposition stays unresolved rather than silently becoming content.

## Packaging is not semantic identity

Containers and codecs recover source structure. File offsets, compression blocks, archive paths, tensor offsets and similar packaging coordinates do not become semantic identity unless a declared reconstruction/content recipe specifically requires them.

Equal canonical content may converge across different packages while package/file occurrences remain separately attributable.

## Numbers and scalars

There is no universal law that a scalar value must be identified by the Unicode decimal spelling of the value.

A source recipe may use an existing canonical numeric representation when that representation is semantically correct, or may retain a typed physical/sample value as physicality/calculation/reconstruction state. The representation and precision boundary are explicit recipe inputs.

The current modality-number perfcache is a finite derived accelerator for one selected numeric recipe. It is not proof that decimal text is the ontology of image/audio samples and it must not be used to force every modality through a textual ladder.

## Modality examples

### Text

Provider/grammar:
- Unicode/UAX/NFC and document-format structure.

Typical composition:
- codepoint/grapheme/word/sentence plus source-native paragraph/section/chapter/table/AST structures when the provider supplies them.

Unicode is authoritative for textual scalar identity in this lane.

### Code and repositories

Provider/grammar:
- exact language grammar (Tree-sitter or other qualified provider);
- Git object/container provider where repository history is selected;
- compiler/object/disassembly providers for calculated execution analysis.

Canonical structure includes ordered syntax/AST and repository/tree composition. Calls, types, dependencies, toolchain outcomes and execution analysis remain typed state around those identities.

Construction uses this grammar in reverse: reuse/compose/minimally mutate canonical AST before realizing source.

### Images

Provider/grammar:
- exact image/container decoder recovers dimensions, channels, samples, metadata and ordering.

The recipe decides which decoded sample/channel/region structures are canonical content/physicality/reconstruction state. It does not turn RGBA into a private universe, and it does not require 255 to become the textual characters 2,5,5 merely to be lawful.

### Audio

Provider/grammar:
- exact audio/container decoder recovers sample format, channels, sample order, timing and metadata.

Precision/sample format is part of the recipe/physicality boundary. Spectral/onset/features are versioned calculations unless the source literally supplies them.

### Video

Provider/grammar:
- container demux + image/audio/frame/timing structure.

Frame ordering and synchronization remain exact occurrences/trajectories. Codec/container packaging does not own semantic identity.

### Chess

Provider/grammar:
- PGN/FEN/rule engine/tablebase/provider formats.

Squares, pieces, positions, moves, lines and games are deterministic domain structures under the common content/occurrence/evidence machine. Hot position perfcaches are derived accelerators, not a private cognition ontology.

### Models

Provider/grammar:
- tokenizer/config/container/tensor structure.

Raw parameter numerics may be transient operands used to derive circuit physicalities/evidence. Checkpoint packaging and raw weight blobs do not become a second durable model ontology.

### Executables / bytecode

Provider/grammar:
- ELF/PE/Mach-O/JAR/classfile/disassembly and related exact structural providers.

Recovered source/bytecode/machine structures may be further calculated into CFG/data/dependency state and target-machine cycle derivations. Those calculations remain versioned and attributable.

## Physicality and trajectory

A modality's canonical structure may have one or more typed physicalities.

Packed trajectory carrier data is an exact constituent manifest; it is not child geometry. Realized curves resolve child identities to the requested physicality coordinates before geometric metrics are applied.

Fréchet/Hausdorff/angular/Hilbert/locality are typed comparison operators, never a replacement for canonical identity or source semantics.

## Perfcache law

A perfcache may materialize deterministic finite derived data for hot structures under spec 33.

It is rebuildable, versioned, verified against canonical state/recipe, never the semantic authority, and never permission to invent a private identity law for convenience.

## Reseed / generation implications

Changing a binding canonicalization/composition recipe can change identities and therefore requires a new generation/reseed where those identities are persisted.

Changing only a lawful physical execution plan (chunk size, worker count, batching, accelerator/provider) must not change canonical results.

## Acceptance

- one global identity/composition law is preserved across modalities;
- Unicode remains authoritative for textual Unicode surfaces without being promoted into the ontology of every digital primitive;
- each provider/recipe has complete field-role disposition;
- packaging and provenance do not silently salt reusable content;
- exact source ordering/reconstruction is preserved where required;
- calculations remain distinguishable from observations/testimony;
- modality-specific physicality is typed and queryable;
- perfcaches remain derived;
- software construction, machine analysis and export consume the same canonical structures instead of private modality worlds.

## Non-success

- private image/audio/code/model atom namespaces with their own identity law;
- converting arbitrary physical values to decimal text solely to satisfy an obsolete "all atoms are Unicode codepoints" rule;
- treating codec/container bytes or offsets as semantic identity by default;
- hashing transient embeddings/calculations as canonical identity;
- flattening source-native AST/document/media structure into a generic text ladder;
- using a modality-specific parser as a private persistence/cognition engine.

# Laplace operational source artifacts

The operational source admits the original repository invention and binding
specification files. The build copies those files directly from `docs/` into the
runtime payload, retaining their repository-relative paths. No rewritten
word-to-operation aliases or substitute ISA summaries are supplied.

The selected inputs are declared as literal `Content` items in
`app/Laplace.Decomposers/Laplace.Decomposers.csproj`:

- `docs/INVENTION.md` and `docs/INVENTIONS.md`.
- Binding specifications 05, 06, 08, 09, 11, 33, 34, 36, and 37.
- The explicitly authored annotation `seeds/operational/exemplars/en_define.conllu`.
- The explicitly authored fragment annotation `seeds/operational/exemplars/en_antonym.conllu`.
- The explicit definition relation-read declaration `seeds/operational/tasks/en_define.json`.
- The explicit antonym relation-read declaration `seeds/operational/tasks/en_antonym.json`.

After the Unicode and language foundation, admit the bundled source with:

```sh
dotnet app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll ingest operational
```

Product deployment runs `python3 scripts/verify-operational-seed.py --ingest`.
This default bootstrap scopes the input cap and forced reobservation flags to
zero, preserving per-file completion skips. An optional
`LAPLACE_INGEST_RUN_RECEIPT_PATH` lets the journal publish the actual generated
run UUID and source identity after its initial durable write; the verifier owns
a new private receipt path for each invocation and never substitutes the latest
source run. It derives the complete expected artifact paths from the project's literal
`Content` selection, verifies the bundled bytes against the authored files, and
uses the existing native BLAKE3/Merkle file-resume recipe to read back the exact
byte fingerprint and source-scoped layer-2 completion attestation for every file.
The file journal must account for that exact set as admitted and complete; the
run must be `ok`, uncapped, and backed by persisted evidence. Output contains
paths, identities, byte counts and completion status. Original contract text is
not copied into deployment logs. The generic ingest journal status checker
remains available for other sources and deliberate capped smoke runs.

An explicitly selected contract file or collection can be supplied as the path:

```sh
dotnet app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll ingest operational /path/to/contracts
```

This lane assigns `SubstrateMandate` provenance to the selected operational
contract source. It accepts Markdown and plain-text contract artifacts, authored CoNLL-U annotations, plus
explicit JSON task-shape declarations described below. The
source adapter supplies each artifact's unchanged bytes and file metadata to the
existing whole-source native grammar composition pipeline. Markdown parsing also
accepts plain text; the original bytes are preserved. The same native machinery
used for source-code admission retains parser structure, lexical constituents,
ordered composition, and source spans, together with the containing file identity.

File names are provenance. The adapter does not derive example words, concept
aliases, definitions, instruction triggers, or instruction programs from file
names or its own interpretation of the text.

Normal reruns inspect the selected artifacts and skip unchanged completed files.
An edited contract retains the previous content while admitting the changed
structure. File timestamps do not define semantic file identity. Existing
attestation identities are excluded from repeated consensus folding by the
shared atomic writer.

Admitting the ISA document makes its authored content and structure available in
the substrate. Executing the ISA still requires the runtime to consume grounded
program structure through the canonical native operations. Document admission
alone does not establish that execution behavior.

## Authored linguistic exemplars

An operational `.conllu` file is an explicit annotation authored for Laplace.
It is witnessed by `OperationalSource` with `SubstrateMandate` trust, separately
from observations attributed to the upstream Universal Dependencies source.
The filename's language prefix declares its language, and each sentence must
retain its exact `# text` surface. The existing CoNLL-U reader and native content
and UD structure composer retain token identities, lemmas, roles, features,
order and source occurrences. The original file bytes enter the same whole-file
grammar admission, shared writer and per-file completion boundary as the other
operational artifacts. The file identity participates in its annotation
provenance. No separate source worker or database writer is introduced.

The bundled `define justice` annotation supplies a syntactic exemplar. Its
separate `seeds/operational/tasks/en_define.json` declaration names the actual
admitted parse identity and the second token reference, recovered from native
source admission. That declaration calls the governed `HAS_DEFINITION` relation
and accepts a witnessed `WordNet_Synset` semantic binding for the second token.
The first token remains invariant. Both artifacts are authored operational
evidence with `SubstrateMandate` provenance; neither claims upstream UD authorship.

The definition declaration supplies one English, two-token relation-read shape: `define` followed
by one token whose current witnessed semantic binding has the declared type.
The shared native runtime uses Unicode-derived identities, exact source
structure and typed relations. It does not contain an English keyword dispatch
rule. Other languages, paraphrases, multi-token arguments and other operations
need their own supported source declarations and evidence. The annotation alone
does not select a task, and the declaration contains no expected answer.

Source execution acceptance reads these exact bundled bytes and checks the
declaration against the native admitted parse and token identities. It exercises
a fresh request operand, witnessed WordNet relation paths, changed source facts
and competing declarations through the existing forward program. Deployment
completion still requires the exact file journal and native completion witnesses
for all fifteen selected artifacts, followed by execution readback.

The additional `The opposite of empty is` annotation retains an intentionally
unfinished five-token fragment. Its fourth token is the prospective variable;
the other four forms are invariant source content. Its separate
`seeds/operational/tasks/en_antonym.json` declaration references the actual
admitted parse and fourth token, calls `IS_ANTONYM_OF`, and accepts the current
whole `Word` identity as its input. Its version-2 slot explicitly declares the
`CURRENT_FORM` binding mode. The declaration contains no answer.
The native executor consumes the same generic token-slot contract as the
definition declaration; it does not dispatch on the English words.

Native database acceptance admits the complete bundle and retains
`antonym-exemplar.json` with the actual parse, token, file, occurrence and task
identities. A separate execution case uses a fresh Unicode operand under the
ordinary forward defaults. It verifies that a missing relation result remains
unresolved even when two positively witnessed Word lemma alternatives have
their own results. Those alternatives remain admitted throughout the test.
Changing the source-witnessed result on the current Word changes the emitted
identity while every byte of the task stays unchanged. Its bounded
`antonym-execution.json` records the actual fact witnesses and execution receipts.

## Declared relation-read task shapes

A selected `.json` source can declare a reusable structural binding to the
native relation-read capability. The bundled English declaration above is one
instance of this generic schema. A source names exact IDs already admitted by
their owning source:

```json
{
  "schema": "laplace/task-shape/relation-read/token-slots/v1",
  "exemplar_parse_id": "<32 hexadecimal digits>",
  "predicate_id": "<32 hexadecimal digits>",
  "slots": [
    {
      "exemplar_token_ref_id": "<32 hexadecimal digits>",
      "accepted_entity_type_id": "<32 hexadecimal digits>"
    }
  ]
}
```

Replace each placeholder with the raw 16-byte identity encoded as hexadecimal.
The exemplar must be a complete admitted UD parse. Each declared slot denotes
one whole token under that parse and one current content occurrence; at least
one token must remain invariant as an indexed lexical anchor. Exemplars with
multiword-token records require span alignment and are unsupported by this
schema. It does not generalize arbitrary spans or subtrees. The matcher retains
dependency topology, grammatical features,
language, order, punctuation, and all undeclared lexical structure. Only the
declared token forms and lemmas may vary, and their current semantic bindings
must satisfy the declared entity type. Additional unmatched content prevents a
complete match. A surface name alone cannot supply an applicability declaration.
A new request need not already have an observed corpus parse: its complete
ordered surface can instantiate the explicitly declared substitutions. The
resulting structural projection is an inference under the shape contract, with
its own recipe identity; it is never recorded as a new `HAS_PARSE` observation
merely because matching succeeded. Existing observed parses constrain that
projection and cannot be bypassed when they contradict it.

The native JSON grammar is parsed once with full-source admission. The source
witness reuses that AST, creates a type-8 structural trajectory, and records
`exemplar_parse IS_EXAMPLE_OF shape`, `shape CALLS predicate`, and the complete
`shape HAS_INPUT slot` set under one source-file context. Missing, duplicate,
unknown, or malformed fields fail admission. Referenced parses, predicates,
token references and entity types are not manufactured by this source.

The trajectory is `[schema, exemplar_parse, predicate, (slot, token_ref,
accepted_type)*, slots_end]`. Its identity is the Document-tier Merkle
composition of those exact IDs. Each slot is the Document-tier Merkle
composition of `[token_slot_schema, token_ref, accepted_type]`. The marker names
are `laplace/task-shape/relation-read/token-slots/v1`,
`laplace/task-shape/token-slot/v1`, and `laplace/task-shape/slots-end/v1`.
Source-file identity belongs to provenance, so changing JSON presentation can
change its source occurrence without changing the declared shape.

Version 2 uses the schema `laplace/task-shape/relation-read/token-slots/v2`
and requires a `binding_mode_id` on every slot. `WITNESSED_SEMANTIC` retains
the same typed, source-witnessed semantic binding alternatives as version 1;
multiple complete interpretations remain ambiguous. `CURRENT_FORM` binds the
exact whole current token form at the declared source occurrence and checks its
stored entity type. The declaration selects this behavior explicitly. Naming
and lemma evidence remains available to other contracts and operators.

The version-2 trajectory retains `[schema, exemplar_parse, predicate,
(slot, token_ref, accepted_type, binding_mode)*, slots_end]`. Its slot identity
commits all four constituents `[token_slot_schema, token_ref, accepted_type,
binding_mode]`. Both schema and slot markers carry version 2. An unsupported
mode or a slot identity that omits or changes the declared mode fails decoding;
the runtime cannot silently reinterpret a version-1 contract as current-form
binding. The version-1 representation and existing definition declaration stay
unchanged.

The type-8 coordinate realizes the canonical declaration representation. Its
recipe serializes one UTF-8 JSON object without whitespace or a BOM, with fields
in this exact order: `schema`, `exemplar_parse_id`, `predicate_id`, `slots`.
Each slot object contains `exemplar_token_ref_id`, then
`accepted_entity_type_id`, followed by `binding_mode_id` for version 2;
slot order is the declared array order. Every ID is
the raw 16 bytes written as 32 lowercase hexadecimal digits. The schema value
is the exact marker string above. The existing native JSON full-source composer
realizes and stages this derived representation, separately from the unchanged
authored file. The source witness reuses the original file AST; only the derived
canonical representation receives its own native grammar composition. No
coordinate is extracted from hash bits or invented for a referenced entity.
These coordinates describe declaration representation, while the type-8
trajectory retains the semantic reference IDs. Changing source whitespace,
property order, JSON escapes or hexadecimal letter case cannot change the
shape's coordinate, Hilbert address or structural trajectory.

The canonical forward program consumes positively witnessed complete shapes
after structural and semantic coupling. It binds new current input IDs and
reads actual predicate results; no expected answer is stored in the shape.
Competing complete interpretations remain ambiguous. The program fingerprint
commits the shape, exemplar/current parse identities and applicability witnesses.
The public receipt exposes that fingerprint together with completion counts and
support fields; these identities are not separate public receipt columns.
These task shapes describe a relation-read capability within the ISA; they do not equate individual prompt
words with cognition opcodes.

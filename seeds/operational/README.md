# Laplace operational source artifacts

The operational source admits the original repository invention and binding
specification files. The build copies those files directly from `docs/` into the
runtime payload, retaining their repository-relative paths. No rewritten
word-to-operation aliases or substitute ISA summaries are supplied.

The selected inputs are declared as literal `Content` items in
`app/Laplace.Decomposers/Laplace.Decomposers.csproj`:

- `docs/INVENTION.md` and `docs/INVENTIONS.md`.
- Binding specifications 05, 06, 08, 09, 11, 33, 34, 36, and 37.

After the Unicode and language foundation, admit the bundled source with:

```sh
dotnet app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll ingest operational
```

An explicitly selected contract file or collection can be supplied as the path:

```sh
dotnet app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll ingest operational /path/to/contracts
```

This lane assigns `SubstrateMandate` provenance to the selected operational
contract source. It accepts Markdown and plain-text contract artifacts. The
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

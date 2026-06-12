# Vendored generated grammar sources

Two upstream grammars do not commit their generated `parser.c` on the branch our
submodules pin (tree-sitter-sql keeps it on `gh-pages`; tree-sitter-swift does not
publish it at all). Every other vendored grammar compiles its upstream-committed C
directly — these two are vendored HERE so the build stays hermetic: **no network
fetch at configure, no tree-sitter CLI on any build machine, ever.** CI and fresh
clones compile exactly these bytes with the in-tree toolchain, like the other 30.

Same law as `engine/core/src/generated/`: committed artifacts of a scripted
generation step. Never hand-edit.

## Regenerating (only when bumping a grammar submodule)

On the canonical Windows dev machine (tree-sitter CLI pinned 0.26.9):

```
cd external\tree-sitter-grammars\tree-sitter-swift && tree-sitter generate
cd external\tree-sitter-grammars\tree-sitter-sql   && git fetch --depth=1 origin gh-pages ^
    && git checkout origin/gh-pages -- src/parser.c src/scanner.c src/tree_sitter/
```

then copy `src/{parser.c,scanner.c,tree_sitter/*.h}` into `generated/<name>/`,
build + run the engine grammar tests, and commit the new artifacts in the same
change as the submodule bump. Restore the submodule working trees to pristine
afterwards (`git -C <submodule> checkout -- . ; git -C <submodule> clean -fd src`).

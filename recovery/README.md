# Recovery evidence

The old `native-build` directory contained generated CMake output, compiled
libraries, and historical SQL expansions. Its complete 450-entry tree was copied
and hash/mode/link verified on 2026-09-12 to:

`/build/laplace/recovery/20260912-cleanup/legacy-native-build`

The adjacent `legacy-native-build-relocation.json` records every preserved entry.
The operator checkout retains an ignored compatibility symlink. Generated build
output is no longer tracked as source; the original tracked versions also remain
in Git history and the verified repository recovery bundles.

Current builds use the configured `/build/laplace/build` location. Historical
recovery builds are evidence, not installable replacements for the current build.

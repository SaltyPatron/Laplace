#!/usr/bin/env bash
# Run the production externals phase against fixture dependency headers.
set -euo pipefail
umask 0002
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:?Declare CI scratch}/site-externals.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
LAPLACE_EXTERNAL="$scratch/external"
SITE_WORK="$scratch/work"
LAPLACE_DEPSRC="$scratch/dependencies"
LAPLACE_DATA="$scratch/data"
LAPLACE_POSTGIS_BUILD="$scratch/postgis-build"
LAPLACE_GEOS_PROJ_PREFIX="$scratch/geo"
LAPLACE_NATIVE="$LAPLACE_DEPSRC/laplace-native"
LAPLACE_EIGEN="$LAPLACE_DEPSRC/eigen"
for name in blake3 laplace-native tree-sitter Fathom spectra postgis eigen; do mkdir -p "$LAPLACE_DEPSRC/$name"; done
mkdir -p "$LAPLACE_DATA/TreeSitter" "$LAPLACE_GEOS_PROJ_PREFIX" "$LAPLACE_POSTGIS_BUILD/liblwgeom/.libs"
printf '#include "../postgis_config.h"\n' > "$LAPLACE_POSTGIS_BUILD/liblwgeom/liblwgeom.h"
printf '#define HEADER_VALUE 11\n' > "$LAPLACE_POSTGIS_BUILD/postgis_config.h"
printf '#define POSTGIS_REVISION fixture\n' > "$LAPLACE_POSTGIS_BUILD/postgis_revision.h"
touch "$LAPLACE_POSTGIS_BUILD/liblwgeom/.libs/liblwgeom.a"
mkdir -p "$LAPLACE_DEPSRC/postgis/liblwgeom"
touch "$LAPLACE_DEPSRC/postgis/liblwgeom/lwinline.h"
say() { :; }
fail() { echo "$*" >&2; exit 1; }
definition=$(sed -n '/^phase_externals() {/,/^}/p' "$root/deploy/linux/site.sh")
[[ $definition == 'phase_externals() {'* ]] || fail 'Externals phase was not found'
eval "$definition"
phase_externals > "$scratch/externals-first.log"
headers="$SITE_WORK/deps/pgsql-18/include"
mkdir "$scratch/project"
cat > "$scratch/project/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.24)
project(HeaderFixture C)
add_executable(headers main.c)
target_include_directories(headers PRIVATE "${HEADERS}")
CMAKE
printf '#include <stdio.h>\n#include <liblwgeom.h>\nint main(void) { printf("%%d\\n", HEADER_VALUE); return 0; }\n' > "$scratch/project/main.c"
cmake -S "$scratch/project" -B "$scratch/build" -G Ninja "-DHEADERS=$headers" > "$scratch/configure.log"
cmake --build "$scratch/build" > "$scratch/first.log"
[[ $("$scratch/build/headers") == 11 ]]
before=$(stat -c '%i %y %a' "$headers/liblwgeom.h" "$headers/postgis_config.h" "$headers/postgis_revision.h")
phase_externals > "$scratch/externals-second.log"
[[ $(stat -c '%i %y %a' "$headers/liblwgeom.h" "$headers/postgis_config.h" "$headers/postgis_revision.h") == "$before" ]]
cmake --build "$scratch/build" | tee "$scratch/second.log"
grep -Fq 'ninja: no work to do.' "$scratch/second.log"
echo 'PASS the real externals phase preserves unchanged prepared headers and a no-work build'
printf '#define HEADER_VALUE 23\n' > "$LAPLACE_POSTGIS_BUILD/postgis_config.h"
phase_externals > "$scratch/externals-changed.log"
cmake --build "$scratch/build" > "$scratch/changed.log"
[[ $("$scratch/build/headers") == 23 ]]
echo 'PASS changed dependency headers rebuild and execute the new code'

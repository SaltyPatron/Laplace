#!/usr/bin/env bash
# Real Git/CMake/Ninja coverage of persistent candidate checkout preparation.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:?Declare build scratch}/checkout-incremental.XXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
repository="$scratch/candidate"
origin="$scratch/dependency"
mkdir "$repository" "$origin"
for path in "$repository" "$origin"; do
  git -C "$path" init -q
  git -C "$path" config user.name 'Checkout fixture'
  git -C "$path" config user.email fixture@example.invalid
done
cat > "$origin/value.c" <<'C'
int value(void) { return 1; }
C
git -C "$origin" add value.c
git -C "$origin" commit -qm initial
cat > "$repository/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.24)
project(CheckoutFixture C)
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${CMAKE_SOURCE_DIR}/law-value")
file(READ "${CMAKE_SOURCE_DIR}/law-value" LAW_VALUE)
string(STRIP "${LAW_VALUE}" LAW_VALUE)
configure_file(law.h.in "${CMAKE_SOURCE_DIR}/engine/core/include/laplace/core/entity_type_law.h" @ONLY)
configure_file(law.c.in "${CMAKE_SOURCE_DIR}/engine/core/src/generated/entity_type_law.c" @ONLY)
add_executable(checkout main.c .ci-dependencies/Laplace-Native/value.c engine/core/src/generated/entity_type_law.c)
target_include_directories(checkout PRIVATE engine/core/include)
CMAKE
printf '10\n' > "$repository/law-value"
printf '#define LAW_VALUE @LAW_VALUE@\n' > "$repository/law.h.in"
printf '#include <laplace/core/entity_type_law.h>\nint law(void) { return LAW_VALUE; }\n' > "$repository/law.c.in"
cat > "$repository/main.c" <<'C'
#include <stdio.h>
extern int value(void);
extern int law(void);
int main(void) { printf("%d %d\n", value(), law()); return 0; }
C
git -C "$repository" add CMakeLists.txt main.c law-value law.h.in law.c.in
git -C "$repository" commit -qm initial
mkdir "$repository/.ci-dependencies"
git clone -q "$origin" "$repository/.ci-dependencies/Laplace-Native"
cmake -S "$repository" -B "$scratch/build" -G Ninja > "$scratch/configure.log"
cmake --build "$scratch/build" > "$scratch/first.log"
[[ $("$scratch/build/checkout") == '1 10' ]]
source_file="$repository/.ci-dependencies/Laplace-Native/value.c"
before=$(stat -c '%i %y' "$source_file")
header="$repository/engine/core/include/laplace/core/entity_type_law.h"
law_source="$repository/engine/core/src/generated/entity_type_law.c"
generated_before=$(stat -c '%i %y' "$header" "$law_source")
printf 'unknown generated source\n' > "$repository/engine/core/src/generated/stray.c"
mkdir -p "$repository/web/node_modules" "$repository/web/dist" "$repository/unrelated/nested"
mkdir "$repository/.ci-dependencies/obsolete"
printf 'unselected dependency\n' > "$repository/.ci-dependencies/obsolete/stale.c"
printf 'dependency lock\n' > "$repository/web/node_modules/.package-lock.json"
printf 'qualified web output\n' > "$repository/web/dist/index.html"
printf 'untracked source\n' > "$repository/unrelated/nested/untracked.c"
printf 'untracked web source\n' > "$repository/web/stray.ts"
printf 'dirty tracked source\n' > "$repository/main.c"
export GITHUB_ACTIONS=true GITHUB_WORKSPACE="$repository"
GITHUB_SHA=$(git -C "$repository" rev-parse HEAD)
export GITHUB_SHA
prepare() { (cd "$repository" && bash "$root/scripts/ci/prepare-checkout.sh"); }
prepare
[[ ! -e "$repository/unrelated" && ! -e "$repository/web/stray.ts" ]]
[[ ! -e "$repository/.ci-dependencies/obsolete" ]]
[[ ! -e "$repository/engine/core/src/generated/stray.c" ]]
[[ -f "$repository/web/node_modules/.package-lock.json" && -f "$repository/web/dist/index.html" ]]
[[ $(stat -c '%i %y' "$source_file") == "$before" ]]
[[ $(stat -c '%i %y' "$header" "$law_source") == "$generated_before" ]]
git -C "$repository" diff --exit-code
# Repairing deliberately dirty tracked source correctly invalidates its object.
cmake --build "$scratch/build" > "$scratch/repaired.log"
prepare
cmake -S "$repository" -B "$scratch/build" > "$scratch/reconfigure.log"
# Each dependency's own checkout still resets/cleans its candidate independently.
git -C "$repository/.ci-dependencies/Laplace-Native" reset --hard HEAD
git -C "$repository/.ci-dependencies/Laplace-Native" clean -ffdx
cmake --build "$scratch/build" | tee "$scratch/second.log"
grep -Fq 'ninja: no work to do.' "$scratch/second.log"
echo 'PASS exact cleanup and configure retain dependency/generated inputs and a no-work second build'
printf 'int value(void) { return 2; }\n' > "$origin/value.c"
git -C "$origin" commit -qam changed
git -C "$repository/.ci-dependencies/Laplace-Native" fetch -q origin
git -C "$repository/.ci-dependencies/Laplace-Native" checkout -q --detach FETCH_HEAD
cmake --build "$scratch/build" > "$scratch/changed.log"
[[ $("$scratch/build/checkout") == '2 10' ]]
echo 'PASS a changed dependency revision rebuilds and executes the new code'
printf '20\n' > "$repository/law-value"
cmake --build "$scratch/build" > "$scratch/law-changed.log"
[[ $("$scratch/build/checkout") == '2 20' ]]
printf '#define LAW_VALUE 99\n' > "$header"
cmake -S "$repository" -B "$scratch/build" > "$scratch/law-repair.log"
cmake --build "$scratch/build" > "$scratch/law-repaired.log"
[[ $("$scratch/build/checkout") == '2 20' ]]
echo 'PASS changed generation inputs rebuild; retained output cannot override its configure owner'
printf 'must survive refusal\n' > "$repository/refusal-witness"
if GITHUB_SHA=0000000000000000000000000000000000000000 prepare > "$scratch/refusal.log" 2>&1; then
  echo 'Wrong revision was accepted' >&2; exit 1
fi
[[ -f "$repository/refusal-witness" ]]
mv "$header" "$scratch/header-cache"
ln -s "$scratch/header-cache" "$header"
if prepare > "$scratch/generated-redirect.log" 2>&1; then
  echo 'Redirected generated input was accepted' >&2; exit 1
fi
[[ -f "$repository/refusal-witness" && -f "$scratch/header-cache" ]]
rm "$header"
mv "$scratch/header-cache" "$header"
mv "$repository/web/node_modules" "$scratch/dependency-cache"
ln -s "$scratch/dependency-cache" "$repository/web/node_modules"
if prepare > "$scratch/redirect.log" 2>&1; then
  echo 'Redirected cache was accepted' >&2; exit 1
fi
[[ -f "$repository/refusal-witness" && -f "$scratch/dependency-cache/.package-lock.json" ]]
echo 'PASS wrong revisions and redirected cache paths are refused before cleanup'

#!/usr/bin/env bash
# Exercise the immutable Git-blob owner and its real Ninja consumers.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:?Declare CI scratch}/operations-contract.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
source="$scratch/operations"
fixture="$scratch/owner"
output="$scratch/contract"
mkdir -p "$source/math" "$source/toolchain" "$fixture/scripts/ci"
cp "$root/scripts/ci/materialize-operations-contract.sh" "$fixture/scripts/ci/"
git -C "$source" init -q
git -C "$source" config user.name 'Contract fixture'
git -C "$source" config user.email fixture@example.invalid
printf 'int value(void) { return 1; }\n' > "$source/math/names.c"
printf 'set(CONTRACT_FIXTURE 1)\n' > "$source/toolchain/flags.cmake"
git -C "$source" add .
git -C "$source" commit -qm initial
manifest() {
  printf '# Git blob identities from SaltyPatron/Laplace-Operations %s.\n' "$(git -C "$source" rev-parse HEAD)"
  for path in math/names.c toolchain/flags.cmake; do
    printf '%s %s\n' "$(git hash-object --no-filters "$source/$path")" "$path"
  done
}
manifest > "$fixture/scripts/ci/operations-build-contract.tsv"
materialize() { bash "$fixture/scripts/ci/materialize-operations-contract.sh" "$source" "$output"; }
materialize
mkdir "$scratch/project"
cat > "$scratch/project/CMakeLists.txt" <<'CMAKE'
cmake_minimum_required(VERSION 3.24)
project(ContractFixture C)
add_executable(contract main.c "${CONTRACT}/math/names.c")
CMAKE
printf '#include <stdio.h>\nextern int value(void);\nint main(void) { printf("%%d\\n", value()); return 0; }\n' > "$scratch/project/main.c"
cmake -S "$scratch/project" -B "$scratch/build" -G Ninja "-DCONTRACT=$output" > "$scratch/configure.log"
cmake --build "$scratch/build" > "$scratch/first.log"
[[ $("$scratch/build/contract") == 1 ]]
before=$(stat -c '%i %y %a' "$output/math/names.c" "$output/toolchain/flags.cmake")
materialize
[[ $(stat -c '%i %y %a' "$output/math/names.c" "$output/toolchain/flags.cmake") == "$before" ]]
cmake --build "$scratch/build" | tee "$scratch/second.log"
grep -Fq 'ninja: no work to do.' "$scratch/second.log"
echo 'PASS unchanged immutable contract keeps file identity and a no-work build'
cp -p "$output/math/names.c" "$scratch/old"
printf 'int value(void) { return 99; }\n' > "$output/math/names.c"
touch -r "$scratch/old" "$output/math/names.c"
materialize
cmp "$scratch/old" "$output/math/names.c"
echo 'PASS changed bytes are repaired even with an unchanged timestamp'
printf 'int value(void) { return 2; }\n' > "$source/math/names.c"
git -C "$source" commit -qam changed
manifest > "$fixture/scripts/ci/operations-build-contract.tsv"
printf 'uncommitted operator change\n' > "$source/math/names.c"
source_state=$(git -C "$source" status --porcelain)
materialize
[[ $(git -C "$source" status --porcelain) == "$source_state" ]]
[[ $(<"$source/math/names.c") == 'uncommitted operator change' ]]
cmake --build "$scratch/build" > "$scratch/changed.log"
[[ $("$scratch/build/contract") == 2 ]]
echo 'PASS a new pin rebuilds the consumer without changing the source checkout'

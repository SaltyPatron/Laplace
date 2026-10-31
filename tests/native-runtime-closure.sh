#!/usr/bin/env bash
set -euo pipefail
: "${TMPDIR:?test scratch required}"
root=$(cd -- "$(dirname -- "$0")/.." && pwd)
work=$(mktemp -d "$TMPDIR/native-runtime.XXXXXXXX")
trap 'rm -rf -- "$work"' EXIT
source "$root/deploy/linux/native-runtime.sh"
mkdir "$work/vendor" "$work/package"
cat > "$work/library.c" <<'C'
int fixture_runtime_value(void) { return 37; }
C
cat > "$work/engine.c" <<'C'
extern int fixture_runtime_value(void);
int main(void) { return fixture_runtime_value() == 37 ? 0 : 1; }
C
cc -shared -fPIC "$work/library.c" -Wl,-soname,libfixture_runtime.so.1 -o "$work/vendor/libfixture_runtime.so.1"
ln -s libfixture_runtime.so.1 "$work/vendor/libfixture_runtime.so"
cc "$work/engine.c" -L"$work/vendor" -lfixture_runtime -Wl,-rpath,"$work/vendor" -o "$work/package/laplace"
resolved=$(native_runtime_libraries "$work/package/laplace")
[[ "$resolved" == "$work/vendor/libfixture_runtime.so.1" ]]
cp -L "$resolved" "$work/package/"
mv "$work/vendor" "$work/unavailable-vendor"
LD_LIBRARY_PATH="$work/package" "$work/package/laplace"
echo 'PASS executable-only dependencies run from the received package without their build prefix'
if native_runtime_libraries "$work/package/laplace" > "$work/missing" 2>&1; then
  echo 'FAIL unresolved executable dependency was accepted' >&2; exit 1
fi
grep -q 'not found' "$work/missing"
echo 'PASS a missing loader dependency fails publication visibly'

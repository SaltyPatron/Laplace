#!/usr/bin/env bash
# Exercise the production MSBuild and native synchronization owners with real ELF dependencies.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:?Declare build scratch}/native-artifact-sync.XXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
native="$scratch/native source"
managed="$scratch/managed"
mkdir -p "$native/core" "$native/dynamics" "$scratch/project" "$scratch/external"
cat > "$scratch/core.c" <<'C'
static const int identity = VALUE;
const void *core_identity(void) { return &identity; }
C
cat > "$scratch/dynamics.c" <<'C'
extern const void *core_identity(void);
const void *dynamics_core_identity(void) { return core_identity(); }
C
build_core() {
  gcc -shared -fPIC -DVALUE="$1" -Wl,-soname,liblaplace_core.so.0 \
    "$scratch/core.c" -o "$native/core/liblaplace_core.so.0.1.0"
}
build_core 1
ln -s liblaplace_core.so.0 "$native/core/liblaplace_core.so"
ln -s liblaplace_core.so.0.1.0 "$native/core/liblaplace_core.so.0"
gcc -shared -fPIC -Wl,-soname,liblaplace_dynamics.so.0 \
  "$scratch/dynamics.c" -L"$native/core" -llaplace_core -o "$native/dynamics/liblaplace_dynamics.so.0.1.0"
ln -s liblaplace_dynamics.so.0 "$native/dynamics/liblaplace_dynamics.so"
ln -s liblaplace_dynamics.so.0.1.0 "$native/dynamics/liblaplace_dynamics.so.0"
gcc -shared -fPIC -DVALUE=99 -Wl,-soname,liblaplace_core.so.0 \
  "$scratch/core.c" -o "$scratch/external/liblaplace_core.so.0"
cat > "$scratch/project/Program.cs" <<'CS'
using System.Runtime.InteropServices;
internal static class Program
{
    [DllImport("laplace_core", EntryPoint = "core_identity")]
    private static extern IntPtr Core();
    [DllImport("laplace_dynamics", EntryPoint = "dynamics_core_identity")]
    private static extern IntPtr Dynamics();
    private static int Main(string[] args)
    {
        var dependency = Dynamics();
        if (dependency != Core() || Marshal.ReadInt32(dependency) != int.Parse(args[0])) return 1;
        Console.WriteLine("selected app-local native closure loaded");
        return 0;
    }
}
CS
cat > "$scratch/project/NativeSyncFixture.csproj" <<XML
<Project>
  <PropertyGroup>
    <DirectoryBuildPropsPath>$root/app/Directory.Build.props</DirectoryBuildPropsPath>
    <DirectoryBuildTargetsPath>$root/app/Directory.Build.targets</DirectoryBuildTargetsPath>
  </PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$root/app/Laplace.Core/Core/NativeLibraryClosure.cs" />
  </ItemGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
XML
export LAPLACE_ENGINE_BUILD="$native" LAPLACE_BUILD_ROOT="$managed" LAPLACE_REUSE_INSTALLED_NATIVE=0
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDUSESERVER=0 UseSharedCompilation=false
output="$managed/app/bin/NativeSyncFixture/Release/net10.0"
mkdir -p "$output"
# Reproduce an existing SDK output with three physical copies of the same ELF.
for source in "$native/core"/liblaplace_core.so*; do cp -L -- "$source" "$output/$(basename "$source")"; done
build() { dotnet build "$scratch/project/NativeSyncFixture.csproj" -c Release --nologo -v quiet; }
run() { LD_LIBRARY_PATH="$scratch/external" dotnet "$1/NativeSyncFixture.dll" "$2"; }
build
[[ -L "$output/liblaplace_core.so" && -L "$output/liblaplace_core.so.0" ]]
[[ ! -L "$output/liblaplace_core.so.0.1.0" ]]
run "$output" 1
before=$(stat -c '%i %y' "$output/liblaplace_core.so.0.1.0" "$output/liblaplace_core.so" "$output/NativeSyncFixture.dll")
build
after=$(stat -c '%i %y' "$output/liblaplace_core.so.0.1.0" "$output/liblaplace_core.so" "$output/NativeSyncFixture.dll")
[[ "$before" == "$after" ]] || { echo 'Unchanged build rewrote native or managed outputs' >&2; exit 1; }
echo 'PASS unchanged second build preserves library, alias and assembly inodes/mtimes'
dotnet publish "$scratch/project/NativeSyncFixture.csproj" -c Release --no-build --nologo -v quiet -o "$scratch/published"
[[ -L "$scratch/published/liblaplace_core.so" && -L "$scratch/published/liblaplace_core.so.0" ]]
run "$scratch/published" 1
# The real deployment synchronizer must retain the ELF alias chain as well.
source "$root/deploy/linux/payload-sync.sh"
mkdir "$scratch/deployed"
laplace_sync_payload "$scratch/published" "$scratch/deployed"
[[ -L "$scratch/deployed/liblaplace_core.so" && -L "$scratch/deployed/liblaplace_core.so.0" ]]
run "$scratch/deployed" 1
build_core 2
touch -d '2000-01-01 UTC' "$native/core/liblaplace_core.so.0.1.0"
build
cmp -- "$native/core/liblaplace_core.so.0.1.0" "$output/liblaplace_core.so.0.1.0"
run "$output" 2
echo 'PASS native content changes propagate despite an older source timestamp'
mv "$native" "$scratch/removed-native-source"
run "$scratch/published" 1
echo 'PASS published closure preserves its own version after source replacement'
if LAPLACE_ENGINE_BUILD="$scratch/missing" bash "$root/scripts/sync-managed-native-artifacts.sh" --output "$scratch/missing-output" > "$scratch/missing.log" 2>&1; then
  echo 'Missing native source was accepted' >&2; exit 1
fi
echo 'PASS missing selected native closure is refused'

#!/bin/bash

set -e

ok=0
warnings=0
errors=0

reset='\033[0m'; green='\033[0;32m'; yellow='\033[0;33m'; red='\033[0;31m'

check_command() {
    local label="$1" cmd="$2" version_args="$3" required="${4:-yes}"
    if command -v "$cmd" >/dev/null 2>&1; then
        local version
        version=$($cmd $version_args 2>&1 | head -1 || echo "(version check failed)")
        printf "${green}✓${reset} %-30s → %s\n" "$label" "$version"
        ok=$((ok + 1))
    else
        if [ "$required" = "yes" ]; then
            printf "${red}✗${reset} %-30s MISSING (required)\n" "$label"
            errors=$((errors + 1))
        else
            printf "${yellow}⚠${reset} %-30s missing (optional)\n" "$label"
            warnings=$((warnings + 1))
        fi
    fi
}

check_path() {
    local label="$1" path="$2" required="${3:-yes}"
    if [ -e "$path" ]; then
        printf "${green}✓${reset} %-30s → %s\n" "$label" "$path"
        ok=$((ok + 1))
    else
        if [ "$required" = "yes" ]; then
            printf "${red}✗${reset} %-30s MISSING (required, expected at %s)\n" "$label" "$path"
            errors=$((errors + 1))
        else
            printf "${yellow}⚠${reset} %-30s missing (optional, expected at %s)\n" "$label" "$path"
            warnings=$((warnings + 1))
        fi
    fi
}

check_pkg() {
    local label="$1" pkg="$2" required="${3:-yes}"
    if pkg-config --modversion "$pkg" >/dev/null 2>&1; then
        printf "${green}✓${reset} %-30s → %s\n" "$label" "$(pkg-config --modversion "$pkg")"
        ok=$((ok + 1))
    else
        if [ "$required" = "yes" ]; then
            printf "${red}✗${reset} %-30s MISSING (pkg-config %s)\n" "$label" "$pkg"
            errors=$((errors + 1))
        else
            printf "${yellow}⚠${reset} %-30s missing (optional)\n" "$label"
            warnings=$((warnings + 1))
        fi
    fi
}

echo "=== OS / kernel ==="
uname -a
grep -E '^(NAME|VERSION)=' /etc/os-release 2>/dev/null | head -2
echo

echo "=== CPU ==="
lscpu | grep -E 'Model name|Architecture|^CPU\(s\)'
echo "AVX support: $(grep -oE 'avx[a-z0-9_]*' /proc/cpuinfo | sort -u | tr '\n' ' ')"
echo

echo "=== Memory ==="
free -h | head -2
echo

echo "=== Build tools ==="
check_command "gcc"   "gcc"  "--version"
check_command "g++"   "g++"  "--version"
check_command "clang"     "clang"     "--version" no
check_command "clang++"   "clang++"   "--version" no
check_command "icx (Intel)"   "icx"   "--version" no
check_command "icpx (Intel)"  "icpx"  "--version" no
check_command "cmake"      "cmake"     "--version"
check_command "ninja"      "ninja"     "--version"
check_command "make"       "make"      "--version"
check_command "pkg-config" "pkg-config" "--version"
echo

echo "=== Intel oneAPI ==="
check_path "oneAPI install root"     "/opt/intel/oneapi"
check_path "oneMKL"                  "/opt/intel/oneapi/mkl/latest"
check_path "oneTBB"                  "/opt/intel/oneapi/tbb/latest"
echo

echo "=== PostgreSQL ==="
check_command "pg_config"      "pg_config" "--version"
check_command "psql"           "psql"      "--version"
check_path    "PG 18 server-dev headers" "/opt/laplace/pgsql-18/include/server"
check_path    "PGXS makefile"  "/opt/laplace/pgsql-18/lib/pgxs/src/makefiles/pgxs.mk"
echo

echo "=== PostGIS ==="
if dpkg -l postgresql-18-postgis-3 2>/dev/null | grep -q '^ii'; then
    printf "${green}✓${reset} %-30s → %s\n" "PostGIS 3 for PG18" "$(dpkg -l postgresql-18-postgis-3 | grep '^ii' | awk '{print $3}')"
    ok=$((ok + 1))
else
    printf "${red}✗${reset} %-30s MISSING\n" "PostGIS 3 for PG18"
    errors=$((errors + 1))
fi
echo

echo "=== .NET ==="
check_command ".NET CLI" "dotnet" "--version"
if command -v dotnet >/dev/null 2>&1; then
    echo "   SDKs available:"
    dotnet --list-sdks | sed 's/^/     /'
fi
echo

echo "=== Libraries ==="
check_pkg "Eigen"     "eigen3"
check_pkg "ICU (uc)"  "icu-uc"
check_pkg "ICU (i18n)" "icu-i18n"
if dpkg -l libxxhash0 2>/dev/null | grep -q '^ii'; then
    printf "${green}✓${reset} %-30s → installed\n" "libxxhash"
    ok=$((ok + 1))
else
    printf "${red}✗${reset} %-30s MISSING (apt install libxxhash-dev)\n" "libxxhash"
    errors=$((errors + 1))
fi
check_path "tree-sitter runtime"  "/usr/local/lib/libtree-sitter.so" no
check_path "tree-sitter grammars" "/vault/Data/TreeSitter" no
echo

echo "=== Summary ==="
printf "${green}%d OK${reset}, ${yellow}%d warning(s)${reset}, ${red}%d error(s)${reset}\n" "$ok" "$warnings" "$errors"
[ "$errors" -eq 0 ]

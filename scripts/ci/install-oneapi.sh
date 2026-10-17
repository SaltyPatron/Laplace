#!/usr/bin/env bash
# The oneAPI pieces the native build needs, at the versions hart-server builds with: the DPC++/C++ compiler 2026.1
# (icx/icpx, cmake/toolchains/intel-oneapi.cmake), MKL 2026.1 (-DLAPLACE_REQUIRE_MKL=ON) and TBB 2023.1.
set -euo pipefail
keyring=/usr/share/keyrings/oneapi-archive-keyring.gpg
if [[ ! -s "$keyring" ]]; then
  curl -fsSL --retry 3 https://apt.repos.intel.com/intel-gpg-keys/GPG-PUB-KEY-INTEL-SW-PRODUCTS.PUB \
    | gpg --dearmor | sudo tee "$keyring" >/dev/null
fi
echo "deb [signed-by=$keyring] https://apt.repos.intel.com/oneapi all main" \
  | sudo tee /etc/apt/sources.list.d/oneAPI.list >/dev/null
sudo apt-get update -q
sudo apt-get install -y -q --no-install-recommends \
  intel-oneapi-compiler-dpcpp-cpp-2026.1 intel-oneapi-mkl-core-devel-2026.1 intel-oneapi-tbb-devel-2023.1 \
  ninja-build libeigen3-dev unzip
set +u; . /opt/intel/oneapi/setvars.sh >/dev/null; set -u
command -v icx icpx

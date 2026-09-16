# Pinned Linux CMake

The Linux build selects Kitware CMake 4.4.3 from the official release archive recorded in `deploy/cmake-release.json`. The selected archive has an exact byte length and SHA256. This supplies CMake, CTest and CPack together without replacing the distribution's packages or changing a global system symlink.

`scripts/provision-cmake.py` owns acquisition and selection. A missing generation is acquired only with `--ensure`; ordinary selection verifies the retained release receipt, every package file or link, and the actual version output of all three tools. A changed generation fails instead of being overwritten. Downloads use the permanent build workspace and become an installed generation only after authentication, extraction and executable checks. A cancelled or failed acquisition leaves the previous generation intact.

Bootstrap, the CI environment action, the ordinary engine pipeline, the dependency build and the CuteChess source build select this owner. The engine/native input fingerprint includes the release pin and provisioning source, and the dependency fingerprint includes the same inputs. The next corresponding build therefore reconfigures its CMake metadata when the selected tool changes.

The default generation is `/opt/laplace/tools/cmake/4.4.3`; acquisition scratch is under `/build/laplace/work/cmake`. The existing install/work prefix overrides remain supported. No changes are made to Windows tool selection. The current package pin is for Linux x86_64; another architecture requires its own authenticated official release entry.

The registered `policy-cmake-release` controls execute fixture tool processes and test acquisition refusal, generation publication, exact cached contents, failed processes and preserved existing state. They do not establish compiler or dependency compatibility. Qualification must also execute the official archive on the actual build host and run the ordinary native configure/build/tests with its selected Intel toolchain, dependency sources and Qt SDK.

Refactor's product has a separate source-built toolchain manifest and release lock. Updating Original's selected CMake does not relabel or replace that package. Its source archive inventory, selected upstream test suite and consumer manifest must be updated and qualified through their existing owners.

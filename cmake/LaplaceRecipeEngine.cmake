# Recompute implicit source defaults at configure time. Explicit cache and
# parent selections retain precedence until the caller changes or clears them.
include("${CMAKE_CURRENT_LIST_DIR}/../../Laplace-Operations/toolchain/source-selection.cmake")
get_filename_component(_laplace_native_checkout "${LAPLACE_NATIVE}" REALPATH)
get_filename_component(_laplace_workspace "${_laplace_native_checkout}" DIRECTORY)
laplace_resolve_source_path(LAPLACE_RECIPE_ENGINE_SOURCE "${_laplace_workspace}/Laplace-Engine" "Canonical Laplace-Engine checkout for recipe execution")
unset(_laplace_native_checkout)
unset(_laplace_workspace)

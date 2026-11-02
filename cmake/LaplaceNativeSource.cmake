# An empty Native selection retains the monorepo's existing fallback behavior.
# Do not persist an implicit environment/external default in the cache.
include("${CMAKE_CURRENT_LIST_DIR}/../../Laplace-Operations/toolchain/source-selection.cmake")
laplace_resolve_source_path(LAPLACE_NATIVE "${LAPLACE_EXTERNAL}/laplace-native" "Laplace-Native checkout: identity (lp_id_compose)")

# An empty Native selection retains the monorepo's existing fallback behavior.
# Do not persist an implicit environment/external default in the cache.
if(NOT DEFINED LAPLACE_OPERATIONS OR "${LAPLACE_OPERATIONS}" STREQUAL "")
	if(DEFINED ENV{LAPLACE_OPERATIONS} AND NOT "$ENV{LAPLACE_OPERATIONS}" STREQUAL "")
		set(LAPLACE_OPERATIONS "$ENV{LAPLACE_OPERATIONS}")
	else()
		get_filename_component(LAPLACE_OPERATIONS "${CMAKE_CURRENT_LIST_DIR}/../../Laplace-Operations" ABSOLUTE)
	endif()
endif()
include("${LAPLACE_OPERATIONS}/toolchain/source-selection.cmake")
laplace_resolve_source_path(LAPLACE_NATIVE "${LAPLACE_EXTERNAL}/laplace-native" "Laplace-Native checkout: identity (lp_id_compose)")

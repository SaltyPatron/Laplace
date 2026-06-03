# extension/CMakeLists.txt
#
# Two PG extensions:
#   laplace_geom      — general-purpose 4D PostGIS additions
#   laplace_substrate — substrate domain (entities, attestations, cascade)
#
# Both built via CMake (no PGXS). Modular SQL via cpp
# preprocessor. Each extension produces:
#   <name>.so          → installs to ${LAPLACE_PG_PKGLIBDIR}
#   <name>.control     → installs to ${LAPLACE_PG_SHAREDIR}/extension/
#   <name>--<ver>.sql  → installs to ${LAPLACE_PG_SHAREDIR}/extension/
#                        (built by SQLPP from sql/<name>.sql.in + modules)

add_subdirectory(laplace_geom)
add_subdirectory(laplace_substrate)

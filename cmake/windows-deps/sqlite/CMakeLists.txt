cmake_minimum_required(VERSION 3.22)
project(laplace_sqlite C)

# Official amalgamation + tools — pinned. Downloaded once into the binary dir.
set(SQLITE_YEAR 2024)
set(SQLITE_AMAL_VER 3460100)
set(SQLITE_URL "https://www.sqlite.org/${SQLITE_YEAR}/sqlite-amalgamation-${SQLITE_AMAL_VER}.zip")
set(SQLITE_ZIP "${CMAKE_BINARY_DIR}/sqlite-amalgamation-${SQLITE_AMAL_VER}.zip")
set(SQLITE_SRC_DIR "${CMAKE_BINARY_DIR}/sqlite-amalgamation-${SQLITE_AMAL_VER}")

# Prebuilt CLI (PROJ needs sqlite3.exe to assemble proj.db).
# The tools zip extracts flat into CMAKE_BINARY_DIR (no versioned subfolder).
set(SQLITE_TOOLS_VER 3460100)
set(SQLITE_TOOLS_URL "https://www.sqlite.org/${SQLITE_YEAR}/sqlite-tools-win-x64-${SQLITE_TOOLS_VER}.zip")
set(SQLITE_TOOLS_ZIP "${CMAKE_BINARY_DIR}/sqlite-tools-win-x64-${SQLITE_TOOLS_VER}.zip")
set(SQLITE_TOOLS_EXE "${CMAKE_BINARY_DIR}/sqlite3.exe")

if(NOT EXISTS "${SQLITE_SRC_DIR}/sqlite3.c")
  message(STATUS "Downloading SQLite amalgamation ${SQLITE_AMAL_VER}...")
  file(DOWNLOAD "${SQLITE_URL}" "${SQLITE_ZIP}" SHOW_PROGRESS TLS_VERIFY ON STATUS _dl)
  list(GET _dl 0 _dl_code)
  if(NOT _dl_code EQUAL 0)
    message(FATAL_ERROR "SQLite amalgamation download failed: ${_dl}")
  endif()
  file(ARCHIVE_EXTRACT INPUT "${SQLITE_ZIP}" DESTINATION "${CMAKE_BINARY_DIR}")
endif()

if(NOT EXISTS "${SQLITE_TOOLS_EXE}")
  message(STATUS "Downloading SQLite tools ${SQLITE_TOOLS_VER}...")
  file(DOWNLOAD "${SQLITE_TOOLS_URL}" "${SQLITE_TOOLS_ZIP}" SHOW_PROGRESS TLS_VERIFY ON STATUS _dl)
  list(GET _dl 0 _dl_code)
  if(NOT _dl_code EQUAL 0)
    message(FATAL_ERROR "SQLite tools download failed: ${_dl}")
  endif()
  file(ARCHIVE_EXTRACT INPUT "${SQLITE_TOOLS_ZIP}" DESTINATION "${CMAKE_BINARY_DIR}")
endif()
if(NOT EXISTS "${SQLITE_TOOLS_EXE}")
  message(FATAL_ERROR "sqlite3.exe missing after tools extract under ${CMAKE_BINARY_DIR}")
endif()

add_library(sqlite3 STATIC "${SQLITE_SRC_DIR}/sqlite3.c")
target_include_directories(sqlite3 PUBLIC
  $<BUILD_INTERFACE:${SQLITE_SRC_DIR}>
  $<INSTALL_INTERFACE:include>
)
target_compile_definitions(sqlite3 PRIVATE
  SQLITE_ENABLE_RTREE
  SQLITE_ENABLE_COLUMN_METADATA
)
target_compile_definitions(sqlite3 PUBLIC SQLITE_CORE)
set_target_properties(sqlite3 PROPERTIES OUTPUT_NAME sqlite3)

include(GNUInstallDirs)
install(TARGETS sqlite3
  ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
  LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
)
install(FILES
  "${SQLITE_SRC_DIR}/sqlite3.h"
  "${SQLITE_SRC_DIR}/sqlite3ext.h"
  DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
)
install(PROGRAMS "${SQLITE_TOOLS_EXE}"
  DESTINATION ${CMAKE_INSTALL_BINDIR}
)

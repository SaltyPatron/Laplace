set(LAPLACE_CORE_HEADERS
    include/laplace/core/version.h
    include/laplace/core/math4d.h
    include/laplace/core/hash128.h
    include/laplace/core/text_identity.h
    include/laplace/core/ud_parse.h
    include/laplace/core/ud_witness.h
    include/laplace/core/task_shape.h
    include/laplace/core/hilbert4d.h
    include/laplace/core/mantissa.h
    include/laplace/core/super_fibonacci.h
    include/laplace/core/byte_atoms.h
    include/laplace/core/glicko2.h
    include/laplace/core/score.h
    include/laplace/core/codepoint_table.h
    include/laplace/core/chess_perfcache_format.h
    include/laplace/core/chess_position_table.h
    include/laplace/core/modality_number_perfcache_format.h
    include/laplace/core/modality_number_table.h
    include/laplace/core/vocabulary_perfcache_format.h
    include/laplace/core/vocabulary_table.h
    include/laplace/core/astar.h
    include/laplace/core/trajectory.h
    include/laplace/core/tier_tree.h
    include/laplace/core/intent_stage.h
    include/laplace/core/physicality_descriptor.h
    include/laplace/core/physicality_descriptor_admission.h
    include/laplace/core/ordered_composition.h
    include/laplace/core/merkle_dedup.h
    include/laplace/core/hash_composer.h
    include/laplace/core/grapheme_floor.h
    include/laplace/core/grammar_registry.h
    include/laplace/core/grammar_decomposer.h
    include/laplace/core/xml_stream.h
    include/laplace/core/fide_xml.h
    include/laplace/core/grammar_compose.h
    include/laplace/core/grammar_tags.h
    include/laplace/core/grapheme_break.h
    include/laplace/core/word_break.h
    include/laplace/core/sentence_break.h
    include/laplace/core/normalize_nfc.h
    include/laplace/core/identity_key.h
    include/laplace/core/text_decomposer.h
    include/laplace/core/content_witness_batch.h
    include/laplace/core/modality_atoms.h
    include/laplace/core/modality_decimal.h
    include/laplace/core/modality_witness.h
    include/laplace/core/image_decomposer.h
    include/laplace/core/audio_decomposer.h
    include/laplace/core/media_decode.h
    include/laplace/core/unicode_seed.h
    include/laplace/core/relation_law.h
    include/laplace/core/pos_law.h
    include/laplace/core/entity_type_law.h
    include/laplace/core/language_law.h
    include/laplace/core/deprel_law.h
    include/laplace/core/qualifier_law.h
    include/laplace/core/trust_class_law.h
    include/laplace/core/firmware_law.h
    include/laplace/core/highway_manifest.h
    include/laplace/core/highway_table.h
    include/laplace/core/attestation_engine.h
    include/laplace/core/partition_route.h
)

set(LAPLACE_CORE_SOURCES
    src/version.c
    src/math4d.c
    src/hash128.c
    src/text_identity.c
    src/native_text_tree.c
    src/ud_parse.c
    src/ud_witness.c
    src/task_shape.c
    src/hilbert4d.c
    src/mantissa.c
    src/super_fibonacci.c
    src/byte_atoms.c
    src/modality_decimal.c
    src/modality_witness.c
    src/image_decomposer.c
    src/audio_decomposer.c
    src/media_decode.c
    src/media_decode_impl.c
    src/media_decode_vorbis.c
    src/glicko2.c
    src/sql_catalog.c
    src/score.c
    src/codepoint_table.c
    src/unicode_properties.c
    src/chess_position_table.c
    src/chess_rules.c
    src/modality_number_table.c
    src/vocabulary_table.c
    src/astar.cpp
    src/trajectory.c
    src/tier_tree.c
    src/intent_stage.c
    src/staged_load.cpp
    src/physicality_descriptor.c
    src/physicality_descriptor_stage.c
    src/physicality_descriptor_read.c
    src/physicality_descriptor_provider.c
    src/physicality_descriptor_admission.cpp
    src/ordered_composition.c
    src/absence_filter.c
    src/merkle_dedup.c
    src/hash_composer.c
    src/grapheme_floor.c
    src/grammar_registry.c
    src/grammar_decomposer.c
    src/xml_stream.c
    src/fide_xml.c
    src/grammar_compose.cpp
    src/grammar_source_compose.c
    src/grammar_rows.c
    src/grammar_tags.c
    src/grapheme_break.c
    src/word_break.c
    src/sentence_break.c
    src/normalize_nfc.c
    src/identity_key.c
    src/text_decomposer.c
    src/content_witness_batch.c
    src/ucd_xml.cpp
    src/unicode_seed.cpp
    src/generated/relation_law.c
    src/generated/pos_law.c
    src/generated/entity_type_law.c
    src/generated/language_law.c
    src/generated/deprel_law.c
    src/generated/qualifier_law.c
    src/generated/trust_class_law.c
    src/generated/firmware_law.c
    src/highway_table.c
    src/attestation_engine.c
    src/partition_route.c
)
set(LAPLACE_PERFCACHE_DIR "${CMAKE_CURRENT_BINARY_DIR}/perfcache")
set(LAPLACE_HIGHWAY_BIN "${LAPLACE_PERFCACHE_DIR}/highway.bin"
    CACHE FILEPATH "Built highway perf-cache blob (consumed by tests + installed)" FORCE)

find_program(LAPLACE_CODEGEN_PYTHON NAMES python python3 REQUIRED)
# Use the same canonical input paths as the extension configure owner.
# Aliases containing /../ produce duplicate Ninja regeneration rules when the
# same manifest is also registered by its project-root path.
get_filename_component(_laplace_codegen_root "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)
set(LAPLACE_CODEGEN_SCRIPT "${_laplace_codegen_root}/scripts/codegen-attestation-law.py")
set(LAPLACE_CODEGEN_OUTPUTS
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/relation_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/pos_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/entity_type_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/language_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/deprel_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/qualifier_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/trust_class_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/firmware_law.h
    ${CMAKE_CURRENT_SOURCE_DIR}/include/laplace/core/highway_manifest.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/relation_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/pos_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/entity_type_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/language_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/deprel_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/qualifier_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/trust_class_law.c
    ${CMAKE_CURRENT_SOURCE_DIR}/src/generated/firmware_law.c
)
# The generated source/SQL files are also configure inputs. Making one of those
# files the custom-command timestamp creates a Ninja regeneration cycle when
# stable codegen correctly leaves an older, unchanged file untouched. The
# build-tree stamp owns freshness; configure-time codegen still guarantees the
# SQL inputs exist before their manifest hash is calculated.
set(LAPLACE_CODEGEN_STAMP
    "${CMAKE_CURRENT_BINARY_DIR}/perfcache/attestation-law.stamp")
# SQL manifests consume these generated seeds during configuration to derive
# the extension version. Generate them before extension/ is configured, including
# on a fresh checkout; a build-time custom command runs too late for that hash.
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${LAPLACE_CODEGEN_SCRIPT}"
    "${_laplace_codegen_root}/engine/manifest/relation_types.toml"
    "${_laplace_codegen_root}/engine/manifest/vocabulary/upos.tsv"
    "${_laplace_codegen_root}/engine/manifest/vocabulary/pos_alias.tsv"
    "${_laplace_codegen_root}/engine/manifest/entity_types.toml"
    "${_laplace_codegen_root}/engine/manifest/languages.tsv"
    "${_laplace_codegen_root}/engine/manifest/vocabulary/deprel.tsv"
    "${_laplace_codegen_root}/engine/manifest/vocabulary/deprel_subtype.tsv"
    "${_laplace_codegen_root}/engine/manifest/qualifiers.toml"
    "${_laplace_codegen_root}/engine/manifest/trust_classes.toml"
    "${_laplace_codegen_root}/engine/manifest/firmware.toml")
execute_process(
    COMMAND "${LAPLACE_CODEGEN_PYTHON}" "${LAPLACE_CODEGEN_SCRIPT}"
    RESULT_VARIABLE _laplace_codegen_result
    OUTPUT_VARIABLE _laplace_codegen_output
    ERROR_VARIABLE _laplace_codegen_error)
if(NOT "${_laplace_codegen_result}" STREQUAL "0")
    message(FATAL_ERROR "Attestation-law generation failed before SQL manifest hashing:\n${_laplace_codegen_output}\n${_laplace_codegen_error}")
endif()

add_custom_command(
    OUTPUT  "${LAPLACE_CODEGEN_STAMP}"
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    COMMAND ${LAPLACE_CODEGEN_PYTHON} ${LAPLACE_CODEGEN_SCRIPT}
    COMMAND ${CMAKE_COMMAND} -E touch "${LAPLACE_CODEGEN_STAMP}"
    DEPENDS ${LAPLACE_CODEGEN_SCRIPT}
            ${_laplace_codegen_root}/engine/manifest/relation_types.toml
            ${_laplace_codegen_root}/engine/manifest/vocabulary/upos.tsv
            ${_laplace_codegen_root}/engine/manifest/vocabulary/pos_alias.tsv
            ${_laplace_codegen_root}/engine/manifest/entity_types.toml
            ${_laplace_codegen_root}/engine/manifest/languages.tsv
            ${_laplace_codegen_root}/engine/manifest/vocabulary/deprel.tsv
            ${_laplace_codegen_root}/engine/manifest/vocabulary/deprel_subtype.tsv
            ${_laplace_codegen_root}/engine/manifest/qualifiers.toml
            ${_laplace_codegen_root}/engine/manifest/trust_classes.toml
            ${_laplace_codegen_root}/engine/manifest/firmware.toml
    COMMENT "Codegen attestation law and relation selectors from manifest TOML"
    VERBATIM
)
add_custom_target(laplace_attestation_codegen DEPENDS "${LAPLACE_CODEGEN_STAMP}")

# zlib for in-process ZIP inflate. Inflate output is deterministic, so any
# system or PostgreSQL zlib yields the same T0 input. On Windows it is located
# through PGBIN.
if(WIN32 AND NOT ZLIB_ROOT AND DEFINED ENV{PGBIN})
    get_filename_component(_laplace_pg_root "$ENV{PGBIN}" DIRECTORY)
    set(ZLIB_ROOT "${_laplace_pg_root}")
endif()
set_source_files_properties(src/hash128.c src/text_identity.c src/native_text_tree.c src/unicode_properties.c src/staged_load.cpp PROPERTIES
    INCLUDE_DIRECTORIES "${LAPLACE_NATIVE}/include")
set_source_files_properties(src/codepoint_table.c PROPERTIES
    INCLUDE_DIRECTORIES "${LAPLACE_NATIVE}/include;${LAPLACE_NATIVE}/src")

find_package(ZLIB REQUIRED)
find_package(LibXml2 REQUIRED)
if(NOT ICU_ROOT)
    set(ICU_ROOT "${LAPLACE_ICU_DIR}")
endif()
find_package(ICU 78 COMPONENTS uc i18n data REQUIRED)

add_library(laplace_core_objects OBJECT ${LAPLACE_CORE_SOURCES})
add_dependencies(laplace_core_objects laplace_attestation_codegen)
target_compile_definitions(laplace_core_objects PRIVATE
    LAPLACE_EXPECTED_UCD_VERSION="${LAPLACE_UNICODE_VERSION}")
target_include_directories(laplace_core_objects PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(laplace_core_objects PRIVATE
    laplace_eigen laplace_text blake3 ZLIB::ZLIB ICU::uc LibXml2::LibXml2)

# Both consumers use the same PIC objects. A source edit compiles once, while
# the PostgreSQL execution module can carry its own core instead of borrowing
# whichever shared core the postmaster loaded before deployment.
add_library(laplace_core SHARED $<TARGET_OBJECTS:laplace_core_objects>)
add_library(laplace_core_static STATIC $<TARGET_OBJECTS:laplace_core_objects>)
target_include_directories(laplace_core_static PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
target_link_libraries(laplace_core_static
    PUBLIC laplace_eigen
    PRIVATE laplace_text blake3 ZLIB::ZLIB ICU::uc LibXml2::LibXml2)

if(WIN32)
    # The separate Windows extension build imports the static archive. Carry its
    # Native and external dependencies from this graph instead of naming old
    # archives in that consumer (notably the former _deps/blake3 directory).
    file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/laplace-core-static-dependencies.cmake"
        CONTENT "set(LAPLACE_CORE_STATIC_DEPENDENCIES
  \"$<TARGET_FILE:laplace_text>\"
  \"$<TARGET_FILE:laplace_static>\"
  \"$<TARGET_FILE:blake3>\"
  \"$<TARGET_FILE:laplace_math>\"
  \"$<TARGET_FILE:tree_sitter_rt>\"
  \"$<TARGET_FILE:ZLIB::ZLIB>\"
  \"$<TARGET_FILE:ICU::uc>\"
  \"$<TARGET_FILE:ICU::i18n>\"
  \"$<TARGET_FILE:ICU::data>\"
  \"$<TARGET_FILE:LibXml2::LibXml2>\")
set(LAPLACE_CORE_STATIC_LINK_OPTIONS \"${LAPLACE_ENGINE_FP_LINK_FLAGS}\")
set(LAPLACE_CORE_STATIC_INCLUDE_DIRECTORIES \"${LAPLACE_NATIVE}/include\")
")
endif()


add_subdirectory(grammars)

# The managed host executes the canonical recipe compiler/ingester in its own
# process. Reuse that source and the Native targets already in this build graph;
# no second .format/.recipe parser or private recipe instruction language is built.
if(NOT DEFINED LAPLACE_RECIPE_ENGINE_SOURCE)
    get_filename_component(_laplace_native_checkout "${LAPLACE_NATIVE}" REALPATH)
    get_filename_component(_laplace_workspace "${_laplace_native_checkout}" DIRECTORY)
    set(LAPLACE_RECIPE_ENGINE_SOURCE "${_laplace_workspace}/Laplace-Engine"
        CACHE PATH "Canonical Laplace-Engine checkout for recipe execution")
endif()
if(NOT EXISTS "${LAPLACE_RECIPE_ENGINE_SOURCE}/src/record_stream.c"
   OR NOT EXISTS "${LAPLACE_RECIPE_ENGINE_SOURCE}/CMakeLists.txt")
    message(FATAL_ERROR "The canonical Laplace-Engine checkout is required at ${LAPLACE_RECIPE_ENGINE_SOURCE}. Set LAPLACE_RECIPE_ENGINE_SOURCE to its existing local checkout.")
endif()
add_subdirectory("${LAPLACE_RECIPE_ENGINE_SOURCE}" "${CMAKE_CURRENT_BINARY_DIR}/recipe_engine")
set(LAPLACE_RECIPE_RUNTIME "${CMAKE_CURRENT_BINARY_DIR}/recipe_engine/runtime")
file(GLOB_RECURSE _recipe_runtime_sources CONFIGURE_DEPENDS
    "${LAPLACE_RECIPE_ENGINE_SOURCE}/recipes/*.recipe"
    "${LAPLACE_RECIPE_ENGINE_SOURCE}/recipes/*.format"
    "${LAPLACE_RECIPE_ENGINE_SOURCE}/recipes/*/source")
file(GLOB _recipe_runtime_grammars CONFIGURE_DEPENDS
    "${LAPLACE_GRAMMARS}/libtree-sitter-*${CMAKE_SHARED_LIBRARY_SUFFIX}")
add_custom_command(OUTPUT "${LAPLACE_RECIPE_RUNTIME}/.complete"
    COMMAND ${CMAKE_COMMAND} --install "${CMAKE_BINARY_DIR}"
            --component LaplaceRecipeRuntime --prefix "${LAPLACE_RECIPE_RUNTIME}"
    COMMAND ${CMAKE_COMMAND} -E touch "${LAPLACE_RECIPE_RUNTIME}/.complete"
    DEPENDS laplace_engine ${_recipe_runtime_sources} ${_recipe_runtime_grammars}
            "${LAPLACE_RECIPE_ENGINE_SOURCE}/recipes/order"
            "${CMAKE_CURRENT_BINARY_DIR}/recipe_engine/cmake_install.cmake"
    COMMENT "Package canonical Native recipe runtime" VERBATIM)
add_custom_target(laplace_recipe_runtime DEPENDS "${LAPLACE_RECIPE_RUNTIME}/.complete")
add_dependencies(laplace_core laplace_recipe_runtime)
target_link_libraries(laplace_core PRIVATE laplace_text tree_sitter_rt ${LAPLACE_TS_GRAMMARS})
target_link_libraries(laplace_core_objects PRIVATE tree_sitter_rt ${LAPLACE_TS_GRAMMARS})
target_link_libraries(laplace_core_static PRIVATE tree_sitter_rt ${LAPLACE_TS_GRAMMARS})
set(LAPLACE_PERFCACHE_BIN "${LAPLACE_PERFCACHE_DIR}/tier0.bin"
    CACHE FILEPATH "Canonical Native scalar cache for tests and installation" FORCE)
add_executable(laplace_tier0 "${LAPLACE_NATIVE}/tools/tier0.c")
set_target_properties(laplace_tier0 PROPERTIES OUTPUT_NAME laplace-tier0)
target_link_libraries(laplace_tier0 PRIVATE laplace_static)

if(EXISTS "${LAPLACE_TIER0}" AND NOT "${LAPLACE_TIER0}" STREQUAL "${LAPLACE_PERFCACHE_BIN}")
    add_custom_command(OUTPUT "${LAPLACE_PERFCACHE_BIN}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${LAPLACE_TIER0}" "${LAPLACE_PERFCACHE_BIN}"
        DEPENDS "${LAPLACE_TIER0}"
        COMMENT "Package the existing canonical Native tier0.bin"
        VERBATIM)
else()
    add_custom_command(OUTPUT "${LAPLACE_PERFCACHE_BIN}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
        COMMAND $<TARGET_FILE:laplace_tier0> "${LAPLACE_UCD_PATH}" "${LAPLACE_PERFCACHE_BIN}"
        DEPENDS laplace_tier0
        COMMENT "Generate canonical Native tier0.bin from the local Unicode estate"
        VERBATIM)
endif()

# Perfcache emit is an explicit target, not part of the default build:
#   ninja laplace_t0_perfcache laplace_highway_perfcache
# then cmake --install. Tier-0 is a mapped Native floor, never database seed rows.
set(LAPLACE_FLAGS_BIN "${LAPLACE_PERFCACHE_DIR}/tier0.flags")
set(_native_flags_input "${LAPLACE_FLAGS}")
if(NOT _native_flags_input)
    set(_native_flags_input "$ENV{LAPLACE_FLAGS}")
endif()
if(NOT _native_flags_input)
    get_filename_component(_native_tier0_dir "${LAPLACE_TIER0}" DIRECTORY)
    set(_native_flags_input "${_native_tier0_dir}/tier0.flags")
endif()
if(EXISTS "${_native_flags_input}" AND EXISTS "${_native_flags_input}.layout"
   AND NOT "${_native_flags_input}" STREQUAL "${LAPLACE_FLAGS_BIN}")
    add_custom_command(OUTPUT "${LAPLACE_FLAGS_BIN}" "${LAPLACE_FLAGS_BIN}.layout"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_native_flags_input}" "${LAPLACE_FLAGS_BIN}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_native_flags_input}.layout" "${LAPLACE_FLAGS_BIN}.layout"
        DEPENDS "${_native_flags_input}" "${_native_flags_input}.layout"
        COMMENT "Package canonical Native Unicode flags and their layout" VERBATIM)
else()
    add_custom_command(OUTPUT "${LAPLACE_FLAGS_BIN}" "${LAPLACE_FLAGS_BIN}.layout"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
        COMMAND ${CMAKE_COMMAND} -E env "LAPLACE_TIER0=${LAPLACE_PERFCACHE_BIN}"
                $<TARGET_FILE:laplace_engine> flags -u "${LAPLACE_UCD_PATH}" -o "${LAPLACE_FLAGS_BIN}"
        DEPENDS laplace_engine "${LAPLACE_UCD_PATH}/ucd/PropertyAliases.txt"
                "${LAPLACE_UCD_PATH}/ucd/PropertyValueAliases.txt"
                "${LAPLACE_UCD_PATH}/ucdxml/ucd.all.flat.xml"
        COMMENT "Generate Native Unicode flags from the local Unicode estate" VERBATIM)
endif()
add_custom_target(laplace_t0_perfcache DEPENDS "${LAPLACE_PERFCACHE_BIN}"
    "${LAPLACE_FLAGS_BIN}" "${LAPLACE_FLAGS_BIN}.layout")
# A caller's installed highway may use an older binary layout. Generate with the
# selected Engine/Native pair so publication cannot copy a stale ambient cache.
file(GLOB_RECURSE _native_highway_manifests CONFIGURE_DEPENDS
    "${LAPLACE_NATIVE}/manifest/*.tsv")
add_custom_command(OUTPUT "${LAPLACE_HIGHWAY_BIN}"
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    COMMAND ${CMAKE_COMMAND} -E env
            "LAPLACE_TIER0=${LAPLACE_PERFCACHE_BIN}"
            "LAPLACE_HIGHWAY=${LAPLACE_HIGHWAY_BIN}"
            "LAPLACE_MANIFEST=${LAPLACE_NATIVE}/manifest"
            $<TARGET_FILE:laplace_engine> highway -o "${LAPLACE_HIGHWAY_BIN}"
    DEPENDS laplace_engine laplace_t0_perfcache ${_native_highway_manifests}
    COMMENT "Generate canonical Native highway from frozen identifier manifests"
    VERBATIM)
add_custom_target(laplace_highway_perfcache DEPENDS "${LAPLACE_HIGHWAY_BIN}")

# Chess compose floor (typed state/move atoms + bounded moves + catalog positions).
# Catalog text is generator interchange only; the emitter parses it into binary state atoms
# before identity/geometry. It is never admitted as a sentence or chess content.
add_subdirectory(tools/chess_position_tables_emit)
if(NOT LAPLACE_CHESS_OPENINGS AND DEFINED ENV{LAPLACE_CHESS_OPENINGS})
    set(LAPLACE_CHESS_OPENINGS "$ENV{LAPLACE_CHESS_OPENINGS}")
endif()
set(LAPLACE_CHESS_OPENINGS "${LAPLACE_CHESS_OPENINGS}" CACHE PATH
    "Optional ECO openings TSV dir for extra tier-2 catalog position coverage")
set(LAPLACE_CHESS_SURFACES "${LAPLACE_PERFCACHE_DIR}/chess_catalog_surfaces.txt")
set(LAPLACE_CHESS_TRANSITION_BIN
    "${LAPLACE_PERFCACHE_DIR}/laplace_chess_transition_perfcache.bin"
    CACHE FILEPATH "Built chess transition-floor blob" FORCE)
set(LAPLACE_CHESS_PERFCACHE_BIN "${LAPLACE_PERFCACHE_DIR}/laplace_chess_position_perfcache.bin"
    CACHE FILEPATH "Built chess compose-floor blob" FORCE)
find_program(LAPLACE_DOTNET NAMES dotnet)
set(LAPLACE_CHESS_SURFACES_PROJ
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/ChessCatalogSurfaces/ChessCatalogSurfaces.csproj")
# The managed generator is part of this native/perfcache build graph. Depending
# on its csproj alone leaves an existing output fresh when Program, compose,
# hashing, or the openings parser changes. CONFIGURE_DEPENDS makes additions and
# removals visible too; Ninja then reruns only this generator and its consumer.
file(GLOB_RECURSE LAPLACE_CHESS_SURFACES_MANAGED_SOURCES CONFIGURE_DEPENDS
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/ChessCatalogSurfaces/*.cs"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/ChessCatalogSurfaces/*.csproj"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Chess/*.cs"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Chess/*.csproj"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Core/*.cs"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Core/*.csproj"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Substrate/*.cs"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../app/Laplace.Substrate/*.csproj")
list(FILTER LAPLACE_CHESS_SURFACES_MANAGED_SOURCES EXCLUDE REGEX "/(bin|obj)/")

# Recorded inputs are an explicit completed export, never an implicit database read during a build.
set(LAPLACE_CHESS_CORPUS_EXPORT "" CACHE FILEPATH "Completed recorded chess floor export receipt")
set(LAPLACE_CHESS_PERFCACHE_MEMORY_BYTES "67108864" CACHE STRING "Controlled native chess producer memory bytes")
set(LAPLACE_CHESS_PERFCACHE_SPILL_BYTES "4294967296" CACHE STRING "Maximum owned chess producer spill bytes")
set(LAPLACE_CHESS_TRANSITION_BUFFER_RECORDS "65536" CACHE STRING "Buffered transition records per sorted run")
set(LAPLACE_CHESS_TRANSITION_MERGE_FAN_IN "32" CACHE STRING "Maximum transition merge readers")
set(_chess_artifact_script "${_laplace_codegen_root}/scripts/chess-floor-artifacts.py")
set(_chess_corpus_deps)
set(_chess_corpus_surfaces)
set(_chess_corpus_transitions)
set(_chess_seal_export_args)
if(LAPLACE_CHESS_CORPUS_EXPORT)
    if(NOT UNIX)
        message(FATAL_ERROR "Recorded corpus floor activation currently requires the verified UNIX pair publisher")
    endif()
    execute_process(
        COMMAND "${LAPLACE_CODEGEN_PYTHON}" "${_chess_artifact_script}" validate-export
                --receipt "${LAPLACE_CHESS_CORPUS_EXPORT}"
        RESULT_VARIABLE _chess_export_rc OUTPUT_VARIABLE _chess_export_json
        ERROR_VARIABLE _chess_export_error OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT _chess_export_rc EQUAL 0)
        message(FATAL_ERROR "Recorded chess floor input refused: ${_chess_export_error}")
    endif()
    # Receipt acceptance has one owner. CMake consumes its authenticated role paths.
    string(JSON _chess_corpus_surfaces GET "${_chess_export_json}" roles position-surfaces)
    string(JSON _chess_corpus_transitions GET "${_chess_export_json}" roles transition-floor)
    foreach(_role IN ITEMS position-surfaces transition-floor inventory-summary witnessed-inputs)
        string(JSON _input GET "${_chess_export_json}" roles "${_role}")
        list(APPEND _chess_corpus_deps "${_input}")
    endforeach()
    list(APPEND _chess_corpus_deps "${LAPLACE_CHESS_CORPUS_EXPORT}")
    list(APPEND _chess_seal_export_args --export-receipt "${LAPLACE_CHESS_CORPUS_EXPORT}")
endif()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
    "${_chess_artifact_script}" ${_chess_corpus_deps})

set(_chess_emit_deps $<TARGET_FILE:laplace_chess_position_tables_emit> ${_chess_corpus_deps})
set(_chess_emit_cmd
    COMMAND $<TARGET_FILE:laplace_chess_position_tables_emit>
            --output ${LAPLACE_CHESS_PERFCACHE_BIN}
            --memory-bytes ${LAPLACE_CHESS_PERFCACHE_MEMORY_BYTES}
            --scratch-dir "${LAPLACE_PERFCACHE_DIR}/position-sort"
            --maximum-spill-bytes ${LAPLACE_CHESS_PERFCACHE_SPILL_BYTES})
if(_chess_corpus_surfaces)
    list(APPEND _chess_emit_cmd --additional-surfaces "${_chess_corpus_surfaces}")
endif()

if(LAPLACE_DOTNET AND EXISTS "${LAPLACE_CHESS_SURFACES_PROJ}")
    set(_chess_openings_deps)
    if(LAPLACE_CHESS_OPENINGS AND EXISTS "${LAPLACE_CHESS_OPENINGS}")
        set(_chess_openings_arg "${LAPLACE_CHESS_OPENINGS}")
        if(IS_DIRECTORY "${LAPLACE_CHESS_OPENINGS}")
            file(GLOB_RECURSE _chess_openings_deps CONFIGURE_DEPENDS
                "${LAPLACE_CHESS_OPENINGS}/*.tsv")
        else()
            set(_chess_openings_deps "${LAPLACE_CHESS_OPENINGS}")
        endif()
    else()
        set(_chess_openings_arg "NONE")
    endif()
    set(_chess_openings_manifest "${_chess_openings_arg}\n")
    foreach(_opening_file IN LISTS _chess_openings_deps)
        file(SHA256 "${_opening_file}" _opening_hash)
        string(APPEND _chess_openings_manifest "${_opening_hash} ${_opening_file}\n")
    endforeach()
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS ${_chess_openings_deps})
    set(_chess_openings_manifest_path "${LAPLACE_PERFCACHE_DIR}/chess_openings_inputs.txt")
    file(GENERATE OUTPUT "${_chess_openings_manifest_path}" CONTENT "${_chess_openings_manifest}")
    set(_chess_seed_transitions "${LAPLACE_PERFCACHE_DIR}/chess_opening_transitions.bin")
    add_custom_command(
        OUTPUT ${LAPLACE_CHESS_SURFACES} "${_chess_seed_transitions}"
        COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
        COMMAND ${LAPLACE_DOTNET} run --project "${LAPLACE_CHESS_SURFACES_PROJ}"
                -c Release --no-launch-profile --
                "${_chess_openings_arg}" "${LAPLACE_CHESS_SURFACES}"
                "${_chess_seed_transitions}"
        DEPENDS laplace_core ${LAPLACE_CHESS_SURFACES_MANAGED_SOURCES}
                ${_chess_openings_deps} "${_chess_openings_manifest_path}"
        WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
        COMMENT "Chess catalog seed surfaces and transitions"
        VERBATIM
    )
    set(_chess_transition_inputs --input "${_chess_seed_transitions}")
    if(_chess_corpus_transitions)
        list(APPEND _chess_transition_inputs --input "${_chess_corpus_transitions}")
    endif()
    add_custom_command(
        OUTPUT ${LAPLACE_CHESS_TRANSITION_BIN}
        COMMAND ${LAPLACE_DOTNET} run --project "${LAPLACE_CHESS_SURFACES_PROJ}"
                -c Release --no-launch-profile -- merge-transition-floors
                --output "${LAPLACE_CHESS_TRANSITION_BIN}"
                --work-directory "${LAPLACE_PERFCACHE_DIR}/transition-sort"
                --maximum-buffered-records ${LAPLACE_CHESS_TRANSITION_BUFFER_RECORDS}
                --merge-fan-in ${LAPLACE_CHESS_TRANSITION_MERGE_FAN_IN}
                --maximum-spill-bytes ${LAPLACE_CHESS_PERFCACHE_SPILL_BYTES}
                ${_chess_transition_inputs}
        DEPENDS "${_chess_seed_transitions}" ${_chess_corpus_deps}
                ${LAPLACE_CHESS_SURFACES_MANAGED_SOURCES}
        WORKING_DIRECTORY "${CMAKE_SOURCE_DIR}"
        COMMENT "Merging bounded seed and recorded-corpus transition floor"
        VERBATIM
    )
    list(APPEND _chess_emit_deps ${LAPLACE_CHESS_SURFACES})
    list(APPEND _chess_emit_cmd --surfaces ${LAPLACE_CHESS_SURFACES})
elseif(LAPLACE_CHESS_CORPUS_EXPORT)
    message(FATAL_ERROR "Recorded chess floor inputs require the existing ChessCatalogSurfaces dotnet owner")
endif()

add_custom_command(
    OUTPUT ${LAPLACE_CHESS_PERFCACHE_BIN}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    ${_chess_emit_cmd}
    DEPENDS ${_chess_emit_deps}
    COMMENT "Emitting bounded chess compose floor from declared seed and recorded inputs"
    VERBATIM
)
if(UNIX AND LAPLACE_DOTNET AND EXISTS "${LAPLACE_CHESS_SURFACES_PROJ}")
    set(LAPLACE_CHESS_PAIR_RECEIPT "${LAPLACE_PERFCACHE_DIR}/chess-floor-pair.json"
        CACHE FILEPATH "Authenticated complete chess floor pair" FORCE)
    add_custom_command(
        OUTPUT "${LAPLACE_CHESS_PAIR_RECEIPT}"
        COMMAND "${LAPLACE_CODEGEN_PYTHON}" "${_chess_artifact_script}" seal-pair
                --position "${LAPLACE_CHESS_PERFCACHE_BIN}"
                --transition "${LAPLACE_CHESS_TRANSITION_BIN}"
                --native-library $<TARGET_FILE:laplace_core>
                --position-emitter $<TARGET_FILE:laplace_chess_position_tables_emit>
                --seed-surfaces "${LAPLACE_CHESS_SURFACES}"
                --receipt "${LAPLACE_CHESS_PAIR_RECEIPT}"
                ${_chess_seal_export_args}
        DEPENDS "${LAPLACE_CHESS_PERFCACHE_BIN}" "${LAPLACE_CHESS_TRANSITION_BIN}"
                "${LAPLACE_CHESS_SURFACES}" "${_chess_artifact_script}"
                $<TARGET_FILE:laplace_core> $<TARGET_FILE:laplace_chess_position_tables_emit>
                ${_chess_corpus_deps}
        COMMENT "Authenticating the complete chess floor generation"
        VERBATIM
    )
    add_custom_target(laplace_chess_position_perfcache DEPENDS "${LAPLACE_CHESS_PAIR_RECEIPT}")
    install(CODE "
        execute_process(
            COMMAND \"${LAPLACE_CODEGEN_PYTHON}\" \"${_chess_artifact_script}\" install-pair
                    --prefix \"\${CMAKE_INSTALL_PREFIX}\"
                    --position \"${LAPLACE_CHESS_PERFCACHE_BIN}\"
                    --transition \"${LAPLACE_CHESS_TRANSITION_BIN}\"
                    --native-library \"$<TARGET_FILE:laplace_core>\"
                    --pair-receipt \"${LAPLACE_CHESS_PAIR_RECEIPT}\"
                    --manifest-only
            RESULT_VARIABLE _chess_install_rc OUTPUT_VARIABLE _chess_install_paths
            ERROR_VARIABLE _chess_install_error OUTPUT_STRIP_TRAILING_WHITESPACE)
        if(NOT _chess_install_rc EQUAL 0)
            message(FATAL_ERROR \"Chess floor pair installation failed: \${_chess_install_error}\")
        endif()
        string(REPLACE \"\\n\" \";\" _chess_manifest_files \"\${_chess_install_paths}\")
        list(APPEND CMAKE_INSTALL_MANIFEST_FILES \${_chess_manifest_files})
    ")
else()
    # Without the recorded-corpus pair activation above (UNIX only), the native seed
    # blobs install directly.
    add_custom_target(laplace_chess_position_perfcache DEPENDS ${LAPLACE_CHESS_PERFCACHE_BIN})
    install(FILES ${LAPLACE_CHESS_PERFCACHE_BIN}
        DESTINATION share/laplace RENAME "laplace_chess_position_perfcache.bin"
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS})
    if(LAPLACE_DOTNET AND EXISTS "${LAPLACE_CHESS_SURFACES_PROJ}")
        add_custom_target(laplace_chess_transition_perfcache DEPENDS ${LAPLACE_CHESS_TRANSITION_BIN})
        install(FILES ${LAPLACE_CHESS_TRANSITION_BIN}
            DESTINATION share/laplace RENAME "laplace_chess_transition_perfcache.bin"
            PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS})
    endif()
endif()

# Canonical number ROM (dense 0..255): each integer is the composition of its T0 digit
# atoms, emitted from T0 alone. Any modality that reuses these integers reads the same
# roots from it.
add_subdirectory(tools/modality_number_tables_emit)
set(LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN
    "${LAPLACE_PERFCACHE_DIR}/laplace_modality_number_perfcache.bin"
    CACHE FILEPATH "Built modality number compose-floor blob" FORCE)
add_custom_command(
    OUTPUT ${LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    COMMAND $<TARGET_FILE:laplace_modality_number_tables_emit>
            --t0     ${LAPLACE_PERFCACHE_BIN}
            --output ${LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN}
    DEPENDS ${LAPLACE_PERFCACHE_BIN} $<TARGET_FILE:laplace_modality_number_tables_emit>
    COMMENT "Emitting modality number compose-floor blob (0..255 channel bytes)"
    VERBATIM
)
add_custom_target(laplace_modality_number_perfcache
    DEPENDS ${LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN})
install(FILES ${LAPLACE_MODALITY_NUMBER_PERFCACHE_BIN}
    DESTINATION share/laplace
    RENAME "laplace_modality_number_perfcache.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

# Governed vocabulary ROM: UPOS, universal relations and their subtypes, features and
# feature values (engine/manifest/vocabulary, generated from their authorities), each
# composed from T0 by the same kernels a recipe uses.
add_subdirectory(tools/vocabulary_tables_emit)
set(LAPLACE_VOCABULARY_PERFCACHE_BIN
    "${LAPLACE_PERFCACHE_DIR}/laplace_vocabulary_perfcache.bin"
    CACHE FILEPATH "Built governed vocabulary blob" FORCE)
set(_laplace_vocabulary_manifest "${_laplace_codegen_root}/engine/manifest/vocabulary")
add_custom_command(
    OUTPUT ${LAPLACE_VOCABULARY_PERFCACHE_BIN}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${LAPLACE_PERFCACHE_DIR}"
    COMMAND $<TARGET_FILE:laplace_vocabulary_tables_emit>
            --t0       ${LAPLACE_PERFCACHE_BIN}
            --manifest ${_laplace_vocabulary_manifest}
            --output   ${LAPLACE_VOCABULARY_PERFCACHE_BIN}
    DEPENDS ${LAPLACE_PERFCACHE_BIN} $<TARGET_FILE:laplace_vocabulary_tables_emit>
            ${_laplace_vocabulary_manifest}/upos.tsv
            ${_laplace_vocabulary_manifest}/deprel.tsv
            ${_laplace_vocabulary_manifest}/deprel_subtype.tsv
            ${_laplace_vocabulary_manifest}/feature.tsv
            ${_laplace_vocabulary_manifest}/feature_value.tsv
    COMMENT "Emitting governed vocabulary blob"
    VERBATIM
)
add_custom_target(laplace_vocabulary_perfcache
    DEPENDS ${LAPLACE_VOCABULARY_PERFCACHE_BIN})
install(FILES ${LAPLACE_VOCABULARY_PERFCACHE_BIN}
    DESTINATION share/laplace
    RENAME "laplace_vocabulary_perfcache.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

set(LAPLACE_PERFCACHE_REFERENCE_BIN "${LAPLACE_PERFCACHE_DIR}/tier0.reference.bin")
add_custom_target(laplace_verify_perfcache_determinism
    COMMAND $<TARGET_FILE:laplace_tier0> "${LAPLACE_UCD_PATH}" "${LAPLACE_PERFCACHE_REFERENCE_BIN}"
    COMMAND ${CMAKE_COMMAND} -E compare_files ${LAPLACE_PERFCACHE_BIN} ${LAPLACE_PERFCACHE_REFERENCE_BIN}
    DEPENDS laplace_t0_perfcache laplace_tier0
    COMMENT "Verify T0 perf-cache emit is byte-deterministic"
    VERBATIM
)

set_target_properties(laplace_core PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_core
)

target_include_directories(laplace_core
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)

target_link_libraries(laplace_core
    PUBLIC  laplace_eigen
    PRIVATE blake3 ZLIB::ZLIB ICU::uc LibXml2::LibXml2
)
if(NOT WIN32)
    find_package(Threads REQUIRED)
    target_link_libraries(laplace_core PRIVATE Threads::Threads)
    target_link_libraries(laplace_core_static PRIVATE Threads::Threads)
    target_link_libraries(laplace_core_objects PRIVATE Threads::Threads)
endif()

# The Syzygy prober is a separate shared library, not part of laplace_core (which is
# loaded into every PostgreSQL backend through laplace_substrate). It is loaded by the
# ingest side as a calculation witness and never by the database.
add_library(laplace_syzygy SHARED src/syzygy.c)
target_include_directories(laplace_syzygy
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
)
target_link_libraries(laplace_syzygy PRIVATE fathom_tb)
set_target_properties(laplace_syzygy PROPERTIES
    VERSION ${PROJECT_VERSION}
    SOVERSION ${PROJECT_VERSION_MAJOR}
    OUTPUT_NAME laplace_syzygy
)
install(TARGETS laplace_syzygy
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
    RUNTIME
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)

if(BUILD_TESTING)
    add_subdirectory(tests)
endif()

install(TARGETS laplace_core
    LIBRARY
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_EXEC_PERMS}
)

# Ship the linker PDB alongside the DLL so a deployed native crash symbolizes.
# Windows/MSVC only — TARGET_PDB_FILE is invalid on Linux CI (breaks cmake --generate).
if(WIN32)
    install(FILES $<TARGET_PDB_FILE:laplace_core>
        DESTINATION lib
        PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
        OPTIONAL
    )
endif()

install(FILES ${LAPLACE_PERFCACHE_BIN}
    DESTINATION share/laplace
    RENAME "tier0.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

install(FILES "${LAPLACE_FLAGS_BIN}" "${LAPLACE_FLAGS_BIN}.layout"
    DESTINATION share/laplace
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

install(FILES ${LAPLACE_HIGHWAY_BIN}
    DESTINATION share/laplace
    RENAME "highway.bin"
    PERMISSIONS ${LAPLACE_INSTALL_FILE_PERMS}
)

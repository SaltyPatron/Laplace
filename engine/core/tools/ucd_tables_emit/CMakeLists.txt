# engine/core/tools/ucd_tables_emit/CMakeLists.txt
#
# Build-time codegen tool that reads UCD .txt files and emits compact C
# lookup tables for UAX#29 segmentation (Grapheme/Word/Sentence break
# property) + NFC normalization (CCC, canonical decomposition,
# composition exclusions) — per ADR 0047 + ADR 0053. The emit tool runs
# at build time; the generated C source links into liblaplace_core.

add_executable(laplace_ucd_tables_emit
    main.cpp
)

target_compile_features(laplace_ucd_tables_emit PRIVATE cxx_std_20)

# Built once per build; does NOT link into the engine .so. Pure host
# tool consumed by the add_custom_command below.
set_target_properties(laplace_ucd_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

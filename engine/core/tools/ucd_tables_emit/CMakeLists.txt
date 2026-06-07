add_executable(laplace_ucd_tables_emit
    main.cpp
)

target_compile_features(laplace_ucd_tables_emit PRIVATE cxx_std_20)

target_include_directories(laplace_ucd_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

find_package(LibXml2 REQUIRED)
target_link_libraries(laplace_ucd_tables_emit PRIVATE
    laplace_core
    LibXml2::LibXml2)

set_target_properties(laplace_ucd_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")

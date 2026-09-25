add_executable(laplace_vocabulary_tables_emit
    main.cpp
)

target_compile_features(laplace_vocabulary_tables_emit PRIVATE cxx_std_20)

target_include_directories(laplace_vocabulary_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)

target_link_libraries(laplace_vocabulary_tables_emit PRIVATE
    laplace_core)

set_target_properties(laplace_vocabulary_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")

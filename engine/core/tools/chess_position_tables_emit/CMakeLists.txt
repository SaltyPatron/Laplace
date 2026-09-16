add_executable(laplace_chess_position_tables_emit
    main.cpp
    producer.cpp
)
target_compile_features(laplace_chess_position_tables_emit PRIVATE cxx_std_20)
target_include_directories(laplace_chess_position_tables_emit PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/../../include)
target_link_libraries(laplace_chess_position_tables_emit PRIVATE laplace_core blake3)
set_target_properties(laplace_chess_position_tables_emit PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")

if(BUILD_TESTING)
    add_executable(laplace_chess_position_tables_emit_tests
        producer_tests.cpp
        producer.cpp)
    target_compile_features(laplace_chess_position_tables_emit_tests PRIVATE cxx_std_20)
    target_include_directories(laplace_chess_position_tables_emit_tests PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/../../include)
    target_link_libraries(laplace_chess_position_tables_emit_tests PRIVATE laplace_core blake3)
    add_test(NAME chess-position-producer.contract
        COMMAND laplace_chess_position_tables_emit_tests)
    set_tests_properties(chess-position-producer.contract PROPERTIES TIMEOUT 180)
endif()

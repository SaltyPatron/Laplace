if(EXISTS "/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests")
  if(NOT EXISTS "/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests[1]_tests.cmake" OR
     NOT "/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests[1]_tests.cmake" IS_NEWER_THAN "/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests" OR
     NOT "/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests[1]_tests.cmake" IS_NEWER_THAN "${CMAKE_CURRENT_LIST_FILE}")
    include("/usr/share/cmake-3.22/Modules/GoogleTestAddTests.cmake")
    gtest_discover_tests_impl(
      TEST_EXECUTABLE [==[/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests]==]
      TEST_EXECUTOR [==[]==]
      TEST_WORKING_DIR [==[/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests]==]
      TEST_EXTRA_ARGS [==[]==]
      TEST_PROPERTIES [==[]==]
      TEST_PREFIX [==[]==]
      TEST_SUFFIX [==[]==]
      TEST_FILTER [==[]==]
      NO_PRETTY_TYPES [==[FALSE]==]
      NO_PRETTY_VALUES [==[FALSE]==]
      TEST_LIST [==[laplace_syzygy_tests_TESTS]==]
      CTEST_FILE [==[/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests[1]_tests.cmake]==]
      TEST_DISCOVERY_TIMEOUT [==[120]==]
      TEST_XML_OUTPUT_DIR [==[]==]
    )
  endif()
  include("/home/ahart/Projects/Laplace-Legacy/recovery/native-build/engine/core/tests/laplace_syzygy_tests[1]_tests.cmake")
else()
  add_test(laplace_syzygy_tests_NOT_BUILT laplace_syzygy_tests_NOT_BUILT)
endif()

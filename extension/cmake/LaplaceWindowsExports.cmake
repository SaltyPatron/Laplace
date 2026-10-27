# Source removals must also invalidate CMake's generated all-symbol export cache.
# Otherwise an incremental Windows link can retain exports from a removed object.
function(laplace_windows_exports target)
    if(NOT WIN32)
        return()
    endif()
    set_target_properties(${target} PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS TRUE)
    get_target_property(sources ${target} SOURCES)
    string(SHA256 signature "${sources}")
    set(stamp "${CMAKE_CURRENT_BINARY_DIR}/CMakeFiles/${target}.exports-roster")
    set(previous "")
    if(EXISTS "${stamp}")
        file(READ "${stamp}" previous)
    endif()
    if(NOT previous STREQUAL signature)
        file(GLOB generated_exports
            "${CMAKE_CURRENT_BINARY_DIR}/CMakeFiles/${target}.dir/exports.def"
            "${CMAKE_CURRENT_BINARY_DIR}/CMakeFiles/${target}.dir/*/exports.def")
        if(generated_exports)
            file(REMOVE ${generated_exports})
        endif()
        file(WRITE "${stamp}" "${signature}")
    endif()
endfunction()

# CMake's auto-export scan can keep exports.def after an object is removed:
# the remaining objects are older even though exports.def.objs has changed.
# Refresh only when the DLL is already being linked; unchanged targets stay current.
function(laplace_refresh_windows_exports target)
    if(WIN32)
        add_custom_command(TARGET ${target} PRE_LINK
            COMMAND ${CMAKE_COMMAND} -E rm -f
                "${CMAKE_CURRENT_BINARY_DIR}/CMakeFiles/${target}.dir/${CMAKE_CFG_INTDIR}/exports.def"
            VERBATIM)
    endif()
endfunction()

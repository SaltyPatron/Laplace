

function(laplace_write_sql_chain chain_file manifest_file src_sql_dir)
    if(NOT EXISTS "${manifest_file}")
        message(FATAL_ERROR "SQL manifest not found: ${manifest_file}")
    endif()
    file(STRINGS "${manifest_file}" _lines)
    set(_chain "")
    set(_deps "")
    set(_hash [[#]])
    foreach(_line IN LISTS _lines)
        string(STRIP "${_line}" _line)
        if(_line STREQUAL "")
            continue()
        endif()
        string(FIND "${_line}" "${_hash}" _hash_pos)
        if(_hash_pos EQUAL 0)
            continue()
        endif()
        string(APPEND _chain "#include \"${_line}\"\n")
        list(APPEND _deps "${src_sql_dir}/${_line}")
    endforeach()
    file(WRITE "${chain_file}" "${_chain}")
    set(SQL_CHAIN_DEPS "${_deps}" PARENT_SCOPE)
endfunction()

# Return, in <out_var>, the absolute paths of every module a manifest references
# (blank lines and #-comments skipped).
function(laplace_manifest_files out_var manifest_file src_sql_dir)
    if(NOT EXISTS "${manifest_file}")
        message(FATAL_ERROR "SQL manifest not found: ${manifest_file}")
    endif()
    file(STRINGS "${manifest_file}" _lines)
    set(_files "")
    set(_hash [[#]])
    foreach(_line IN LISTS _lines)
        string(STRIP "${_line}" _line)
        if(_line STREQUAL "")
            continue()
        endif()
        string(FIND "${_line}" "${_hash}" _hpos)
        if(_hpos EQUAL 0)
            continue()
        endif()
        list(APPEND _files "${src_sql_dir}/${_line}")
    endforeach()
    set(${out_var} "${_files}" PARENT_SCOPE)
endfunction()

# Fail configuration if any *.sql.in under src_sql_dir is neither referenced by a
# manifest (install/upgrade) nor allow-listed (build shims / config templates /
# sql/.manifest_ignore entries). This is the structural lock that makes the
# legacy numbered-monolith / orphaned-module pattern impossible to reintroduce:
# a stray file that ships nothing (or, worse, silently ships nothing while the
# author believes it does) breaks the build instead of rotting unnoticed.
function(laplace_check_manifest_complete src_sql_dir)
    set(_referenced ${ARGN})   # remaining args = already-resolved module abs paths
    set(_allowed
        "${src_sql_dir}/laplace_substrate.sql.in"
        "${src_sql_dir}/laplace_substrate_upgrade.sql.in"
        "${src_sql_dir}/uninstall_laplace_substrate.sql.in"
        "${src_sql_dir}/sqldefines.h.in")
    if(EXISTS "${src_sql_dir}/.manifest_ignore")
        file(STRINGS "${src_sql_dir}/.manifest_ignore" _ign)
        foreach(_i IN LISTS _ign)
            string(STRIP "${_i}" _i)
            if(NOT _i STREQUAL "" AND NOT _i MATCHES "^#")
                list(APPEND _allowed "${src_sql_dir}/${_i}")
            endif()
        endforeach()
    endif()
    file(GLOB_RECURSE _all_sql CONFIGURE_DEPENDS "${src_sql_dir}/*.sql.in")
    set(_orphans "")
    foreach(_f IN LISTS _all_sql)
        list(FIND _referenced "${_f}" _ri)
        list(FIND _allowed "${_f}" _ai)
        if(_ri EQUAL -1 AND _ai EQUAL -1)
            list(APPEND _orphans "${_f}")
        endif()
    endforeach()
    if(_orphans)
        string(REPLACE ";" "\n  " _orphan_str "${_orphans}")
        message(FATAL_ERROR
            "SQL manifest completeness check failed. These *.sql.in files are not "
            "referenced by manifest.install/manifest.upgrade and are not allow-listed. "
            "Add them to a manifest, delete them, or list them in sql/.manifest_ignore:\n  "
            "${_orphan_str}")
    endif()
endfunction()
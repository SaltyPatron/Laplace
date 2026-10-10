# Text checkout conventions are not part of the Tier-0 geometry contract.
# Preserve the SHA256 composition used by LF checkouts while treating CRLF
# checkouts identically. Missing inputs must not silently reduce the identity.
function(laplace_generator_fingerprint output)
    set(mix "")
    foreach(source IN LISTS ARGN)
        file(READ "${source}" content)
        string(REPLACE "\r\n" "\n" content "${content}")
        string(SHA256 digest "${content}")
        string(APPEND mix "${digest}")
    endforeach()
    string(SHA256 fingerprint "${mix}")
    string(SUBSTRING "${fingerprint}" 0 16 fingerprint)
    set(${output} "${fingerprint}" PARENT_SCOPE)
endfunction()

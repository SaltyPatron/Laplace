#Requires -Version 7
# Exercise the shared manifest owner through actual incremental CMake/Ninja runs.
$ErrorActionPreference='Stop'
$work=Join-Path ([IO.Path]::GetTempPath()) ('extension-sql-version-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $work 'source'
$build=Join-Path $work 'build'
$null=New-Item -ItemType Directory -Path $source -Force
$manifest=[IO.Path]::GetFullPath("$PSScriptRoot/../extension/laplace_substrate/cmake/SqlManifest.cmake").Replace('\','/')
function Invoke-TestCMake([string[]]$Arguments){
    $output=@(& cmake @Arguments 2>&1)
    if($LASTEXITCODE){throw ($output -join "`n")}
    $output -join "`n"
}
try {
    Set-Content -LiteralPath "$source/CMakeLists.txt" -Value @'
cmake_minimum_required(VERSION 3.20)
project(sql_version NONE)
add_subdirectory(laplace_substrate)
add_subdirectory(laplace_geom)
'@
    foreach($name in 'laplace_substrate','laplace_geom'){
        $owner=Join-Path $source $name
        $null=New-Item -ItemType Directory -Path "$owner/sql/functions" -Force
        $files=@{
            "$name.control.in"="default_version = '@EXT_VERSION@'"
            'sql/manifest.install'='functions/install.sql.in'
            'sql/manifest.upgrade'='functions/upgrade.sql.in'
            'sql/functions/install.sql.in'="SELECT 'install';"
            'sql/functions/upgrade.sql.in'="SELECT 'upgrade';"
            "sql/$name.sql.in"='install shim'
            'sql/sqldefines.h.in'='#define SQL_CONSTANT 1'
        }
        foreach($file in $files.Keys){Set-Content -LiteralPath (Join-Path $owner $file) -Value $files[$file]}
        $cmake=@'
include("@MANIFEST@")
laplace_manifest_files(install "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.install" "${CMAKE_CURRENT_SOURCE_DIR}/sql")
laplace_manifest_files(upgrade "${CMAKE_CURRENT_SOURCE_DIR}/sql/manifest.upgrade" "${CMAKE_CURRENT_SOURCE_DIR}/sql")
file(GLOB templates "${CMAKE_CURRENT_SOURCE_DIR}/sql/*.in" "${CMAKE_CURRENT_SOURCE_DIR}/*.control.in")
set(inputs ${install} ${upgrade} ${templates})
list(SORT inputs)
set(hashes "")
foreach(input IN LISTS inputs)
    file(SHA256 "${input}" hash)
    string(APPEND hashes "${hash}")
endforeach()
string(SHA256 version "${hashes}")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/version.txt" "${version}")
'@
        Set-Content -LiteralPath "$owner/CMakeLists.txt" -Value $cmake.Replace('@MANIFEST@',$manifest)
    }
    $null=Invoke-TestCMake @('-S',$source,'-B',$build,'-G','Ninja')
    foreach($name in 'laplace_substrate','laplace_geom'){
        foreach($leaf in 'sql/functions/install.sql.in','sql/functions/upgrade.sql.in','sql/sqldefines.h.in',"sql/$name.sql.in","$name.control.in"){
            $version=Join-Path $build "$name/version.txt"
            $before=Get-Content -LiteralPath $version -Raw
            $other=if($name -eq 'laplace_substrate'){'laplace_geom'}else{'laplace_substrate'}
            $otherBefore=Get-Content -LiteralPath "$build/$other/version.txt" -Raw
            Start-Sleep -Milliseconds 50
            Add-Content -LiteralPath "$source/$name/$leaf" -Value '-- changed content'
            $output=Invoke-TestCMake @('--build',$build)
            if($output -notmatch 'Re-running CMake' -or (Get-Content -LiteralPath $version -Raw) -eq $before){
                throw "Incremental build retained stale version for $name/$leaf : $output"
            }
            if((Get-Content -LiteralPath "$build/$other/version.txt" -Raw) -ne $otherBefore){throw 'An unrelated extension version changed.'}
            Write-Host "PASS incremental SQL version: $name/$leaf"
        }
    }
} finally {
    $resolved=[IO.Path]::GetFullPath($work)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected test directory outside temporary storage.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

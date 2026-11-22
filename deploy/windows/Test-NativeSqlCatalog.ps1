#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Library,
    [string]$Catalog
)
$ErrorActionPreference = 'Stop'
if (-not ('LaplacePackageNativeAbi' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class LaplacePackageNativeAbi {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr Query([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    public static IntPtr Load(string path) {
        var handle = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    public static bool HasExport(IntPtr handle, string entry) {
        return NativeLibrary.TryGetExport(handle, entry, out _);
    }
    public static string Read(IntPtr handle, string entry, string name) {
        var query = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(handle, entry));
        return Marshal.PtrToStringUTF8(query(name));
    }
}
'@
}

function Resolve-CatalogPath([string]$ExplicitCatalog) {
    if ($ExplicitCatalog) {
        return (Resolve-Path -LiteralPath $ExplicitCatalog).Path
    }
    $candidates = @()
    if ($env:LAPLACE_SRC) {
        $candidates += Join-Path $env:LAPLACE_SRC 'Laplace-Engine/src/db/sql_catalog.def'
    }
    $candidates += @(
        "$PSScriptRoot/../../../Laplace-Engine/src/db/sql_catalog.def",
        "$PSScriptRoot/../../.ci-dependencies/laplace-engine/src/db/sql_catalog.def"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Read-ExpandedCatalog([string]$CatalogPath) {
    $catalogDir = Split-Path -Parent $CatalogPath
    $source = [IO.File]::ReadAllText($CatalogPath)
    return [regex]::Replace($source, '(?m)^\s*#include\s+"(?<inc>[^"]+)"\s*$', {
        param($m)
        [IO.File]::ReadAllText((Join-Path $catalogDir $m.Groups['inc'].Value))
    })
}

$libraryPath = (Resolve-Path -LiteralPath $Library).Path
$libraryDir = Split-Path -Parent $libraryPath
$catalogPath = Resolve-CatalogPath $Catalog
$handle = [LaplacePackageNativeAbi]::Load($libraryPath)
$nativeHandle = [IntPtr]::Zero
try {
    if ([LaplacePackageNativeAbi]::HasExport($handle, 'laplace_sql_query_text') -and
        [LaplacePackageNativeAbi]::HasExport($handle, 'laplace_sql_query_parameters')) {
        if (-not $catalogPath) {
            throw 'Cannot locate native SQL catalog (sql_catalog.def); package verification is incomplete.'
        }
        $source = Read-ExpandedCatalog $catalogPath
        $literal = '"(?:\\.|[^"\\])*"'
        $pattern = 'SQL_QUERY\(\s*"(?<name>[^"]+)"\s*,\s*"(?<parameters>[^"]*)"\s*,\s*(?<text>(?:' + $literal + '\s*)+)\)'
        $queries = [regex]::Matches($source, $pattern)
        if ($queries.Count -eq 0 -or $queries.Count -ne [regex]::Matches($source, '\bSQL_QUERY\(').Count) {
            throw 'Cannot parse every native SQL contract; package verification is incomplete.'
        }
        foreach ($query in $queries) {
            $name = $query.Groups['name'].Value
            $expected = -join @([regex]::Matches($query.Groups['text'].Value, $literal) | ForEach-Object {
                [regex]::Unescape($_.Value.Substring(1, $_.Value.Length - 2))
            })
            $actual = [LaplacePackageNativeAbi]::Read($handle, 'laplace_sql_query_text', $name)
            $parameters = [LaplacePackageNativeAbi]::Read($handle, 'laplace_sql_query_parameters', $name)
            if ($actual -cne $expected -or $parameters -cne $query.Groups['parameters'].Value) {
                throw "Native SQL contract differs from source: $name in $libraryPath. Rebuild the native engine before packaging."
            }
        }
        Write-Host "Verified $($queries.Count) native SQL contracts: $libraryPath"
        return
    }

    foreach ($export in 'lp_engine_init', 'lp_engine_turn', 'lp_engine_turn_response_free',
                        'lp_engine_chess_play', 'lp_engine_mold_checkpoint', 'lp_engine_free',
                        'lp_text_id', 'lp_id_compose') {
        if (-not [LaplacePackageNativeAbi]::HasExport($handle, $export)) {
            throw "Native engine ABI export is missing: $export in $libraryPath. Rebuild Laplace-Engine before packaging."
        }
    }

    $nativeDll = Join-Path $libraryDir 'laplace.dll'
    if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
        throw "Native identity library is missing alongside engine ABI: $nativeDll"
    }
    $nativeHandle = [LaplacePackageNativeAbi]::Load($nativeDll)
    foreach ($export in 'lp_id_codepoint', 'lp_id_compose', 'lp_coord_centroid', 'lp_tier0_codepoint') {
        if (-not [LaplacePackageNativeAbi]::HasExport($nativeHandle, $export)) {
            throw "Native library export is missing: $export in $nativeDll. Rebuild Laplace-Native before packaging."
        }
    }

    $contractCount = 0
    if ($catalogPath) {
        $source = Read-ExpandedCatalog $catalogPath
        $literal = '"(?:\\.|[^"\\])*"'
        $pattern = 'LP_SQL\(\s*(?<name>[A-Z0-9_]+)\s*,\s*(?<arity>\d+)\s*,\s*(?<text>(?:' + $literal + '\s*)+)\)'
        $queries = [regex]::Matches($source, $pattern)
        $rawCalls = ([regex]::Matches($source, '\bLP_SQL\(').Count - [regex]::Matches($source, 'define LP_SQL\(').Count)
        if ($queries.Count -eq 0 -or $queries.Count -ne $rawCalls) {
            throw "Cannot parse every LP_SQL contract in $catalogPath; package verification is incomplete."
        }
        $contractCount = $queries.Count
    }
    Write-Host "Verified modular native ABI closure ($contractCount SQL catalog entries checked): $libraryPath"
} finally {
    if ($nativeHandle -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.NativeLibrary]::Free($nativeHandle)
    }
    [Runtime.InteropServices.NativeLibrary]::Free($handle)
}

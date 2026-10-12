#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Library,
    [string]$Catalog = "$PSScriptRoot/../../engine/core/src/sql_catalog.def"
)
$ErrorActionPreference = 'Stop'
if (-not ('LaplacePackageSqlCatalog' -as [type])) {
    Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class LaplacePackageSqlCatalog {
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr Query([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    public static IntPtr Load(string path) {
        var handle = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x1000);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    public static string Read(IntPtr handle, string entry, string name) {
        var query = Marshal.GetDelegateForFunctionPointer<Query>(NativeLibrary.GetExport(handle, entry));
        return Marshal.PtrToStringUTF8(query(name));
    }
}
'@
}
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Catalog))
$literal = '"(?:\\.|[^"\\])*"'
$pattern = 'SQL_QUERY\(\s*"(?<name>[^"]+)"\s*,\s*"(?<parameters>[^"]*)"\s*,\s*(?<text>(?:'+$literal+'\s*)+)\)'
$queries = [regex]::Matches($source, $pattern)
if ($queries.Count -eq 0 -or $queries.Count -ne [regex]::Matches($source, '\bSQL_QUERY\(').Count) {
    throw 'Cannot parse every native SQL contract; package verification is incomplete.'
}
$libraryPath = (Resolve-Path -LiteralPath $Library).Path
$handle = [LaplacePackageSqlCatalog]::Load($libraryPath)
try {
    foreach ($query in $queries) {
        $name = $query.Groups['name'].Value
        $expected = -join @([regex]::Matches($query.Groups['text'].Value, $literal) | ForEach-Object {
            [regex]::Unescape($_.Value.Substring(1, $_.Value.Length - 2))
        })
        $actual = [LaplacePackageSqlCatalog]::Read($handle, 'laplace_sql_query_text', $name)
        $parameters = [LaplacePackageSqlCatalog]::Read($handle, 'laplace_sql_query_parameters', $name)
        if ($actual -cne $expected -or $parameters -cne $query.Groups['parameters'].Value) {
            throw "Native SQL contract differs from source: $name in $libraryPath. Rebuild the native engine before packaging."
        }
    }
    Write-Host "Verified $($queries.Count) native SQL contracts: $libraryPath"
} finally {
    [Runtime.InteropServices.NativeLibrary]::Free($handle)
}

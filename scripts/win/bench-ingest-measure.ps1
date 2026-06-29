param(
    [string]$Source,
    [string]$Path
)

function Format-Bytes([long]$n) {
    if ($n -ge 1GB) { return "{0:N3} GB" -f ($n / 1GB) }
    if ($n -ge 1MB) { return "{0:N1} MB" -f ($n / 1MB) }
    return "{0:N0} bytes" -f $n
}

$files = @()
switch ($Source.ToLowerInvariant()) {
    'conceptnet' {
        $f = Join-Path $Path 'assertions.csv'
        if (Test-Path $f) { $files += Get-Item $f }
    }
    'wiktionary' {
        $f = Join-Path $Path 'raw-wiktextract-data.jsonl'
        if (-not (Test-Path $f)) { $f = Join-Path $Path 'kaikki.org-dictionary-English.jsonl' }
        if (Test-Path $f) { $files += Get-Item $f }
    }
    'wiktionary-en' {
        $f = Join-Path $Path 'kaikki.org-dictionary-English.jsonl'
        if (Test-Path $f) { $files += Get-Item $f }
    }
    'ud' {
        if (Test-Path $Path) {
            $files += Get-ChildItem $Path -Recurse -File -Filter '*.conllu' -ErrorAction SilentlyContinue
        }
    }
    default {
        if (Test-Path $Path) {
            if ((Get-Item $Path).PSIsContainer) {
                $files += Get-ChildItem $Path -Recurse -File -ErrorAction SilentlyContinue
            } else {
                $files += Get-Item $Path
            }
        }
    }
}

$totalBytes = ($files | Measure-Object Length -Sum).Sum
$fileCount = $files.Count
Write-Output "source=$Source"
Write-Output "path=$Path"
Write-Output "file_count=$fileCount"
Write-Output "total_bytes=$totalBytes"
Write-Output "total_size=$(Format-Bytes $totalBytes)"
foreach ($f in ($files | Sort-Object Length -Descending | Select-Object -First 5)) {
    Write-Output "file=$(Format-Bytes $f.Length) $($f.FullName)"
}

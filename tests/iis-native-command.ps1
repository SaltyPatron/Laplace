#Requires -Version 7
param([Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../deploy/windows/iis-runtime.ps1"
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence){throw 'Choose a new evidence directory; existing evidence is preserved.'}
$null=New-Item -ItemType Directory -Path $evidence
$fixture=Join-Path $evidence 'native-command-fixture.ps1'
[IO.File]::WriteAllText($fixture,@'
param([int]$Result,[string]$Marker)
[Console]::Out.WriteLine('native-stdout-'+$Marker)
[Console]::Error.WriteLine('native-stderr-'+$Marker)
exit $Result
'@)
$shell=(Get-Command pwsh -CommandType Application).Source
$checks=@()
foreach($preference in @($false,$true)){
    foreach($case in @(@{Code=0;Max=0;Fails=$false},@{Code=1;Max=0;Fails=$true},@{Code=7;Max=7;Fails=$false},@{Code=8;Max=7;Fails=$true},@{Code=23;Max=0;Fails=$true})){
        $PSNativeCommandUseErrorActionPreference=$preference
        $marker='fixture-'+$preference+'-'+$case.Code
        $failure=$null
        try{$output=Invoke-IisNativeCommand $shell @('-NoProfile','-File',$fixture,'-Result',[string]$case.Code,'-Marker',$marker) -MaxSuccessExitCode $case.Max}
        catch{$failure=$_}
        if($case.Fails){
            if(-not $failure){throw "Exit $($case.Code) was silently accepted."}
            $message=$failure.Exception.Message
            foreach($expected in @("exit $($case.Code)",'native-stdout-'+$marker,'native-stderr-'+$marker,$fixture)){
                if(-not $message.Contains($expected)){throw 'Native command failure lost its exit code, argument context or output.'}
            }
        }else{
            if($failure){throw $failure}
            $message=$output -join "`n"
            if(-not $message.Contains('native-stdout-'+$marker) -or -not $message.Contains('native-stderr-'+$marker)){throw 'Successful native output was lost.'}
        }
        if($PSNativeCommandUseErrorActionPreference -ne $preference){throw 'Native policy leaked outside the command owner.'}
        $checks+=@{caller_native_error_policy=$preference;exit_code=$case.Code;maximum_success=$case.Max;passed=$true}
    }
}
foreach($script in @('deploy/windows/publish.ps1','deploy/windows/iis-runtime.ps1')){
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path "$PSScriptRoot/.." $script),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ($errors.Message -join "`n")}
}
@{schema='laplace.iis-native-command-acceptance/v1';checked_utc=[DateTimeOffset]::UtcNow.ToString('o');checks=$checks;serving_iis_not_modified=$true} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json')
Write-Host 'PASS actual native output/exit handling under both PowerShell policies; no IIS service or publication was run.'

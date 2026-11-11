$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $root ('.artifacts/qualification-failure-' + [Guid]::NewGuid().ToString('N') + '.txt')
$global:habyQualificationProbeCalls = [Collections.Generic.List[string]]::new()
function wslc {
    if ($args[0] -eq '--version') { $global:LASTEXITCODE = 0; return 'synthetic-runtime' }
    $global:habyQualificationProbeCalls.Add([string]$args[0])
    $global:LASTEXITCODE = 17
    return 'synthetic launch failure; not real Kubernetes evidence'
}
$failed = $false
try { & (Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1') -Runtime wslc -EvidencePath $evidence }
catch { $failed = $true }
if (!$failed) { throw 'Runtime failure was swallowed.' }
if ($global:habyQualificationProbeCalls.Count -ne 1 -or $global:habyQualificationProbeCalls[0] -ne 'run') { throw 'Unexpected action after failed launch.' }
$report = Get-Content -LiteralPath $evidence -Raw
if (!$report.Contains('Run: FAIL') -or !$report.Contains('Run-owned private directory removed.')) { throw 'Failure evidence or private cleanup missing.' }
if ($report.Contains('Harness: PASS') -or $report.Contains('synthetic launch failure')) { throw 'Unsafe or false evidence.' }
Write-Output 'PASS launch failure propagates, evidence retained, private directory removed; synthetic control only.'
exit 0

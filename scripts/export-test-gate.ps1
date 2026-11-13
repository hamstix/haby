param([Parameter(Mandatory)][string[]]$TrxPath, [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Gate, [Parameter(Mandatory)][string]$Suite)
$ErrorActionPreference = 'Stop'
try {
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new gate output directory; earlier evidence must not be overwritten.' }
. "$PSScriptRoot/test-suite-plan.ps1"
& "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath $TrxPath -OutputDirectory $OutputDirectory
$cases = @(Get-Content (Join-Path $OutputDirectory 'cases.json') -Raw | ConvertFrom-Json)
Assert-HabyGatePlan $cases (Join-Path $PSScriptRoot '../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json') $Gate @($Suite)
if (@($cases | Where-Object outcome -CNE 'Passed').Count) { throw 'Required gate has failed/skipped cases.' }
$root = Split-Path $PSScriptRoot -Parent
$sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
$diff = (& git -C $root diff --binary HEAD | Out-String)
$untracked = @(& git -C $root ls-files --others --exclude-standard)
$required = $env:HABY_AOT_CLI_REQUIRED -ceq '1'; $nativeHash = $null
if ($Gate.StartsWith('Native AOT (', [StringComparison]::Ordinal)) {
    if (-not $required -or -not $env:HABY_AOT_CLI_PATH -or -not (Test-Path -LiteralPath $env:HABY_AOT_CLI_PATH -PathType Leaf)) { throw 'Required native artifact is missing.' }
    $nativeHash = (Get-FileHash -LiteralPath $env:HABY_AOT_CLI_PATH).Hash.ToLowerInvariant()
}
[pscustomobject]@{ source = $sha; trackedDiffSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($diff))).ToLowerInvariant()
    dirty = -not [string]::IsNullOrWhiteSpace($diff) -or $untracked.Count -ne 0; untrackedFileCount = $untracked.Count
    sdk = (& dotnet --version | Out-String).Trim(); requiredAot = $required; nativeSha256 = $nativeHash
    gate = $Gate; suite = $Suite; cases = $cases.Count; status = 'complete' } |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'gate.json') -Encoding utf8
} catch { throw 'Gate inventory export/validation failed; protected parser/test content withheld.' }

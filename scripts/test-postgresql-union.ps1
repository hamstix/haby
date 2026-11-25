$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-suite-plan.ps1"
function Refuses([scriptblock]$Action) { $refused=$false; try { & $Action | Out-Null } catch { $refused=$true }; if (-not $refused) { throw 'Invalid union accepted.' } }
$plan = Get-Content "$PSScriptRoot/../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json" -Raw | ConvertFrom-Json
$rows = @($plan.entries | Where-Object gate -CEQ 'postgresql-integration')
$smoke = @($rows | Where-Object { $_.case -cin @('47a4427250a45570b86cc662504bee1709141be4c30be469aaafb3333f96a90f','4d3138b78192e0daf927d9bfac70b8669e7960c53f66fea13e7544293b6b9b4d') })
$path = Join-Path ([IO.Path]::GetTempPath()) ('haby-union-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    @{version=1;scope='targeted';cases=$smoke} | ConvertTo-Json -Depth 7 | Set-Content $path
    $result = Get-HabyFocusedPostgreSqlSelection $rows '' '' $path
    if ($result.cases.Count -ne 2) { throw 'Smoke union changed.' }
    Refuses { Get-HabyFocusedPostgreSqlSelection $rows 'Other' '' $path }
    @{version=1;scope='targeted';cases=@($smoke[0])} | ConvertTo-Json -Depth 7 | Set-Content $path
    Refuses { Get-HabyFocusedPostgreSqlSelection $rows '' '' $path }
    @{version=1;scope='targeted';cases=@($smoke + $smoke[0])} | ConvertTo-Json -Depth 7 | Set-Content $path
    Refuses { Get-HabyFocusedPostgreSqlSelection $rows '' '' $path }
    $theory = @($rows | Group-Object method | Where-Object Count -GT 1)[0].Group
    @{version=1;scope='targeted';cases=@($smoke + $theory[0])} | ConvertTo-Json -Depth 7 | Set-Content $path
    Refuses { Get-HabyFocusedPostgreSqlSelection $rows '' '' $path }
    @{version=1;scope='targeted';source=('0'*40);tree=('1'*40);cases=$smoke} | ConvertTo-Json -Depth 7 | Set-Content $path
    Refuses { & "$PSScriptRoot/test-postgresql-measured.ps1" -Docker -SelectionPath $path -ResultsDirectory (Join-Path ([IO.Path]::GetTempPath()) 'haby-stale-union-never-created') }
} finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
'Current union controls passed: two smoke facts, duplicate/missing refusal, full theory multiplicity, stale pre-runtime source refusal.'

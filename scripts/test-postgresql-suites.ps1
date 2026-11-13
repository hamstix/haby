$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-suite-plan.ps1"
. "$PSScriptRoot/postgresql-coverage.ps1"
. "$PSScriptRoot/invoke-postgresql-suites.ps1"
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
Require (@(Get-HabyPostgreSqlSuites 'full').Count -eq 2) 'Full omitted a suite.'
Require ((@(Get-HabyPostgreSqlSuites 'full') -join ',') -ceq 'functional,infrastructure-qualification') 'Full suite membership/order changed.'
Require ((@(Get-HabyPostgreSqlSuites 'functional') -join ',') -ceq 'functional') 'Functional selection changed.'
Require ((@(Get-HabyPostgreSqlSuites 'infrastructure-qualification') -join ',') -ceq 'infrastructure-qualification') 'Qualification selection changed.'
foreach ($bad in @('', ' ', 'FULL', 'PROTECTED-invalid')) {
    $refused = $false; try { Get-HabyPostgreSqlSuites $bad | Out-Null } catch { $refused = $true }
    Require $refused 'Invalid/empty selection was accepted.'
}
$rows = @([pscustomobject]@{method='Haby.Tests.A.One';case='a';suite='functional'},
    [pscustomobject]@{method='Haby.Tests.B.Two';case='b';suite='infrastructure-qualification'})
Require ((Get-HabySuiteFilter $rows 'functional') -ceq 'FullyQualifiedName!~Haby.Tests.B.') 'Wrong suite filter.'
$refused = $false; try { Get-HabySuiteFilter @() 'functional' | Out-Null } catch { $refused = $true }
Require $refused 'Empty suite filter was accepted.'
foreach ($wrong in @(@($rows[0]), @($rows[0],$rows[0]))) {
    $refused = $false; try { Assert-HabyInventory $rows $wrong } catch { $refused = $true }
    Require $refused 'Missing/duplicate aggregate was accepted.'
}
$refused = $false
try { Assert-HabyInventory @($rows[0],$rows[0],$rows[1]) @($rows[0],$rows[1],$rows[1]) } catch { $refused = $true }
Require $refused 'Equal-total/equal-distinct-key but wrong multiplicity was accepted.'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-suite-control-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $template = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="same"><TestMethod className="Haby.Tests.Fixture" name="METHOD" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="same" testName="METHOD(value: PROTECTED-input)" outcome="Passed" duration="00:00:00.1" startTime="2026-11-13T12:00:00Z" endTime="2026-11-13T12:00:00.1Z" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>
'@
    $one = Join-Path $directory 'one.trx'; $two = Join-Path $directory 'two.trx'
    $template.Replace('METHOD','One') | Set-Content -LiteralPath $one
    $template.Replace('METHOD','Two') | Set-Content -LiteralPath $two
    $safe = Join-Path $directory 'safe'
    & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath @($one,$two) -OutputDirectory $safe
    $cases = @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)
    Require ($cases.Count -eq 2) 'Aggregate lost a group.'
    foreach ($file in Get-ChildItem $safe -File) { Require (-not ([IO.File]::ReadAllText($file.FullName)).Contains('PROTECTED-input')) 'Aggregate leaked parameters.' }
    $refused = $false
    try { & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath @($one,$one) -OutputDirectory (Join-Path $directory 'duplicate') } catch { $refused = $true }
    Require $refused 'Cross-group duplicate was accepted.'
    Require (-not (Test-Path (Join-Path $directory 'duplicate'))) 'Invalid aggregate published output.'
    # Distinct actual theory rows may have identical truncated display names.
    # Preserve their Counter multiplicity and opaque row identity, not deduplication.
    $template.Replace('METHOD','One').Replace('id="same"','id="different"').Replace('testId="same"','testId="different"') | Set-Content -LiteralPath $two
    $repeated = Join-Path $directory 'repeated-display'
    & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath @($one,$two) -OutputDirectory $repeated
    $twins = @(Get-Content (Join-Path $repeated 'cases.json') -Raw | ConvertFrom-Json)
    Require ($twins.Count -eq 2 -and $twins[0].case -ceq $twins[1].case -and $twins[0].row -cne $twins[1].row) 'Identical theory displays lost actual row identity/multiplicity.'
    $functional = 'Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Control'
    $qualification = 'Haby.PostgreSql.IntegrationTests.OwnedInfrastructureTests.Control'
    $controlCases = @($functional,$qualification | ForEach-Object {
        [pscustomobject]@{ method = $_; case = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_ + [char]10 + $_))).ToLowerInvariant()
            suite = $(if ($_ -ceq $functional) { 'functional' } else { 'infrastructure-qualification' }); gate = 'postgresql-integration' }
    })
    $plan = Join-Path $directory 'plan.json'
    [pscustomobject]@{ entries = $controlCases } | ConvertTo-Json | Set-Content -LiteralPath $plan
    $script:functionalControl = $functional; $script:qualificationControl = $qualification
    function global:dotnet {
        $isQualification = ($args -join ' ').Contains('FullyQualifiedName~Haby.PostgreSql.IntegrationTests.OwnedInfrastructureTests.')
        $method = if ($isQualification) { $script:qualificationControl } else { $script:functionalControl }
        if ($args -contains '--list-tests') { $method; $global:LASTEXITCODE = 0; return }
        if ($isQualification) { $script:qualificationCalls++ }
        $global:LASTEXITCODE = $(if ($isQualification) { 0 } else { 17 })
        $index = [Array]::IndexOf($args, '--results-directory')
        if (-not $script:missingRaw) {
            $class = $method.Substring(0,$method.LastIndexOf('.')); $name = $method.Substring($method.LastIndexOf('.')+1)
            $data = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="one"><TestMethod className="'+$class+'" name="'+$name+'" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="one" testName="'+$method+'" outcome="Passed" duration="00:00:00.1" startTime="2026-11-13T12:00:00Z" endTime="2026-11-13T12:00:00.1Z" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
            if (-not $isQualification) { $data = $data.Replace('outcome="Passed"','outcome="Failed"').Replace('passed="1" failed="0"','passed="0" failed="1"') }
            $data | Set-Content -LiteralPath (Join-Path $args[$index+1] 'full.trx')
        }
        $counts = @{limit=4;hostGroupLimit=2}
        foreach ($field in @('active','startupAttempts','ready','cleaned','startupFailed','cleanupFailed','catalogDatabasesCreated','catalogDatabasesDropped','capacityRefused','readyActive','hostGroupsActive','hostCapacityRefused','peakReservedServers','peakSuiteReservedServers','peakControlReservedServers','peakReadyServers','peakReadySuiteServers','peakReadyControlServers','peakHostGroups')) { $counts[$field]=0 }
        $counts | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:HABY_TEST_TIMING_DIRECTORY 'resources.json')
    }
    foreach ($missing in @($false,$true)) {
        $script:missingRaw = $missing; $script:qualificationCalls = 0
        $raw = Join-Path $directory ('runner-raw-'+$missing); $output = Join-Path $directory ('runner-safe-'+$missing)
        New-Item -ItemType Directory -Path $raw,$output | Out-Null
        $result = Invoke-HabyPostgreSqlSuites -Project unused -Artifacts unused -Private $raw -Safe $output -Coverage $controlCases -Suites @('functional','infrastructure-qualification') -MapPath "$PSScriptRoot/../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json" -PlanPath $plan -ServerLimit 4 -HostLimit 2
        Require ($result.exitCode -eq 17) 'Suite/report failure masked the primary exit.'
        Require ($result.reportingFailed -eq $missing) 'Secondary report outcome changed.'
        Require ($script:qualificationCalls -eq $(if ($missing) { 0 } else { 1 })) 'Full omitted/repeated qualification or continued after unconfirmed cleanup.'
        if (-not $missing) {
            $evidence = Get-Content (Join-Path $output 'functional/failure-evidence.json') -Raw | ConvertFrom-Json
            $executed = @(Get-Content (Join-Path $output 'functional/cases.json') -Raw | ConvertFrom-Json)
            Require ($evidence.failedCases -eq 1 -and $evidence.records.Count -eq 1 -and $evidence.records[0].row -ceq $executed[0].row) 'Suite primary failure lost exact row evidence.'
            $qualified = Get-Content (Join-Path $output 'infrastructure-qualification/failure-evidence.json') -Raw | ConvertFrom-Json
            Require ($qualified.failedCases -eq 0 -and $qualified.records.Count -eq 0) 'Passing qualification fabricated failure rows.'
        }
    }
} finally {
    $owned = [IO.Path]::GetFullPath($directory)
    if ($owned.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $owned -Recurse -Force }
}
'Suite controls: full/partial selection, empty refusal, aggregate multiplicity and cross-group redaction passed.'
exit 0

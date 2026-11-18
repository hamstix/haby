$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-suite-plan.ps1"
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Refuses([scriptblock]$Action) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    Require $refused 'Invalid or empty focused selection was accepted.'
}
$prefix = 'Haby.PostgreSql.IntegrationTests.'
$rows = @(
    [pscustomobject]@{ method = $prefix + 'CatalogTests.One'; case = 'a'; suite = 'functional' },
    [pscustomobject]@{ method = $prefix + 'CatalogTests.One'; case = 'b'; suite = 'functional' },
    [pscustomobject]@{ method = $prefix + 'CatalogTests.Two'; case = 'c'; suite = 'functional' },
    [pscustomobject]@{ method = $prefix + 'CatalogTestsExtra.One'; case = 'd'; suite = 'functional' },
    [pscustomobject]@{ method = $prefix + 'OwnedInfrastructureTests.Control'; case = 'e'; suite = 'infrastructure-qualification' }
)
$method = Get-HabyFocusedPostgreSqlSelection $rows ($prefix + 'CatalogTests.One') ''
Require ($method.cases.Count -eq 2) 'Testcase did not preserve all theory rows.'
Require ($method.filter -ceq ('FullyQualifiedName=' + $prefix + 'CatalogTests.One')) 'Testcase predicate is not exact.'
Require (($method.suites -join ',') -ceq 'functional') 'Focused testcase included unrelated suite.'
$group = Get-HabyFocusedPostgreSqlSelection $rows '' ($prefix + 'CatalogTests')
Require ($group.cases.Count -eq 3) 'Group included a different class or omitted a member.'
Require ($group.filter -ceq ('FullyQualifiedName=' + $prefix + 'CatalogTests.One|FullyQualifiedName=' + $prefix + 'CatalogTests.Two')) 'Group predicate is not an exact method union.'
Require ($null -eq (Get-HabyFocusedPostgreSqlSelection $rows '' '')) 'Ordinary full execution became focused.'
Refuses { Get-HabyFocusedPostgreSqlSelection $rows ($prefix + 'CatalogTests.One') ($prefix + 'CatalogTests') }
Refuses { Get-HabyFocusedPostgreSqlSelection $rows ($prefix + 'Missing.One') '' }
Refuses { Get-HabyFocusedPostgreSqlSelection @() ($prefix + 'CatalogTests.One') '' }
foreach ($bad in @(' ', 'CatalogTests', ($prefix + 'CatalogTests.One|FullyQualifiedName~Other'), ($prefix + 'CatalogTests.One(value: secret)'), ($prefix.ToLowerInvariant() + 'CatalogTests.One'))) {
    Refuses { Get-HabyFocusedPostgreSqlSelection $rows $bad '' }
}
$selected = @($method.cases)
Assert-HabyInventory $selected @($rows[0], $rows[1])
Refuses { Assert-HabyInventory $selected @($rows[0]) }
Refuses { Assert-HabyInventory $selected @($rows[0], $rows[0]) }

# Unknown method names must be rejected against the current ledger before WSLc setup.
$global:HabyFocusedEarlyRuntimeCalls = 0
function global:wslc { $global:HabyFocusedEarlyRuntimeCalls++; $global:LASTEXITCODE = 1; 'controlled-unavailable' }
try {
    Refuses { & "$PSScriptRoot/test-postgresql-wslc.ps1" -TestCase ($prefix + 'Missing.One') }
    Require ($global:HabyFocusedEarlyRuntimeCalls -eq 0) 'Unknown focused selector reached runtime setup.'
} finally { Remove-Item Function:/wslc -ErrorAction SilentlyContinue }

# Conflicting Kubernetes modes must fail before any runtime/build/private setup.
$script:runtimeCalls = 0
$runner = { param($File, $Arguments, $Seconds) $script:runtimeCalls++; throw 'Unexpected command.' }
$path = Join-Path ([IO.Path]::GetTempPath()) ('haby-invalid-selection-' + [Guid]::NewGuid().ToString('N') + '.txt')
Refuses { & "$PSScriptRoot/run-kubernetes-qualification.ps1" -Runtime wslc -DeploymentKernel -DurableActions -CommandRunner $runner -EvidencePath $path }
Require ($script:runtimeCalls -eq 0 -and -not (Test-Path -LiteralPath $path)) 'Invalid Kubernetes selection performed setup.'

. "$PSScriptRoot/invoke-postgresql-suites.ps1"
. "$PSScriptRoot/postgresql-coverage.ps1"
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-focused-control-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $script:chosen = $prefix + 'AdministrativeSecretTests.Control'
    $script:other = $prefix + 'OwnedInfrastructureTests.Control'
    $coverage = @($script:chosen, $script:other | ForEach-Object {
        [pscustomobject]@{ method = $_; case = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_ + [char]10 + $_))).ToLowerInvariant()
            suite = $(if ($_ -ceq $script:chosen) { 'functional' } else { 'infrastructure-qualification' }); gate = 'postgresql-integration' }
    })
    $plan = Join-Path $directory 'plan.json'
    [pscustomobject]@{ entries = $coverage } | ConvertTo-Json | Set-Content -LiteralPath $plan
    function global:dotnet {
        $script:commands.Add(@($args))
        $filterIndex = [Array]::IndexOf($args, '--filter')
        if ($filterIndex -lt 0 -or $args[$filterIndex+1] -cne ('FullyQualifiedName=' + $script:chosen)) { throw 'Focused command widened the selection.' }
        $global:LASTEXITCODE = 0
        if ($args -contains '--list-tests') {
            if ($script:scenario -eq 'zero') { return }
            if ($script:scenario -eq 'wrong') { $script:other } else { $script:chosen }
            return
        }
        if ($script:scenario -eq 'canceled') { $global:LASTEXITCODE = 130; return }
        if ($script:scenario -eq 'missing') { return }
        $index = [Array]::IndexOf($args, '--results-directory')
        $class = $script:chosen.Substring(0, $script:chosen.LastIndexOf('.'))
        $outcome = if ($script:scenario -eq 'failed') { 'Failed' } elseif ($script:scenario -eq 'skipped') { 'NotExecuted' } else { 'Passed' }
        if ($script:scenario -eq 'failed') { $global:LASTEXITCODE = 17 }
        $passed = [int]($outcome -eq 'Passed'); $failed = [int]($outcome -eq 'Failed'); $skipped = [int]($outcome -eq 'NotExecuted')
        $xml = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="one"><TestMethod className="'+$class+'" name="Control" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="one" testName="'+$script:chosen+'" outcome="'+$outcome+'" duration="00:00:00.1" startTime="2026-11-18T12:00:00Z" endTime="2026-11-18T12:00:00.1Z" /></Results><ResultSummary><Counters total="1" passed="'+$passed+'" failed="'+$failed+'" notExecuted="'+$skipped+'" /></ResultSummary></TestRun>'
        $xml | Set-Content -LiteralPath (Join-Path $args[$index+1] 'full.trx')
    }
    foreach ($script:scenario in @('passed', 'failed', 'canceled', 'missing', 'zero', 'wrong', 'skipped')) {
        $script:commands = [Collections.Generic.List[object]]::new()
        $raw = Join-Path $directory ($script:scenario + '-private'); $safe = Join-Path $directory ($script:scenario + '-safe')
        New-Item -ItemType Directory -Path $raw, $safe | Out-Null
        $result = Invoke-HabyPostgreSqlSuites -Project unused -Artifacts unused -Private $raw -Safe $safe -Coverage $coverage -Suites @('functional','infrastructure-qualification') -MapPath "$PSScriptRoot/../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json" -PlanPath $plan -TestCase $script:chosen -ExternalFocused
        Require ($result.groups.Count -eq 1 -and $result.groups[0].suite -ceq 'functional') 'Focused run launched an unrelated suite.'
        Require ($script:commands.Count -eq $(if ($script:scenario -in @('zero', 'wrong')) { 1 } else { 2 })) 'Focused discovery/execution call count changed.'
        if ($script:scenario -eq 'passed') { Require ($result.exitCode -eq 0 -and $result.groups[0].outcome -ceq 'passed') 'Focused pass was lost.' }
        else { Require ($result.exitCode -ne 0) 'Failed/incomplete focused run passed.' }
        if ($script:scenario -eq 'failed') { Require ($result.exitCode -eq 17 -and $result.groups[0].outcome -ceq 'failed') 'Focused primary test failure was replaced.' }
        if ($script:scenario -eq 'canceled') { Require ($result.exitCode -eq 130 -and $result.groups[0].outcome -ceq 'canceled') 'Focused cancellation was not distinct.' }
        if ($script:scenario -eq 'zero') { Require ($result.groups[0].outcome -ceq 'zero-discovery') 'Zero discovery was not distinct.' }
    }
} finally {
    Remove-Item Function:/dotnet -ErrorAction SilentlyContinue
    $owned = [IO.Path]::GetFullPath($directory)
    if ($owned.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $owned -Recurse -Force }
}
'Focused selection controls passed: exact method/group, theory multiplicity, invalid/zero discovery, no unrelated suite, Kubernetes pre-setup rejection.'

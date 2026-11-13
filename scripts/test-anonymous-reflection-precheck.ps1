$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-suite-plan.ps1"
. "$PSScriptRoot/postgresql-coverage.ps1"
. "$PSScriptRoot/invoke-postgresql-suites.ps1"
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-reflection-gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$method = 'Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Disabled_tls_serves_grpc_on_loopback_without_certificate_settings'
$qualification = 'Haby.PostgreSql.IntegrationTests.OwnedInfrastructureTests.Control'
$cases = @($method,$qualification | ForEach-Object {
    [pscustomobject]@{ method = $_; case = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_ + [char]10 + $_))).ToLowerInvariant()
        suite = $(if ($_ -ceq $method) { 'functional' } else { 'infrastructure-qualification' }); gate = 'postgresql-integration' }
})
$sentinel = 'PROTECTED_PRECHECK_188'
try {
    $plan = Join-Path $directory 'plan.json'
    [pscustomobject]@{ entries = $cases } | ConvertTo-Json | Set-Content $plan
    function global:dotnet {
        if ($args[0] -cne 'test' -or $args -cnotcontains '--no-build' -or $args -cnotcontains '--no-restore') { throw 'Precheck must reuse the prepared build.' }
        $focus = $args -ccontains ('FullyQualifiedName=' + $method)
        $qual = ($args -join ' ').Contains('FullyQualifiedName~Haby.PostgreSql.IntegrationTests.OwnedInfrastructureTests.')
        $current = if ($qual) { $qualification } else { $method }
        if ($args -contains '--list-tests') { $current; $global:LASTEXITCODE = 0; return }
        if ($focus) { $script:focusedCalls++ } else { $script:fullCalls++ }
        $global:LASTEXITCODE = if ($focus -and $mode -in @('failure','missing','malformed')) { 17 } else { 0 }
        if ($focus -and $mode -ceq 'missing') { return }
        if ($focus -and $mode -ceq 'wrong-case') { $current += 'Other' }
        $outcome = if ($LASTEXITCODE -eq 0) { 'Passed' } else { 'Failed' }
        $class = $current.Substring(0,$current.LastIndexOf('.')); $name = $current.Substring($current.LastIndexOf('.')+1)
        $xml = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="one"><TestMethod className="'+$class+'" name="'+$name+'" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="one" testName="'+$current+'" outcome="'+$outcome+'" duration="00:00:00.1" startTime="2026-11-13T12:00:00Z" endTime="2026-11-13T12:00:00.1Z"><Output><ErrorInfo><Message>'+$sentinel+'</Message></ErrorInfo></Output></UnitTestResult></Results><ResultSummary><Counters total="1" passed="'+[int]($outcome -ceq 'Passed')+'" failed="'+[int]($outcome -ceq 'Failed')+'" notExecuted="0" /></ResultSummary></TestRun>'
        if ($focus -and $mode -ceq 'malformed') { $xml = $xml.Replace('</TestRun>', '<'+$sentinel+'>') }
        $index = [Array]::IndexOf($args, '--results-directory')
        $file = if ($focus) { 'focused.trx' } else { 'full.trx' }
        $xml | Set-Content -LiteralPath (Join-Path $args[$index+1] $file)
        if ($focus -and $mode -ceq 'cleanup-evidence') {
            ($method+',cleanup,other') | Set-Content (Join-Path $env:HABY_TEST_TIMING_DIRECTORY 'host-diagnostics.csv')
        }
        if ($focus -and $mode -ceq 'metadata-failure') {
            New-Item -ItemType Directory (Join-Path $env:HABY_TEST_TIMING_DIRECTORY 'precheck.json') | Out-Null
        }
        if (-not $focus) {
            $resources = @{limit=4;hostGroupLimit=2}
            foreach ($field in @('active','startupAttempts','ready','cleaned','startupFailed','cleanupFailed','catalogDatabasesCreated','catalogDatabasesDropped','capacityRefused','readyActive','hostGroupsActive','hostCapacityRefused','peakReservedServers','peakSuiteReservedServers','peakControlReservedServers','peakReadyServers','peakReadySuiteServers','peakReadyControlServers','peakHostGroups')) { $resources[$field]=0 }
            $resources | ConvertTo-Json | Set-Content (Join-Path $env:HABY_TEST_TIMING_DIRECTORY 'resources.json')
        }
    }
    foreach ($mode in @('passed','failure','missing','wrong-case','cleanup-evidence','malformed','metadata-failure')) {
        $script:focusedCalls = 0; $script:fullCalls = 0
        $raw = Join-Path $directory ($mode+'-raw'); $safe = Join-Path $directory ($mode+'-safe')
        New-Item -ItemType Directory -Path $raw,$safe | Out-Null
        $result = Invoke-HabyPostgreSqlSuites -Project unused -Artifacts unused -Private $raw -Safe $safe -Coverage $cases -Suites @('functional','infrastructure-qualification') -MapPath "$PSScriptRoot/../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json" -PlanPath $plan -ServerLimit 4 -HostLimit 2 -AnonymousReflectionPrecheck -Source ('0'*40)
        Require ($script:focusedCalls -eq 1) 'Named precheck was repeated or omitted.'
        Require ($result.precheck.scope -ceq 'partial') 'Focused evidence claimed full scope.'
        $expectedExit = if ($mode -ceq 'passed') { 0 } elseif ($mode -in @('failure','missing','malformed')) { 17 } else { 1 }
        Require ($result.exitCode -eq $expectedExit) 'Primary precheck exit was masked or a failed gate passed.'
        if ($mode -ceq 'passed') {
            Require ($script:fullCalls -eq 2 -and $result.trxPaths.Count -eq 2 -and $result.groups.Count -eq 2) 'Successful precheck omitted/repeated a full suite.'
            Require ($result.precheck.cleanup -ceq 'confirmed-by-named-test-lifecycle') 'Precheck cleanup was not confirmed.'
            & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath $result.trxPaths -OutputDirectory $safe
            $executed = @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)
            Assert-PostgreSqlCoverage $cases $executed
            Require ($executed.Count -eq 2) 'Focused row leaked into full aggregation.'
        } else {
            Require ($script:fullCalls -eq 0 -and $result.trxPaths.Count -eq 0 -and $result.groups.Count -eq 0) 'Full suite ran after failed/unconfirmed precheck.'
            Require ($result.precheck.cleanup -ceq 'not-confirmed') 'Failed precheck fabricated cleanup success.'
        }
        foreach ($file in Get-ChildItem $safe -Recurse -File) { Require (-not ([IO.File]::ReadAllText($file.FullName)).Contains($sentinel)) 'Precheck public evidence disclosed protected text.' }
    }
    'Named precheck controls: prepared build reuse, partial scope, stop-before-full, primary exits, cleanup refusal and disjoint aggregation passed.'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($directory).StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($directory) -match '^haby-reflection-gate-[a-f0-9]{32}$') { Remove-Item -LiteralPath $directory -Recurse -Force }
}

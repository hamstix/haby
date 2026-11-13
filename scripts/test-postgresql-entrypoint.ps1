$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/postgresql-docker-preflight.ps1"
. "$PSScriptRoot/postgresql-coverage.ps1"
function Assert-Check([bool]$Condition, [string]$Label) { if (-not $Condition) { throw $Label } }
$sentinel = 'PROTECTED-preflight-driver-password'
foreach ($scenario in @('success', 'create', 'start', 'ready', 'network', 'foreign', 'foreign-id', 'cleanup', 'primary-and-cleanup')) {
    $state = @{ scenario = $scenario; created = $false; removed = $false; owner = ''; id = ('a' * 64); trace = [Collections.Generic.List[object]]::new() }
    $invoke = {
        param($File, [string[]]$Arguments, $TimeoutSeconds, $ChildEnvironment)
        $state.trace.Add(@($Arguments))
        $code = 0; $text = ''
        switch ($Arguments[0]) {
            'create' {
                $label = $Arguments[[Array]::IndexOf($Arguments, '--label') + 1]
                $state.owner = $label.Split('=', 2)[1]
                if ($state.scenario -eq 'create') { $code = 7; $text = $sentinel }
                else { $state.created = $true; $text = $state.id }
            }
            'start' { if ($state.scenario -in @('start', 'primary-and-cleanup')) { $code = 8; $text = $sentinel } }
            'exec' { if ($state.scenario -eq 'ready') { $code = 1; $text = $sentinel } else { $text = '1' } }
            'port' { $text = '127.0.0.1:65432' }
            'inspect' {
                if (-not $state.created) { $code = 1 }
                else { $text = $(if ($state.scenario -eq 'foreign-id') { 'b' * 64 } else { $state.id }) + ' ' + $(if ($state.scenario -eq 'foreign') { 'foreign' } else { $state.owner }) }
            }
            'rm' {
                if ($Arguments[-1] -cne $state.id) { throw 'Removal lost the exact owned ID.' }
                if ($state.scenario -in @('cleanup', 'primary-and-cleanup')) { $code = 9; $text = $sentinel }
                else { $state.removed = $true }
            }
            'container' { if ($state.created -and -not $state.removed) { $text = $state.id } }
            default { throw 'Unexpected fake command.' }
        }
        [pscustomobject]@{ exitCode = $code; output = $text; timedOut = $false }
    }.GetNewClosure()
    $connect = { param($Address, $Port) return $state.scenario -ne 'network' }.GetNewClosure()
    $result = Invoke-PostgreSqlOwnedServerCheck -InvokeCommand $invoke -Connect $connect -ReadySeconds 0
    Assert-Check (-not ($result | ConvertTo-Json -Depth 5).Contains($sentinel)) 'Preflight disclosed driver/credential text.'
    switch ($scenario) {
        'success' { Assert-Check ($result.exitCode -eq 0 -and $state.removed) ('Healthy owned server did not clean up: ' + $result.primary + '/' + $result.cleanup) }
        'create' { Assert-Check ($result.exitCode -eq 20 -and $result.primary -eq 'docker-create' -and $result.cleanup -eq 'ok') 'Create failure was masked.' }
        'start' { Assert-Check ($result.exitCode -eq 20 -and $state.removed) 'Partial startup was not cleaned.' }
        'ready' { Assert-Check ($result.exitCode -eq 20 -and $result.primary -eq 'postgres-ready' -and $state.removed) 'Readiness deadline was not bounded/cleaned.' }
        'network' { Assert-Check ($result.exitCode -eq 20 -and $result.primary -eq 'postgres-tcp' -and $state.removed) 'Loopback failure was not preserved/cleaned.' }
        'foreign' { Assert-Check ($result.cleanup -eq 'ownership-refused' -and @($state.trace | Where-Object { $_[0] -eq 'rm' }).Count -eq 0) 'Foreign identity authorized removal.' }
        'foreign-id' { Assert-Check ($result.cleanup -eq 'ownership-refused' -and @($state.trace | Where-Object { $_[0] -eq 'rm' }).Count -eq 0) 'Replaced container ID authorized removal.' }
        'cleanup' { Assert-Check ($result.exitCode -eq 21 -and $result.primary -eq 'ok' -and $result.cleanup -eq 'remove-failed') 'Cleanup failure disappeared.' }
        'primary-and-cleanup' { Assert-Check ($result.exitCode -eq 20 -and $result.primary -eq 'docker-start' -and $result.cleanup -eq 'remove-failed') 'Cleanup replaced the primary failure.' }
    }
}
Assert-Check (Test-PostgreSqlSdkVersion '10.0.402' '10.0.401') 'Supported SDK rejected.'
Assert-Check (-not (Test-PostgreSqlSdkVersion '11.0.100' '10.0.401')) 'Wrong SDK major accepted.'
Assert-Check (-not (Test-PostgreSqlSdkVersion '10.0.300' '10.0.401')) 'Wrong SDK feature band accepted.'
Assert-Check (-not (Test-PostgreSqlSdkVersion '10.0.401-preview' '10.0.401')) 'Prerelease SDK accepted.'
$runtimeInventory = 'Microsoft.NETCore.App 10.0.15 /runtime' + [Environment]::NewLine + 'Microsoft.AspNetCore.App 10.0.15 /runtime'
Assert-Check (Test-PostgreSqlRuntimeInventory $runtimeInventory) 'Supported runtime inventory rejected.'
Assert-Check (-not (Test-PostgreSqlRuntimeInventory 'Microsoft.NETCore.App 10.0.15 /runtime')) 'Missing ASP.NET runtime accepted.'
Assert-Check (-not (Test-PostgreSqlRuntimeInventory ($runtimeInventory.Replace('10.0.15', '11.0.0')))) 'Wrong runtime major accepted.'
$malformedRuntime = 'MicrosoftXNETCoreXApp 10.0.15 /runtime' + [Environment]::NewLine + 'MicrosoftXAspNetCoreXApp 10.0.15 /runtime'
Assert-Check (-not (Test-PostgreSqlRuntimeInventory $malformedRuntime)) 'Malformed runtime family names accepted.'
$unavailable = Invoke-PostgreSqlCommand 'haby-deliberately-absent-runtime-command' @('--version') 1
Assert-Check ($unavailable.exitCode -eq 127 -and -not $unavailable.output) 'Missing prerequisite command did not fail safely.'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-coverage-control-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
try {
    $discovery = Join-Path $directory 'discovery.txt'
    $first = 'Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Case(value: PROTECTED-A)'
    $second = 'Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Case(value: PROTECTED-B)'
    @($first, $second, $first) | Set-Content -LiteralPath $discovery
    $map = Join-Path $PSScriptRoot '../tests/Haby.PostgreSql.IntegrationTests/coverage-map.json'
    $cases = @(Get-PostgreSqlCoverageCases $discovery $map)
    Assert-Check ($cases.Count -eq 3 -and $cases[0].case -ceq $cases[2].case -and $cases[0].case -cne $cases[1].case) 'Theory identities/multiplicity lost.'
    Assert-Check (-not ($cases | ConvertTo-Json -Depth 5).Contains('PROTECTED-')) 'Theory parameter text escaped.'
    $executed = @($cases | ForEach-Object { [pscustomobject]@{ method = $_.method; case = $_.case; outcome = 'Passed' } })
    Assert-PostgreSqlCoverage $cases $executed
    $refused = $false
    try { Assert-PostgreSqlCoverage $cases @($executed[0], $executed[1]) } catch { $refused = $true }
    Assert-Check $refused 'Omitted repeated case was accepted.'
    $executed[1].outcome = 'NotExecuted'; $refused = $false
    try { Assert-PostgreSqlCoverage $cases $executed } catch { $refused = $true }
    Assert-Check $refused 'Skipped integration case was accepted.'
    $first.Replace('OperatorHostConfigurationTests', 'UnmappedTests') | Set-Content -LiteralPath $discovery
    $refused = $false
    try { Get-PostgreSqlCoverageCases $discovery $map | Out-Null } catch { $refused = $true }
    Assert-Check $refused 'Unmapped class was accepted.'
} finally {
    $resolved = [IO.Path]::GetFullPath($directory)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
Write-Output 'Entrypoint controls: exact ownership, startup/cleanup precedence, privacy and SDK refusal passed.'
Write-Output 'Coverage controls: theory identity/multiplicity, unknown classes, omissions and skips passed.'
exit 0

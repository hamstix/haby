# One named Host regression gate, not an arbitrary filtered-suite entrypoint.
function Invoke-HabyAnonymousReflectionPrecheck {
    param([string]$Project, [string]$Artifacts, [string]$Private, [string]$Safe,
        [object[]]$Coverage, [string]$Source)
    $method = 'Haby.PostgreSql.IntegrationTests.OperatorHostConfigurationTests.Disabled_tls_serves_grpc_on_loopback_without_certificate_settings'
    $raw = Join-Path $Private 'anonymous-reflection-precheck'
    $output = Join-Path $Safe 'anonymous-reflection-precheck'
    $oldTiming = $env:HABY_TEST_TIMING_DIRECTORY
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $testExit = 1; $reportFailed = $false; $passed = $false; $counts = $null
    try {
        New-Item -ItemType Directory -Path $raw, $output -ErrorAction Stop | Out-Null
        if ($IsLinux) { [IO.File]::SetUnixFileMode($raw, [IO.UnixFileMode]448) }
        $expected = @($Coverage | Where-Object method -CEQ $method)
        if ($expected.Count -ne 1) { throw 'Named precheck inventory must contain exactly one row.' }
        $env:HABY_TEST_TIMING_DIRECTORY = $output
        & dotnet test $Project --no-build --no-restore --artifacts-path $Artifacts --filter ('FullyQualifiedName=' + $method) --logger 'trx;LogFileName=focused.trx' --results-directory $raw *> (Join-Path $raw 'test.log')
        $testExit = $LASTEXITCODE
        & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath (Join-Path $raw 'focused.trx') -OutputDirectory $output
        $executed = @(Get-Content (Join-Path $output 'cases.json') -Raw | ConvertFrom-Json)
        Assert-PostgreSqlCoverage $expected $executed
        $counts = Get-Content (Join-Path $output 'counts.json') -Raw | ConvertFrom-Json
        # The named case awaits RunAsync's call/channel/Host disposals before it
        # can pass. Any disposal failure fails the case and emits separate CSV.
        $passed = $testExit -eq 0 -and $counts.total -eq 1 -and $counts.passed -eq 1 -and
            $counts.failed -eq 0 -and $counts.skipped -eq 0 -and
            -not (Test-Path -LiteralPath (Join-Path $output 'host-diagnostics.csv'))
    } catch {
        $reportFailed = $true
        Write-Warning 'Named reflection precheck report unavailable; protected output withheld.'
    } finally { $env:HABY_TEST_TIMING_DIRECTORY = $oldTiming }
    $exit = if ($testExit -ne 0) { $testExit } elseif ($passed -and -not $reportFailed) { 0 } else { 1 }
    $result = [pscustomobject]@{ source = $Source; scope = 'partial'; method = $method
        status = $(if ($exit -eq 0) { 'passed' } else { 'failed' }); exitCode = $exit; testExitCode = $testExit
        reportingFailed = $reportFailed; counts = $counts; seconds = $watch.Elapsed.TotalSeconds
        cleanup = $(if ($passed -and -not $reportFailed) { 'confirmed-by-named-test-lifecycle' } else { 'not-confirmed' }) }
    try { $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'precheck.json') -Encoding utf8 -ErrorAction Stop }
    catch { $result.reportingFailed = $true; if ($result.exitCode -eq 0) { $result.exitCode = 1 }; $result.status = 'failed'; $result.cleanup = 'not-confirmed' }
    return $result
}

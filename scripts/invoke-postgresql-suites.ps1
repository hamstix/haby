function Invoke-HabyPostgreSqlSuites {
    param([string]$Project, [string]$Artifacts, [string]$Private, [string]$Safe,
        [object[]]$Coverage, [string[]]$Suites, [string]$MapPath, [string]$PlanPath,
        [int]$ServerLimit, [int]$HostLimit)
    $firstExit = 0; $failedReport = $false
    $reports = [Collections.Generic.List[object]]::new()
    $trx = [Collections.Generic.List[string]]::new()
    $resourceGroups = [Collections.Generic.List[object]]::new()
    $oldTiming = $env:HABY_TEST_TIMING_DIRECTORY
    try {
        foreach ($suite in $Suites) {
            $raw = Join-Path $Private $suite; $output = Join-Path $Safe $suite
            New-Item -ItemType Directory -Path $raw, $output | Out-Null
            if ($IsLinux) { [IO.File]::SetUnixFileMode($raw, [IO.UnixFileMode]448) }
            $expected = @($Coverage | Where-Object suite -CEQ $suite)
            $filter = Get-HabySuiteFilter $Coverage $suite
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $phase = 'discovery'; $exitCode = 0; $discoverySeconds = $null; $testSeconds = $null; $exportSeconds = $null
            try {
                & dotnet test $Project --no-build --no-restore --artifacts-path $Artifacts --list-tests --filter $filter *> (Join-Path $raw 'discovery.log')
                if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE; throw 'Filtered discovery failed.' }
                $discovered = @(Get-PostgreSqlCoverageCases (Join-Path $raw 'discovery.log') $MapPath)
                Assert-HabyInventory $expected $discovered
                $discoverySeconds = $watch.Elapsed.TotalSeconds; $watch.Restart(); $phase = 'test'
                $env:HABY_TEST_TIMING_DIRECTORY = $output
                & dotnet test $Project --no-build --no-restore --artifacts-path $Artifacts --filter $filter --logger 'trx;LogFileName=full.trx' --results-directory $raw *> (Join-Path $raw 'test.log')
                $exitCode = $LASTEXITCODE; $testSeconds = $watch.Elapsed.TotalSeconds
                if ($firstExit -eq 0 -and $exitCode -ne 0) { $firstExit = $exitCode }
                $path = Join-Path $raw 'full.trx'; $trx.Add($path)
                $watch.Restart(); $phase = 'export'
                & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath $path -OutputDirectory $output
                $executed = @(Get-Content (Join-Path $output 'cases.json') -Raw | ConvertFrom-Json)
                Assert-PostgreSqlCoverage $expected $executed
                Assert-HabyGatePlan $executed $PlanPath 'postgresql-integration' @($suite)
                $resources = Get-Content (Join-Path $output 'resources.json') -Raw | ConvertFrom-Json
                Assert-HabyResourceCounts $resources $ServerLimit $HostLimit
                $resourceGroups.Add($resources)
                $exportSeconds = $watch.Elapsed.TotalSeconds; $phase = 'complete'
            } catch {
                $failedReport = $true
                if ($firstExit -eq 0) { $firstExit = $(if ($exitCode -ne 0) { $exitCode } else { 1 }) }
                Write-Warning 'Suite discovery/report/cleanup failed; protected output withheld. Remaining suites are unperformed when cleanup is unconfirmed.'
            }
            $reports.Add([pscustomobject]@{ suite = $suite; expected = $expected.Count; phase = $phase; testExitCode = $exitCode
                discoverySeconds = $discoverySeconds; testSeconds = $testSeconds; exportSeconds = $exportSeconds })
            if ($phase -ne 'complete') { break }
        }
    } catch {
        $failedReport = $true
        if ($firstExit -eq 0) { $firstExit = 1 }
        Write-Warning 'Suite orchestration failed; existing primary exit retained and protected output withheld.'
    } finally { $env:HABY_TEST_TIMING_DIRECTORY = $oldTiming }
    # Aggregate numeric counts only; peaks are maxima, not sums of parallel peaks.
    try { if ($resourceGroups.Count) {
        $combined = [ordered]@{ limit = $ServerLimit; hostGroupLimit = $HostLimit }
        foreach ($field in @('active','startupAttempts','ready','cleaned','startupFailed','cleanupFailed','catalogDatabasesCreated','catalogDatabasesDropped','capacityRefused','readyActive','hostGroupsActive','hostCapacityRefused')) {
            $combined[$field] = [int]($resourceGroups | Measure-Object -Property $field -Sum).Sum
        }
        foreach ($field in @('peakReservedServers','peakSuiteReservedServers','peakControlReservedServers','peakReadyServers','peakReadySuiteServers','peakReadyControlServers','peakHostGroups')) {
            $combined[$field] = [int]($resourceGroups | Measure-Object -Property $field -Maximum).Maximum
        }
        $combined | ConvertTo-Json | Set-Content (Join-Path $Safe 'resources.json') -Encoding utf8
    } } catch {
        $failedReport = $true
        if ($firstExit -eq 0) { $firstExit = 1 }
        Write-Warning 'Aggregate resource report unavailable; existing primary exit retained.'
    }
    return [pscustomobject]@{ exitCode = $firstExit; reportingFailed = $failedReport; groups = $reports.ToArray(); trxPaths = $trx.ToArray() }
}

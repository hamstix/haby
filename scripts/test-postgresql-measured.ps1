param([Parameter(Mandatory)][string]$ResultsDirectory, [switch]$Docker)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path $destination) { throw 'Use a new results directory for each measurement.' }
New-Item -ItemType Directory -Force $destination | Out-Null
$private = Join-Path $destination 'private'
$safe = Join-Path $destination 'safe'
New-Item -ItemType Directory -Force $private, $safe | Out-Null
$project = Join-Path $root 'tests/Haby.PostgreSql.IntegrationTests/Haby.PostgreSql.IntegrationTests.csproj'
$artifacts = Join-Path $root '.artifacts'
$phases = [Collections.Generic.List[object]]::new()
$wall = [Diagnostics.Stopwatch]::StartNew()
$testExit = 1
$reportFailed = $false
$primaryPhase = 'not-started'
$preflight = $null
$coverage = @()
$cleanupExit = $null
$oldDockerHost = $env:DOCKER_HOST
$sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
$diff = (& git -C $root diff --binary HEAD | Out-String)
$diffHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($diff))).ToLowerInvariant()
$untracked = @(& git -C $root ls-files --others --exclude-standard)
$assemblyHash = $null
$packages = [xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)
$runnerPackages = @($packages.Project.ItemGroup.PackageVersion | Where-Object Include -in @('xunit', 'xunit.runner.visualstudio', 'Microsoft.NET.Test.Sdk', 'Npgsql', 'Testcontainers.PostgreSql') |
    ForEach-Object { [pscustomobject]@{ name = $_.Include; version = $_.Version } })
$oldTimingDirectory = $env:HABY_TEST_TIMING_DIRECTORY
$env:HABY_TEST_TIMING_DIRECTORY = $safe
try {
    $blocked = $false
    if ($Docker) {
        Push-Location $root
        . (Join-Path $PSScriptRoot 'postgresql-docker-preflight.ps1')
        . (Join-Path $PSScriptRoot 'postgresql-coverage.ps1')
        $primaryPhase = 'preflight'; $timer = [Diagnostics.Stopwatch]::StartNew()
        if ($IsLinux) { [IO.File]::SetUnixFileMode($private, [IO.UnixFileMode]448) }
        $prerequisites = Invoke-PostgreSqlDockerPrerequisites $root $private
        $owned = $null
        $phases.Add([pscustomobject]@{ phase = 'preflight'; seconds = $timer.Elapsed.TotalSeconds; exitCode = $prerequisites.exitCode })
        $testExit = $prerequisites.exitCode; $cleanupExit = [int]($prerequisites.cleanup -ne 'ok')
        if ($testExit -eq 0) {
            $primaryPhase = 'owned-server-preflight'; $timer.Restart()
            $owned = Invoke-PostgreSqlOwnedServerCheck -LogDirectory $private
            $testExit = $owned.exitCode; $cleanupExit = [int]($owned.cleanup -ne 'ok')
            $phases.Add([pscustomobject]@{ phase = 'owned-server-preflight'; seconds = $timer.Elapsed.TotalSeconds; exitCode = $testExit })
        }
        $preflight = [pscustomobject]@{ prerequisites = $prerequisites; ownedServer = $owned }
        $preflight | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $safe 'preflight.json') -Encoding utf8
        $blocked = $testExit -ne 0
        if ($blocked) { Write-Warning 'PostgreSQL prerequisites/owned-server cleanup failed. See safe/preflight.json and the documented code/action table; raw driver output is private.' }
    }
    foreach ($phase in @('restore', 'build', 'test')) {
        if ($blocked) { break }
        $primaryPhase = $phase
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $arguments = @($phase, $project, '--artifacts-path', $artifacts)
        if ($phase -eq 'build') { $arguments += '--no-restore' }
        if ($phase -eq 'test') {
            if ($Docker) {
                & dotnet test $project --no-build --no-restore --artifacts-path $artifacts --list-tests *> (Join-Path $private 'discovery.log')
                $discoveryExit = $LASTEXITCODE
                $phases.Add([pscustomobject]@{ phase = 'discovery'; seconds = $timer.Elapsed.TotalSeconds; exitCode = $discoveryExit })
                if ($discoveryExit -ne 0) { $testExit = $discoveryExit; $primaryPhase = 'discovery'; break }
                $primaryPhase = 'discovery-coverage'
                $coverage = @(Get-PostgreSqlCoverageCases (Join-Path $private 'discovery.log') (Join-Path $root 'tests/Haby.PostgreSql.IntegrationTests/coverage-map.json'))
                [pscustomobject]@{ status = 'discovered-not-executed'; cases = $coverage } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $safe 'coverage.json') -Encoding utf8
                $timer.Restart()
                $primaryPhase = 'test'
            }
            $assemblyPath = Join-Path $artifacts 'bin/Haby.PostgreSql.IntegrationTests/debug/Haby.PostgreSql.IntegrationTests.dll'
            if (Test-Path $assemblyPath) { $assemblyHash = (Get-FileHash $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            $arguments += @('--no-build', '--logger', 'trx;LogFileName=full.trx', '--results-directory', $private)
        }
        & dotnet @arguments *> (Join-Path $private "$phase.log")
        $code = $LASTEXITCODE
        $phases.Add([pscustomobject]@{ phase = $phase; seconds = $timer.Elapsed.TotalSeconds; exitCode = $code })
        if ($code -ne 0) { $testExit = $code; break }
        if ($phase -eq 'test') { $testExit = $code }
    }
} catch {
    if ($testExit -eq 0) { $testExit = 1 }
    Write-Warning 'PostgreSQL runner phase failed; protected exception output withheld. Consult the phase/preflight metadata.'
} finally {
    $env:HABY_TEST_TIMING_DIRECTORY = $oldTimingDirectory
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            & (Join-Path $PSScriptRoot 'export-postgresql-timing.ps1') -TrxPath (Join-Path $private 'full.trx') -OutputDirectory $safe
            if ($Docker) {
                $executed = @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)
                Assert-PostgreSqlCoverage $coverage $executed
                [pscustomobject]@{ status = 'complete'; discovered = $coverage.Count; executed = $executed.Count; cases = $coverage } |
                    ConvertTo-Json -Depth 6 | Set-Content (Join-Path $safe 'coverage.json') -Encoding utf8
            }
        } catch {
            $reportFailed = $true
            # Do not surface parser exceptions containing raw diagnostic content.
            Write-Warning 'Timing export failed; raw results remain private.'
        }
        $phases.Add([pscustomobject]@{ phase = 'artifact-export'; seconds = $timer.Elapsed.TotalSeconds; exitCode = [int]$reportFailed })
        $buildWarningCodes = @()
        if (Test-Path (Join-Path $private 'build.log')) {
            [string]$buildText = Get-Content (Join-Path $private 'build.log') -Raw
            if (-not [string]::IsNullOrEmpty($buildText)) {
                $buildWarningCodes = @([regex]::Matches($buildText, '(?i)\bwarning\s+([A-Z]+[0-9]+)') | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() } | Sort-Object -Unique)
            }
        }
        $endSha = (& git -C $root rev-parse HEAD | Out-String).Trim()
        $endDiff = (& git -C $root diff --binary HEAD | Out-String)
        $endDiffHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($endDiff))).ToLowerInvariant()
        [pscustomobject]@{
            source = $sha; trackedDiffSha256 = $diffHash; sdk = (& dotnet --version | Out-String).Trim()
            testAssemblySha256 = $assemblyHash
            untrackedFileCountAtStart = $untracked.Count
            sourceChangedDuringRun = $sha -ne $endSha -or $diffHash -ne $endDiffHash
            installedRuntimes = @(& dotnet --list-runtimes | ForEach-Object { ($_ -split ' ')[0..1] -join ' ' })
            os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            processors = [Environment]::ProcessorCount; configuration = 'Debug'
            parallelism = 'xUnit CPU-bound default; existing exclusive collections unchanged'
            runnerPackages = $runnerPackages
            buildWarningCodes = $buildWarningCodes
            externalCluster = $env:HABY_POSTGRESQL_EPHEMERAL_EXTERNAL -eq '1'
            requiredAot = $env:HABY_AOT_CLI_REQUIRED -eq '1'
            wallSeconds = $wall.Elapsed.TotalSeconds; testExitCode = $testExit; phases = $phases
            entrypoint = $(if ($Docker) { 'hosted-linux-docker' } else { 'existing-measured' })
            primaryPhase = $primaryPhase; preflightCleanupExitCode = $cleanupExit; reportingFailed = $reportFailed
            fixtureCleanup = 'Owned by existing fixtures; failures remain xUnit failures. Preflight cleanup is reported separately.'
        } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $safe 'measurement.json') -Encoding utf8
        Write-Output ('POSTGRESQL_MEASUREMENT ' + (Get-Content (Join-Path $safe 'measurement.json') -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 5 -Compress))
        if (Test-Path (Join-Path $safe 'cases.json')) {
            foreach ($case in @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)) {
                Write-Output ('POSTGRESQL_CASE ' + ($case | Select-Object method, case, outcome, seconds | ConvertTo-Json -Compress))
            }
        }
    } catch {
        $reportFailed = $true
        Write-Warning 'Timing report unavailable; output withheld and test exit preserved.'
    }
    if ($Docker) { $env:DOCKER_HOST = $oldDockerHost; Pop-Location }
}
if ($testExit -ne 0) { exit $testExit }
if ($reportFailed) { exit 1 }
exit 0

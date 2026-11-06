param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
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
    foreach ($phase in @('restore', 'build', 'test')) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $arguments = @($phase, $project, '--artifacts-path', $artifacts)
        if ($phase -eq 'build') { $arguments += '--no-restore' }
        if ($phase -eq 'test') {
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
} finally {
    $env:HABY_TEST_TIMING_DIRECTORY = $oldTimingDirectory
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        & (Join-Path $PSScriptRoot 'export-postgresql-timing.ps1') -TrxPath (Join-Path $private 'full.trx') -OutputDirectory $safe
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
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $safe 'measurement.json') -Encoding utf8
    Write-Output ('POSTGRESQL_MEASUREMENT ' + (Get-Content (Join-Path $safe 'measurement.json') -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 5 -Compress))
    if (Test-Path (Join-Path $safe 'cases.json')) {
        foreach ($case in @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)) {
            Write-Output ('POSTGRESQL_CASE ' + ($case | Select-Object method, case, outcome, seconds | ConvertTo-Json -Compress))
        }
    }
}
if ($testExit -ne 0) { exit $testExit }
if ($reportFailed) { exit 1 }
exit 0

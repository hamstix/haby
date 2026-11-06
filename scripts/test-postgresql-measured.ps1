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
$oldTimingDirectory = $env:HABY_TEST_TIMING_DIRECTORY
$env:HABY_TEST_TIMING_DIRECTORY = $safe
try {
    foreach ($phase in @('restore', 'build', 'test')) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $arguments = @($phase, $project, '--artifacts-path', $artifacts)
        if ($phase -eq 'build') { $arguments += '--no-restore' }
        if ($phase -eq 'test') { $arguments += @('--no-build', '--logger', 'trx;LogFileName=full.trx', '--results-directory', $private) }
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
    $sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $diff = (& git -C $root diff --binary HEAD | Out-String)
    $diffHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($diff))).ToLowerInvariant()
    [pscustomobject]@{
        source = $sha; trackedDiffSha256 = $diffHash; sdk = (& dotnet --version | Out-String).Trim()
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processors = [Environment]::ProcessorCount; configuration = 'Debug'
        externalCluster = $env:HABY_POSTGRESQL_EPHEMERAL_EXTERNAL -eq '1'
        requiredAot = $env:HABY_AOT_CLI_REQUIRED -eq '1'
        wallSeconds = $wall.Elapsed.TotalSeconds; testExitCode = $testExit; phases = $phases
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $safe 'measurement.json') -Encoding utf8
}
if ($testExit -ne 0) { exit $testExit }
if ($reportFailed) { exit 1 }
exit 0

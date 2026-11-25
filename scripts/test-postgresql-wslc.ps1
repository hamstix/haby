param([string]$ResultsDirectory = (Join-Path $PSScriptRoot ('../.artifacts/postgresql-' + [Guid]::NewGuid().ToString('N'))),
    [string]$TestCase = '', [string]$TestGroup = '', [string]$SelectionPath = '')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/test-suite-plan.ps1"
$null = Test-HabyFocusedPostgreSqlArguments $TestCase $TestGroup $SelectionPath
Assert-HabyFocusedPostgreSqlPlan (Join-Path $PSScriptRoot '../tests/Haby.PostgreSql.IntegrationTests/suite-gate-map.json') $TestCase $TestGroup $SelectionPath

if ($SelectionPath) {
    $selection = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
    $root = Split-Path $PSScriptRoot -Parent
    if ($selection.source -cne ((& git -C $root rev-parse HEAD | Out-String).Trim()) -or $selection.tree -cne ((& git -C $root rev-parse 'HEAD^{tree}' | Out-String).Trim())) { throw 'Stale union source/tree before WSLc startup.' }
}
if (-not (Get-Command wslc -ErrorAction SilentlyContinue)) { throw 'wslc is unavailable in this PowerShell process.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is unavailable in this PowerShell process.' }
$helpText = (& wslc run --help 2>&1 | Out-String)
$missingOptions = @('--detach', '--rm', '--name', '--publish', '--env') |
    Where-Object { -not $helpText.Contains($_) }
if ($LASTEXITCODE -ne 0 -or $missingOptions.Count -gt 0) { throw 'Required wslc run options are unavailable.' }

$name = 'haby-pg-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 16)
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$runAttempted = $false
$testExitCode = 1
$primaryExitCode = 1
$cleanupState = 'not-attempted'
$oldMode = [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL')
$oldConnection = [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION')
$oldContainer = [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONTAINER')
try {
    $runAttempted = $true
    $null = & wslc run --detach --rm --name $name --publish "127.0.0.1:${port}:5432" `
        --env "POSTGRES_PASSWORD=$password" 'postgres:17.6-alpine' 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'The test-owned WSLc PostgreSQL container could not start.' }
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = '127.0.0.1'
    $builder['Port'] = $port
    $builder['Database'] = 'postgres'
    $builder['Username'] = 'postgres'
    $builder['Password'] = $password
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL', '1')
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION', $builder.ConnectionString)
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONTAINER', $name)
    & (Join-Path $PSScriptRoot 'test-postgresql-measured.ps1') -ResultsDirectory $ResultsDirectory -TestCase $TestCase -TestGroup $TestGroup -SelectionPath $SelectionPath
    $testExitCode = $LASTEXITCODE
    $primaryExitCode = $testExitCode
}
finally {
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL', $oldMode)
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION', $oldConnection)
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONTAINER', $oldContainer)
    if ($runAttempted) {
        $null = & wslc stop $name 2>&1
        $cleanupState = if ($LASTEXITCODE -eq 0) { 'owned-stop-confirmed' } else { 'not-confirmed' }
        if ($cleanupState -ne 'owned-stop-confirmed') {
            Write-Warning 'Cleanup failed for the run-owned PostgreSQL container.'
            if ($testExitCode -eq 0) { $testExitCode = 1 }
        }
    }
    # Stop acknowledgement is separate from fixture SQL cleanup and from a
    # whole-runtime inventory proof. Hard process termination cannot attest it.
    $measurementPath = Join-Path $ResultsDirectory 'safe/measurement.json'
    if (Test-Path -LiteralPath $measurementPath) {
        try {
            $measurement = Get-Content -LiteralPath $measurementPath -Raw | ConvertFrom-Json
            $measurement | Add-Member -NotePropertyName wrapperCleanup -NotePropertyValue ([pscustomobject]@{
                primaryExitCode = $primaryExitCode; cleanup = $cleanupState; finalExitCode = $testExitCode
            }) -Force
            $measurement | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $measurementPath -Encoding utf8
        } catch {
            Write-Warning 'Wrapper cleanup report unavailable; primary exit preserved.'
            if ($testExitCode -eq 0) { $testExitCode = 1 }
        }
    }
}
exit $testExitCode

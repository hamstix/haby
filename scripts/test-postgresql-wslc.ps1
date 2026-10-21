$ErrorActionPreference = 'Stop'

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
$oldMode = [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL')
$oldConnection = [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION')
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
    dotnet test .\tests\Haby.PostgreSql.IntegrationTests\Haby.PostgreSql.IntegrationTests.csproj `
        --artifacts-path .\.artifacts
    if ($LASTEXITCODE -ne 0) { throw 'The PostgreSQL integration tests failed.' }
}
finally {
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL', $oldMode)
    [Environment]::SetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION', $oldConnection)
    if ($runAttempted) {
        $null = & wslc stop $name 2>&1
        if ($LASTEXITCODE -ne 0) { Write-Warning "Cleanup failed for test-owned container $name." }
    }
}

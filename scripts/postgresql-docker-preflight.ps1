# Docker-only entrypoint helpers. Raw command output belongs only in private/.
function Test-PostgreSqlSdkVersion([string]$Actual, [string]$Required) {
    if ($Actual -notmatch '^\d+\.\d+\.\d+$') { return $false }
    $a = [Version]$Actual; $r = [Version]$Required
    return $a.Major -eq $r.Major -and $a.Minor -eq $r.Minor -and
        [Math]::Floor($a.Build / 100) -eq [Math]::Floor($r.Build / 100) -and $a.Build -ge $r.Build
}

function Test-PostgreSqlRuntimeInventory([string]$Inventory) {
    return $Inventory -match '(?m)^Microsoft\.NETCore\.App 10\.0\.\d+ ' -and
        $Inventory -match '(?m)^Microsoft\.AspNetCore\.App 10\.0\.\d+ '
}

function Get-PostgreSqlServerLimit($Value) {
    if ($null -eq $Value) { return 4 }
    if ([string]$Value -cnotmatch '^[2-8]$') { throw 'HABY_TEST_POSTGRESQL_SERVERS must be an integer from 2 through 8.' }
    return [int]$Value
}

function Invoke-PostgreSqlCommand {
    param([string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 20,
        [hashtable]$ChildEnvironment = @{}, [string]$LogDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($entry in $ChildEnvironment.GetEnumerator()) { $start.Environment[$entry.Key] = [string]$entry.Value }
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $expired = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($expired) { $process.Kill(); $null = $process.WaitForExit(1000) }
        if (-not $stdout.Wait(1000) -or -not $stderr.Wait(1000)) { throw 'Owned command output unavailable.' }
        $standardOutput = $stdout.GetAwaiter().GetResult()
        $text = $standardOutput + $stderr.GetAwaiter().GetResult()
        if ($LogDirectory) {
            [IO.File]::WriteAllText((Join-Path $LogDirectory ([Guid]::NewGuid().ToString('N') + '.log')), $text)
        }
        return [pscustomobject]@{ exitCode = $(if ($expired) { 124 } else { $process.ExitCode }); output = $standardOutput; timedOut = $expired }
    } catch {
        # Neither launch errors nor driver text are safe public diagnostics.
        return [pscustomobject]@{ exitCode = 127; output = ''; timedOut = $false }
    } finally { if ($process) { $process.Dispose() } }
}

function Invoke-PostgreSqlDockerPrerequisites([string]$Root, [string]$LogDirectory) {
    $phase = 'linux-required'; $probeDirectory = $null; $artifactProbe = $null; $result = $null
    try {
        if (-not $IsLinux) { throw 'Linux required.' }
        $phase = 'resource-bound'
        $serverLimit = Get-PostgreSqlServerLimit ([Environment]::GetEnvironmentVariable('HABY_TEST_POSTGRESQL_SERVERS'))
        $phase = 'external-mode'
        foreach ($name in @('HABY_POSTGRESQL_EPHEMERAL_EXTERNAL', 'HABY_POSTGRESQL_EPHEMERAL_CONNECTION', 'HABY_POSTGRESQL_EPHEMERAL_CONTAINER', 'HABY_POSTGRESQL_EPHEMERAL_RUN')) {
            if ([Environment]::GetEnvironmentVariable($name)) { throw 'External mode is not Docker mode.' }
        }
        $phase = 'source-assets'
        foreach ($path in @('HamstixHaby.slnx', 'global.json', 'schemas', 'tests/Haby.PostgreSql.IntegrationTests/Haby.PostgreSql.IntegrationTests.csproj')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $path))) { throw 'Repository asset missing.' }
        }
        $artifactDirectory = Join-Path $Root '.artifacts'
        New-Item -ItemType Directory -Path $artifactDirectory -Force -ErrorAction Stop | Out-Null
        $artifactProbe = Join-Path $artifactDirectory ('.write-probe-' + [Guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($artifactProbe, 'non-secret artifact write probe')
        $phase = 'sdk'
        $sdk = Invoke-PostgreSqlCommand dotnet @('--version') 20 @{} $LogDirectory
        $required = (Get-Content (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.version
        if ($sdk.exitCode -ne 0 -or -not (Test-PostgreSqlSdkVersion $sdk.output.Trim() $required)) { throw 'Unsupported SDK.' }
        $phase = 'runtime'
        $runtimes = Invoke-PostgreSqlCommand dotnet @('--list-runtimes') 20 @{} $LogDirectory
        if ($runtimes.exitCode -ne 0 -or -not (Test-PostgreSqlRuntimeInventory $runtimes.output)) { throw 'Required .NET/ASP.NET Core runtime unavailable.' }
        $phase = 'native-required'
        if ($env:HABY_AOT_CLI_REQUIRED -cne '1' -or -not $env:HABY_AOT_CLI_PATH -or
            -not (Test-Path -LiteralPath $env:HABY_AOT_CLI_PATH -PathType Leaf)) { throw 'Published native CLI required.' }
        foreach ($argument in @('--help', '--version')) {
            $native = Invoke-PostgreSqlCommand $env:HABY_AOT_CLI_PATH @($argument) 20 @{} $LogDirectory
            if ($native.exitCode -ne 0) { throw 'Published native CLI unavailable.' }
        }
        $phase = 'temporary-files'
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $probeDirectory = Join-Path $tempRoot ('haby-preflight-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $probeDirectory -ErrorAction Stop | Out-Null
        [IO.File]::SetUnixFileMode($probeDirectory, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
        $probe = Join-Path $probeDirectory 'mode'
        [IO.File]::WriteAllText($probe, 'non-secret temporary-file probe')
        [IO.File]::SetUnixFileMode($probe, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        if ([int][IO.File]::GetUnixFileMode($probeDirectory) -ne 448 -or [int][IO.File]::GetUnixFileMode($probe) -ne 384) { throw 'Native Unix temporary storage required.' }
        $phase = 'capacity'
        foreach ($path in @($Root, $tempRoot)) {
            if ([IO.DriveInfo]::new($path).AvailableFreeSpace -lt 1073741824) { throw 'At least 1 GiB free space required.' }
        }
        $phase = 'docker-endpoint'
        if ($env:DOCKER_HOST -and $env:DOCKER_CONTEXT) { throw 'Conflicting Docker host/context settings.' }
        if ($env:TESTCONTAINERS_HOST_OVERRIDE -and $env:TESTCONTAINERS_HOST_OVERRIDE -notin @('localhost', '127.0.0.1', '::1')) { throw 'Use local loopback Testcontainers networking.' }
        $endpoint = $env:DOCKER_HOST
        if (-not $endpoint) {
            $context = Invoke-PostgreSqlCommand docker @('context', 'inspect', '--format', '{{.Endpoints.docker.Host}}') 20 @{} $LogDirectory
            if ($context.exitCode -ne 0) { throw 'Docker context unavailable.' }
            $endpoint = $context.output.Trim()
        }
        if ($endpoint -notmatch '^unix:///[^\r\n]+$') { throw 'Use a local Linux Unix-socket Docker endpoint.' }
        # Bind Docker CLI and Testcontainers to the same local endpoint in this process only.
        $env:DOCKER_HOST = $endpoint
        $phase = 'docker-api'
        $docker = Invoke-PostgreSqlCommand docker @('version', '--format', '{{.Server.APIVersion}}') 20 @{} $LogDirectory
        if ($docker.exitCode -ne 0 -or $docker.output.Trim() -notmatch '^\d+\.\d+$') { throw 'Docker API unavailable.' }
        $phase = 'postgres-image'
        $image = Invoke-PostgreSqlCommand docker @('image', 'inspect', 'postgres:17.6-alpine', '--format', '{{.Id}}') 20 @{} $LogDirectory
        if ($image.exitCode -ne 0) {
            $pull = Invoke-PostgreSqlCommand docker @('pull', 'postgres:17.6-alpine') 120 @{} $LogDirectory
            if ($pull.exitCode -ne 0) { throw 'Pinned PostgreSQL image unavailable.' }
        }
        $result = [pscustomobject]@{ primary = 'ok'; cleanup = 'ok'; exitCode = 0; serverLimit = $serverLimit; sdk = $sdk.output.Trim(); dockerApi = $docker.output.Trim(); nativeSha256 = (Get-FileHash -LiteralPath $env:HABY_AOT_CLI_PATH).Hash.ToLowerInvariant() }
    } catch {
        $result = [pscustomobject]@{ primary = $phase; cleanup = 'ok'; exitCode = 20 }
    } finally {
        if ($artifactProbe -and (Test-Path -LiteralPath $artifactProbe)) {
            try { Remove-Item -LiteralPath $artifactProbe -Force } catch {
                $result.cleanup = 'artifact-probe-remove-failed'
                if ($result.primary -eq 'ok') { $result.exitCode = 21 }
            }
        }
        if ($probeDirectory -and (Test-Path -LiteralPath $probeDirectory)) {
            # Only the unique directory created immediately above; never clean a supplied root.
            try { Remove-Item -LiteralPath $probeDirectory -Recurse -Force } catch {
                $result.cleanup = 'temporary-remove-failed'
                if ($result.primary -eq 'ok') { $result.exitCode = 21 }
            }
        }
    }
    return $result
}

function Invoke-PostgreSqlOwnedServerCheck {
    param([string]$LogDirectory, [scriptblock]$InvokeCommand, [int]$ReadySeconds = 60, [scriptblock]$Connect)
    if (-not $InvokeCommand) {
        $InvokeCommand = { param($File, $Arguments, $TimeoutSeconds, $ChildEnvironment)
            Invoke-PostgreSqlCommand $File $Arguments $TimeoutSeconds $ChildEnvironment $LogDirectory }
    }
    if (-not $Connect) {
        $Connect = { param($Address, $Port)
            $client = [Net.Sockets.TcpClient]::new()
            try { return $client.ConnectAsync($Address, $Port).Wait(3000) -and $client.Connected }
            finally { $client.Dispose() }
        }
    }
    $owner = [Guid]::NewGuid().ToString('N'); $name = 'haby-preflight-' + $owner
    $primary = 'ok'; $cleanup = 'ok'; $phase = 'docker-create'; $attempted = $false; $id = $null
    $startupWatch = [Diagnostics.Stopwatch]::StartNew(); $startupSeconds = 0; $cleanupSeconds = 0
    try {
        $attempted = $true
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        # The password is passed only through this owned client's environment, never argv/logs/config.
        $created = & $InvokeCommand 'docker' @('create', '--name', $name, '--label', "haby.preflight.owner=$owner", '--publish', '127.0.0.1::5432', '--env', 'POSTGRES_PASSWORD', 'postgres:17.6-alpine') 30 @{ POSTGRES_PASSWORD = $password }
        $password = $null
        if ($created.exitCode -ne 0 -or $created.output.Trim() -notmatch '^[a-f0-9]{64}$') { throw 'Create failed.' }
        $id = $created.output.Trim(); $phase = 'docker-start'
        $started = & $InvokeCommand 'docker' @('start', $id) 30 @{}
        if ($started.exitCode -ne 0) { throw 'Start failed.' }
        $phase = 'postgres-ready'; $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $ready = & $InvokeCommand 'docker' @('exec', $id, 'psql', '-U', 'postgres', '-d', 'postgres', '-Atqc', 'SELECT 1') 5 @{}
            if ($ready.exitCode -eq 0 -and $ready.output.Trim() -ceq '1') { break }
            if ($timer.Elapsed.TotalSeconds -ge $ReadySeconds) { throw 'PostgreSQL readiness deadline.' }
            Start-Sleep -Milliseconds 250
        } while ($true)
        $phase = 'postgres-tcp'
        $mapping = & $InvokeCommand 'docker' @('port', $id, '5432/tcp') 20 @{}
        $portMatch = [regex]::Match($mapping.output.Trim(), '^127\.0\.0\.1:([0-9]{1,5})$')
        if ($mapping.exitCode -ne 0 -or -not $portMatch.Success -or
            [int]$portMatch.Groups[1].Value -notin 1..65535 -or
            -not (& $Connect '127.0.0.1' ([int]$portMatch.Groups[1].Value))) { throw 'Owned PostgreSQL loopback port unavailable.' }
    } catch { $primary = $phase } finally {
        $startupWatch.Stop(); $startupSeconds = $startupWatch.Elapsed.TotalSeconds
        $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
        if ($attempted) {
            try {
                $inspection = & $InvokeCommand 'docker' @('inspect', '--format', '{{.Id}} {{index .Config.Labels "haby.preflight.owner"}}', $name) 20 @{}
                if ($inspection.exitCode -eq 0) {
                    $parts = $inspection.output.Trim().Split(' ')
                    if ($parts.Count -ne 2 -or $parts[0] -notmatch '^[a-f0-9]{64}$' -or $parts[1] -cne $owner -or ($id -and $parts[0] -cne $id)) {
                        $cleanup = 'ownership-refused'
                    } else {
                        $id = $parts[0]
                        $removed = & $InvokeCommand 'docker' @('rm', '--force', '--volumes', $id) 30 @{}
                        if ($removed.exitCode -ne 0) { $cleanup = 'remove-failed' }
                        else {
                            $remaining = & $InvokeCommand 'docker' @('container', 'ls', '--all', '--no-trunc', '--filter', "id=$id", '--format', '{{.ID}}') 20 @{}
                            if ($remaining.exitCode -ne 0 -or $remaining.output.Trim()) { $cleanup = 'absence-unconfirmed' }
                        }
                    }
                } else {
                    $remaining = & $InvokeCommand 'docker' @('container', 'ls', '--all', '--no-trunc', '--filter', ('name=^/' + $name + '$'), '--format', '{{.ID}}') 20 @{}
                    if ($remaining.exitCode -ne 0 -or $remaining.output.Trim()) { $cleanup = 'absence-unconfirmed' }
                }
            } catch { $cleanup = 'unknown' }
        }
        $cleanupSeconds = $cleanupWatch.Elapsed.TotalSeconds
    }
    [pscustomobject]@{ primary = $primary; cleanup = $cleanup; containerId = $id; ownedName = $name
        startupSeconds = $startupSeconds; cleanupSeconds = $cleanupSeconds
        exitCode = $(if ($primary -ne 'ok') { 20 } elseif ($cleanup -ne 'ok') { 21 } else { 0 }) }
}

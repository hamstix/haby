param(
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot ('../.artifacts/postgresql-' + [Guid]::NewGuid().ToString('N'))),
    [string]$PrivateDirectoryBase = (Join-Path $PSScriptRoot '../.artifacts/postgresql-owned'),
    [string]$WindowsPowerShellPath,
    [string]$HostAddress = '127.0.0.1',
    [switch]$PrepareOnly,
    [switch]$CleanupOnly,
    [string]$ConfigurationPath,
    [string]$TestAssemblyPath = (Join-Path $PSScriptRoot '../.artifacts/bin/Haby.PostgreSql.IntegrationTests/debug/Haby.PostgreSql.IntegrationTests.dll')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($null -ne [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONNECTION') -or $null -ne [Environment]::GetEnvironmentVariable('HABY_POSTGRESQL_EPHEMERAL_CONTAINER')) {
    throw 'Shared external PostgreSQL configuration is obsolete. Supplied targets are refused.'
}
if ($PrepareOnly -and $CleanupOnly) { throw 'Choose preparation or cleanup.' }
if (-not $IsWindows -and -not $IsLinux) { throw 'Windows or WSL Linux is required.' }
if (-not $WindowsPowerShellPath) {
    if ($IsWindows) { $WindowsPowerShellPath = (Get-Command pwsh -ErrorAction Stop).Source }
    else { throw 'Specify the WSL path to Windows pwsh for ownership validation.' }
}

# Setup and cleanup clients have independent deadlines. Original output is private.
function Invoke-PrivateClient([string]$Executable, [string[]]$Arguments, [string]$Prefix, [int]$Seconds) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $null = $info.Environment.Remove('PSModulePath')
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::new(); $child.StartInfo = $info
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($Seconds))
    $started = $false
    try {
        $started = $child.Start()
        if (-not $started) { throw 'Owned client did not start.' }
        $stdout = $child.StandardOutput.ReadToEndAsync($deadline.Token)
        $stderr = $child.StandardError.ReadToEndAsync($deadline.Token)
        $null = $child.WaitForExitAsync($deadline.Token).GetAwaiter().GetResult()
        $text = $stdout.WaitAsync($deadline.Token).GetAwaiter().GetResult()
        $errorText = $stderr.WaitAsync($deadline.Token).GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Prefix + '.stdout', $text)
        [IO.File]::WriteAllText($Prefix + '.stderr', $errorText)
        if ($child.ExitCode -ne 0) { throw 'Owned client failed.' }
        return $text.Trim()
    } catch {
        if ($started -and -not $child.HasExited) { $child.Kill() }
        throw 'Owned client failed or timed out; ownership records retained.'
    } finally {
        $deadline.Cancel(); $deadline.Dispose(); $child.Dispose()
    }
}
function Windows-Code([string]$Code) {
    [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ErrorActionPreference='Stop';" + $Code))
}
function Quote-Value([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }

if (-not $CleanupOnly) {
    if ($ConfigurationPath) { throw 'A new run must allocate a fresh configuration.' }
    $wslc = (Get-Command wslc -ErrorAction Stop).Source
    $runId = [Guid]::NewGuid().ToString('N')
    $directory = Join-Path ([IO.Path]::GetFullPath($PrivateDirectoryBase)) $runId
    if (Test-Path -LiteralPath $directory) { throw 'Use a fresh owned run directory.' }
    $null = New-Item -ItemType Directory -Path $directory
    $cliDirectory = if ($IsWindows) { $directory } else { (& wslpath -w $directory | Out-String).Trim() }
    if (-not $IsWindows -and $LASTEXITCODE -ne 0) { throw 'The run root must be on a Windows-mounted filesystem.' }
    [IO.File]::WriteAllText((Join-Path $directory '.run-id'), $runId)
    # No credentials or raw server output exist until the protected ACL is verified.
    $code = '$p=' + (Quote-Value $cliDirectory) + ';$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;' +
        '$a=[Security.AccessControl.DirectorySecurity]::new();$a.SetAccessRuleProtection($true,$false);' +
        '$a.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,''FullControl'',''ContainerInherit,ObjectInherit'',''None'',''Allow''));' +
        'Set-Acl -LiteralPath $p -AclObject $a;Write-Output OWNED_ROOT_PROTECTED'
    $proof = Invoke-PrivateClient $WindowsPowerShellPath @('-NoProfile', '-NonInteractive', '-EncodedCommand', (Windows-Code $code)) (Join-Path $directory 'root-bootstrap') 20
    if ($proof -ne 'OWNED_ROOT_PROTECTED') { throw 'Private root protection failed.' }
    $ConfigurationPath = Join-Path $directory 'run.json'
    [ordered]@{
        formatVersion = 1; runId = $runId; localDirectory = $directory; cliDirectory = $cliDirectory
        wslcPath = $wslc; windowsPowerShellPath = $WindowsPowerShellPath; hostAddress = $HostAddress
        useLoopbackRelay = [bool]$IsLinux; pythonPath = $(if ($IsLinux) { (Get-Command python3 -ErrorAction Stop).Source } else { '' })
    } | ConvertTo-Json | Set-Content -LiteralPath $ConfigurationPath -Encoding utf8
    if ($PrepareOnly) { return $ConfigurationPath }
}

$previousMode = $env:HABY_POSTGRESQL_EPHEMERAL_EXTERNAL
$previousRun = $env:HABY_POSTGRESQL_EPHEMERAL_RUN
$testExit = 1; $cleanupFailed = $false
try {
    $env:HABY_POSTGRESQL_EPHEMERAL_EXTERNAL = '1'
    $env:HABY_POSTGRESQL_EPHEMERAL_RUN = [IO.Path]::GetFullPath($ConfigurationPath)
    if (-not $CleanupOnly) {
        # Preserve the existing unfiltered measured command and test configuration.
        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $PSScriptRoot 'test-postgresql-measured.ps1') -ResultsDirectory $ResultsDirectory
        $testExit = $LASTEXITCODE
    } else { $testExit = 0 }
} finally {
    try {
        $assembly = [IO.Path]::GetFullPath($TestAssemblyPath)
        if (-not (Test-Path -LiteralPath $assembly)) { throw 'Cleanup assembly is unavailable.' }
        $code = 'Add-Type -Path ' + (Quote-Value $assembly) + ';[Haby.PostgreSql.IntegrationTests.PostgreSqlFixtureRunCleanup]::RunAsync().GetAwaiter().GetResult()'
        $null = Invoke-PrivateClient (Get-Process -Id $PID).Path @('-NoProfile', '-NonInteractive', '-EncodedCommand', (Windows-Code $code)) (Join-Path (Split-Path $ConfigurationPath -Parent) 'supervisor-cleanup') 90
    } catch { $cleanupFailed = $true; Write-Warning 'Owned fixture cleanup is incomplete; private run records retained.' }
    $env:HABY_POSTGRESQL_EPHEMERAL_EXTERNAL = $previousMode
    $env:HABY_POSTGRESQL_EPHEMERAL_RUN = $previousRun
}
if ($testExit -ne 0) { exit $testExit }
if ($cleanupFailed) { exit 1 }
exit 0

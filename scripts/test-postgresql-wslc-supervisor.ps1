param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$destination = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a fresh control evidence directory.' }
$null = New-Item -ItemType Directory -Path $destination
$source = Join-Path $PSScriptRoot 'test-postgresql-wslc.ps1'
$text = [IO.File]::ReadAllText($source)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Supervisor source did not parse.' }
$boundary = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-PrivateClient' }, $true)
if (-not $boundary) { throw 'Controlled client boundary not found.' }
# Replace only the subprocess boundary with a virtual-time command control.
# The supervisor configuration, fixture counting, budget and exit flow stay real.
$control = @"
function Invoke-PrivateClient([string]`$Executable, [string[]]`$Arguments, [string]`$Prefix, [int]`$Seconds) {
    `$root = Split-Path `$env:HABY_POSTGRESQL_EPHEMERAL_RUN -Parent
    `$elapsed = 0
    [IO.File]::WriteAllText((Join-Path `$root 'budget.txt'), [string]`$Seconds)
    foreach (`$fixture in @(Get-ChildItem -LiteralPath `$root -Directory | Sort-Object Name)) {
        [IO.File]::AppendAllText((Join-Path `$root 'visited.txt'), `$fixture.Name + [Environment]::NewLine)
        `$elapsed += 60
        if (`$elapsed -gt `$Seconds) { throw 'Controlled supervisor deadline exhausted before remaining fixtures.' }
        # Each early recovery consumes its bounded timeout; aggregation must
        # still attempt the last fixture and retain the original failure.
    }
    throw 'Controlled earlier fixture cleanup timeout retained after all visits.'
}
"@
$controlled = $text.Substring(0, $boundary.Extent.StartOffset) + $control + $text.Substring($boundary.Extent.EndOffset)
$controlledPath = Join-Path $destination 'controlled-supervisor.ps1'
[IO.File]::WriteAllText($controlledPath, $controlled)
$run = Join-Path $destination ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $run
$fixtureIds = @(1..3 | ForEach-Object { [Guid]::NewGuid().ToString('N') } | Sort-Object)
foreach ($id in $fixtureIds) {
    $directory = Join-Path $run $id
    $null = New-Item -ItemType Directory -Path $directory
    [IO.File]::WriteAllText((Join-Path $directory 'fixture.json'), '{}')
}
$config = Join-Path $run 'run.json'; [IO.File]::WriteAllText($config, '{}')
$assembly = Join-Path $destination 'controlled.dll'; [IO.File]::WriteAllText($assembly, 'No assembly executes: structured process control only.')
$oldConnection = $env:HABY_POSTGRESQL_EPHEMERAL_CONNECTION
$oldContainer = $env:HABY_POSTGRESQL_EPHEMERAL_CONTAINER
try {
    $env:HABY_POSTGRESQL_EPHEMERAL_CONNECTION = $null; $env:HABY_POSTGRESQL_EPHEMERAL_CONTAINER = $null
    & (Get-Process -Id $PID).Path -NoProfile -File $controlledPath -CleanupOnly -ConfigurationPath $config -WindowsPowerShellPath (Get-Process -Id $PID).Path -TestAssemblyPath $assembly *> (Join-Path $destination 'supervisor-original-output.log')
    $exit = $LASTEXITCODE
} finally {
    $env:HABY_POSTGRESQL_EPHEMERAL_CONNECTION = $oldConnection; $env:HABY_POSTGRESQL_EPHEMERAL_CONTAINER = $oldContainer
}
$visited = @(Get-Content (Join-Path $run 'visited.txt'))
[ordered]@{ fixtureIds = $fixtureIds; visited = $visited; budgetSeconds = [int](Get-Content (Join-Path $run 'budget.txt')); originalFailureExit = $exit; supervisorSha256 = (Get-FileHash $source).Hash.ToLowerInvariant(); controlledSourceSha256 = (Get-FileHash $controlledPath).Hash.ToLowerInvariant() } | ConvertTo-Json | Set-Content (Join-Path $destination 'result.json')
if ($exit -eq 0) { throw 'Earlier cleanup failure was lost.' }
if ($visited.Count -ne 3 -or @(Compare-Object $fixtureIds $visited).Count) { throw 'Recovery did not visit every recorded fixture after earlier timeouts.' }
Write-Output 'MULTI_FIXTURE_TIMEOUT_CONTROL PASS: all three visited; original failure retained.'

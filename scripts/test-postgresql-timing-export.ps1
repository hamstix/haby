$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-timing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
try {
    $raw = Join-Path $directory 'raw.trx'
    $safe = Join-Path $directory 'safe'
    $sentinel = 'PROTECTED-password-snapshot-SCRAM-stdout-attachment'
    $xml = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions><UnitTest id="one"><TestMethod className="Haby.Tests.Fixture" name="Case" /></UnitTest></TestDefinitions>
  <Results>
    <UnitTestResult testId="one" testName="Case(value: SENTINEL)" outcome="Failed" duration="00:00:01.2500000" startTime="2026-11-06T12:00:00Z" endTime="2026-11-06T12:00:01.25Z">
      <Output><StdOut>SENTINEL</StdOut><ErrorInfo><Message>SENTINEL</Message><StackTrace>SENTINEL</StackTrace></ErrorInfo></Output>
      <ResultFiles><ResultFile path="SENTINEL" /></ResultFiles>
    </UnitTestResult>
    <UnitTestResult testId="one" testName="Case(value: other)" outcome="Passed" duration="00:00:00.5000000" startTime="2026-11-06T12:00:02Z" endTime="2026-11-06T12:00:02.5Z" />
  </Results>
  <ResultSummary><Counters total="2" passed="1" failed="1" notExecuted="0" /><Output><StdOut>SENTINEL</StdOut></Output></ResultSummary>
</TestRun>
'@
    $xml.Replace('SENTINEL', $sentinel) | Set-Content $raw
    & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath $raw -OutputDirectory $safe
    foreach ($file in Get-ChildItem $safe -File) {
        if ((Get-Content $file.FullName -Raw).Contains($sentinel)) { throw 'Protected content escaped.' }
    }
    $cases = @(Get-Content (Join-Path $safe 'cases.json') -Raw | ConvertFrom-Json)
    if ($cases.Count -ne 2 -or $cases[0].case -eq $cases[1].case -or @($cases | Where-Object outcome -eq Failed).Count -ne 1) { throw 'Case inventory/outcomes changed.' }
    $refused = $false
    $xml.Replace('Case(value: other)', 'Case(value: SENTINEL)') | Set-Content $raw
    try { & "$PSScriptRoot/export-postgresql-timing.ps1" -TrxPath $raw -OutputDirectory (Join-Path $directory 'duplicate') } catch { $refused = $true }
    if (-not $refused) { throw 'Duplicate execution was accepted.' }
    if (Test-Path (Join-Path $directory 'duplicate')) { throw 'Failed validation published artifacts.' }
    # Exercise orchestration in a child PowerShell with a failing fake test runner.
    # No real tests are retried, filtered or substituted in production/CI.
    $xml.Replace('SENTINEL', $sentinel) | Set-Content $raw
    $child = Join-Path $directory 'failure.ps1'
    @'
function global:dotnet {
    if ($args[0] -eq '--version') { '10.0.test'; return }
    if ($args[0] -eq 'test') {
        $index = [Array]::IndexOf($args, '--results-directory')
        if ($env:HABY_TIMING_TEST_VALID_TRX -eq '1') {
            Copy-Item -LiteralPath $env:HABY_TIMING_TEST_TRX -Destination (Join-Path $args[$index + 1] 'full.trx')
        }
        $global:LASTEXITCODE = 17
        if ($env:HABY_TIMING_TEST_REPORT_FAILURE -eq '1') {
            function global:ConvertTo-Json { throw 'Injected artifact output failure.' }
        }
    } else { $global:LASTEXITCODE = 0 }
}
& $env:HABY_TIMING_TEST_RUNNER -ResultsDirectory $env:HABY_TIMING_TEST_DESTINATION
exit $LASTEXITCODE
'@ | Set-Content $child
    $env:HABY_TIMING_TEST_RUNNER = Join-Path $PSScriptRoot 'test-postgresql-measured.ps1'
    $env:HABY_TIMING_TEST_TRX = $raw
    foreach ($valid in @('1', '0', 'report-failure')) {
        $env:HABY_TIMING_TEST_VALID_TRX = $(if ($valid -eq '0') { '0' } else { '1' })
        $env:HABY_TIMING_TEST_REPORT_FAILURE = $(if ($valid -eq 'report-failure') { '1' } else { '0' })
        $env:HABY_TIMING_TEST_DESTINATION = Join-Path $directory "failed-run-$valid"
        & (Join-Path $PSHOME 'pwsh') -NoProfile -File $child *> (Join-Path $directory "child-$valid.log")
        if ($LASTEXITCODE -ne 17) { throw 'Test exit code was masked by timing export.' }
        if ($valid -eq 'report-failure') { continue }
        $manifest = Join-Path $env:HABY_TIMING_TEST_DESTINATION 'safe/measurement.json'
        if (-not (Test-Path $manifest)) {
            $diagnostic = Get-Content (Join-Path $directory "child-$valid.log") -Raw
            $locations = @([regex]::Matches($diagnostic, 'test-postgresql-measured\.ps1:[0-9]+') | ForEach-Object Value | Sort-Object -Unique)
            throw ('Measurement manifest absent; safe runner locations: ' + ($locations -join ', '))
        }
        $metadata = Get-Content $manifest -Raw | ConvertFrom-Json
        if ($metadata.testExitCode -ne 17) { throw 'Recorded failure code changed.' }
    }
    Write-Output 'Timing exporter: redaction, distinct parameterized cases, failed outcomes and duplicate refusal passed.'
    Write-Output 'Measured runner: failing test exit survives successful and failed artifact export.'
} finally {
    # This exact newly-created temporary directory is owned by this invocation.
    if ([IO.Path]::GetFullPath($directory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
exit 0

$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-failure-fixtures-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$sentinel = 'PROTECTED_SYNTHETIC_188'
$exporter = Join-Path $PSScriptRoot 'export-postgresql-timing.ps1'
function Assert-That([bool]$condition, [string]$label) { if (-not $condition) { throw $label } }
function Write-Fixture([object[]]$messages, [string[]]$outcomes = @(), [string]$method = 'Case') {
    $results = for ($i = 0; $i -lt $messages.Count; $i++) {
        $outcome = if ($outcomes.Count) { $outcomes[$i] } else { 'Failed' }
        $errorXml = if ($null -eq $messages[$i]) { '' } else { '<Output><ErrorInfo><Message>' + [Security.SecurityElement]::Escape($messages[$i]) + '</Message><StackTrace>' + $sentinel + '</StackTrace></ErrorInfo><StdOut>' + $sentinel + '</StdOut></Output>' }
        '<UnitTestResult testId="one" testName="case-' + $i + '-' + $sentinel + '" outcome="' + $outcome + '" duration="00:00:01" startTime="2026-11-12T00:00:00Z" endTime="2026-11-12T00:00:01Z">' + $errorXml + '<ResultFiles><ResultFile path="' + $sentinel + '" /></ResultFiles></UnitTestResult>'
    }
    $passed = @($outcomes | Where-Object { $_ -eq 'Passed' }).Count
    $skipped = @($outcomes | Where-Object { $_ -eq 'NotExecuted' }).Count
    $failed = $messages.Count - $passed - $skipped
    $xml = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest id="one"><TestMethod className="Haby.Tests.Fixture" name="' + $method + '" /></UnitTest></TestDefinitions><Results>' + ($results -join '') + '</Results><ResultSummary><Counters total="' + $messages.Count + '" passed="' + $passed + '" failed="' + $failed + '" notExecuted="' + $skipped + '" /></ResultSummary></TestRun>'
    [IO.File]::WriteAllText((Join-Path $directory 'raw.trx'), $xml)
}
function Export-Fixture {
    $console = (& $exporter -TrxPath (Join-Path $directory 'raw.trx') -OutputDirectory (Join-Path $directory 'safe') *>&1 | Out-String)
    Assert-That (-not $console.Contains($sentinel)) 'Console disclosed protected text.'
    foreach ($file in Get-ChildItem (Join-Path $directory 'safe') -File) {
        $text = [IO.File]::ReadAllText($file.FullName)
        Assert-That (-not $text.Contains($sentinel)) 'Safe file disclosed protected text.'
    }
    $json = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw
    $schema = Join-Path $PSScriptRoot '../docs/development/schemas/postgresql-failure-evidence-v1.schema.json'
    Assert-That ($json | Test-Json -SchemaFile $schema -ErrorAction Stop) 'Sidecar violates schema.'
    $e = $json | ConvertFrom-Json
    $cases = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)
    foreach ($record in $e.records) {
        Assert-That (@($cases | Where-Object { $_.case -ceq $record.case -and $_.method -ceq $record.method -and $_.outcome -eq 'Failed' }).Count -eq 1) 'Failure record lost canonical identity.'
        Assert-That ($record.rootCause -ceq 'unknown' -and $record.phase -ceq 'unavailable') 'Parsed signature claimed cause/phase.'
    }
    return $e
}
function Assert-Refused {
    $caught = $null
    try { & $exporter -TrxPath (Join-Path $directory 'raw.trx') -OutputDirectory (Join-Path $directory 'safe') } catch { $caught = $_ }
    Assert-That ($null -ne $caught) 'Invalid fixture accepted.'
    Assert-That ($caught.Exception.Message -ceq 'Safe timing export failed; report withheld.') 'Raw parser error escaped.'
    Assert-That (-not ($caught | Out-String).Contains($sentinel)) 'Protected parser content escaped.'
    Assert-That (@(Get-ChildItem (Join-Path $directory 'safe') -File).Count -eq 0) 'Stale/partial export survived rejection.'
}
try {
    # Independently specified public codes, rather than exporter-derived expectations.
    $known = @(
        @('Assert.Equal() Failure', 'assertion', 'equal', 'unknown', 'unavailable', 'unavailable'),
        @('Assert.True() Failure', 'assertion', 'true', 'unknown', 'unavailable', 'unavailable'),
        @('Assert.False() Failure', 'assertion', 'false', 'unknown', 'unavailable', 'unavailable'),
        @('Assert.Null() Failure', 'assertion', 'null', 'unknown', 'unavailable', 'unavailable'),
        @('Assert.NotNull() Failure', 'assertion', 'not-null', 'unknown', 'unavailable', 'unavailable'),
        @('System.TimeoutException : The operation has timed out.', 'timeout', 'unknown', 'timeout', 'unavailable', 'unavailable'),
        @('System.OperationCanceledException : The operation was canceled.', 'cancellation', 'unknown', 'cancellation', 'unavailable', 'unavailable'),
        @('System.Net.Sockets.SocketException (111): Connection refused', 'transport', 'unknown', 'socket', 'unavailable', 'connection-refused'),
        @('System.Net.Sockets.SocketException (10061): No connection could be made because the target machine actively refused it.', 'transport', 'unknown', 'socket', 'unavailable', 'connection-refused'),
        @('Grpc.Core.RpcException : Status(StatusCode="Unavailable", Detail="Service is unavailable.")', 'transport', 'unknown', 'grpc', 'unavailable', 'unavailable'),
        @('Grpc.Core.RpcException : Status(StatusCode="DeadlineExceeded", Detail="Deadline exceeded.")', 'timeout', 'unknown', 'grpc', 'deadline-exceeded', 'unavailable')
    )
    Write-Fixture @($known | ForEach-Object { $_[0] })
    $e = Export-Fixture
    Assert-That ($e.schemaVersion -eq 1 -and $e.failedCases -eq 11 -and $e.emittedRecords -eq 11 -and $e.omittedRecords -eq 0 -and -not $e.truncated) 'Known fixture accounting changed.'
    foreach ($i in 0..10) {
        $identity = 'Haby.Tests.Fixture.Case' + "`ncase-$i-$sentinel"
        $id = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
        $r = @($e.records | Where-Object case -CEQ $id)[0]
        Assert-That ($r.classificationMode -ceq 'parsed-signature' -and $r.category -ceq $known[$i][1] -and $r.assertionKind -ceq $known[$i][2] -and $r.exceptionCategory -ceq $known[$i][3] -and $r.grpcCode -ceq $known[$i][4] -and $r.socketCode -ceq $known[$i][5]) 'Known signature codes changed.'
    }
    $first = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw
    $null = Export-Fixture
    Assert-That ($first -ceq (Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw)) 'Sidecar is nondeterministic.'
    $unknown = @($null, '', $sentinel, "$sentinel Assert.Equal() Failure", "Assert.Equal() Failure`nAssert.True() Failure", "Assert.Equal() Failure`nExpected: $sentinel", 'assert.equal() Failure', ('x' * 4097), "`u{202e}$sentinel", "$sentinel <>&`" \ Grpc.Core.RpcException : Status(StatusCode=`"Unavailable`")")
    Write-Fixture $unknown
    $e = Export-Fixture
    Assert-That ($e.records.Count -eq $unknown.Count -and @($e.records | Where-Object { $_.classificationMode -cne 'unavailable' -or $_.category -cne 'unknown' -or $_.assertionKind -cne 'unknown' -or $_.exceptionCategory -cne 'unknown' -or $_.grpcCode -cne 'unavailable' -or $_.socketCode -cne 'unavailable' }).Count -eq 0) 'Unrecognized text acquired certainty.'
    Write-Fixture @('Assert.Equal() Failure', 'Assert.True() Failure', 'Assert.False() Failure') @('Failed', 'Passed', 'NotExecuted')
    $e = Export-Fixture
    $counts = Get-Content (Join-Path $directory 'safe/counts.json') -Raw | ConvertFrom-Json
    Assert-That ($e.records.Count -eq 1 -and $counts.total -eq 3 -and $counts.passed -eq 1 -and $counts.failed -eq 1 -and $counts.skipped -eq 1) 'Mixed outcomes changed.'
    $cases = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)
    Assert-That (@($cases | Where-Object { $_.seconds -ne 1 -or $_.start -cne '2026-11-12T00:00:00.0000000+00:00' -or $_.end -cne '2026-11-12T00:00:01.0000000+00:00' }).Count -eq 0) 'Canonical timing changed.'
    Write-Fixture @($null, $null) @('Passed', 'NotExecuted')
    $e = Export-Fixture
    Assert-That ($e.failedCases -eq 0 -and $e.records.Count -eq 0) 'No-failure run fabricated evidence.'
    Write-Fixture (@('Assert.Equal() Failure') * 1026)
    $e = Export-Fixture
    $cases = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)
    Assert-That ($e.failedCases -eq 1026 -and $e.records.Count -eq 1024 -and $e.omittedRecords -eq 2 -and $e.truncated -and $cases.Count -eq 1026) 'Record cap lost canonical cases.'
    Write-Fixture @('Assert.Equal() Failure') @() ('A' * 512)
    $e = Export-Fixture
    Assert-That ($e.omittedRecords -eq 1 -and $e.records.Count -eq 0 -and $e.truncated) 'Method bound not explicit.'
    Write-Fixture (@('Assert.Equal() Failure') * 1024) @() ('A' * (512 - 'Haby.Tests.Fixture.'.Length))
    $e = Export-Fixture
    Assert-That ($e.records.Count -eq 1024 -and (Get-Item (Join-Path $directory 'safe/failure-evidence.json')).Length -le 1048576) 'Maximum-width records violate byte bound.'
    Write-Fixture @('Assert.Equal() Failure')
    $raw = Join-Path $directory 'raw.trx'
    $valid = Get-Content $raw -Raw
    foreach ($invalid in @(
        $valid.Replace('name="Case"', ('name="' + $sentinel + ' /"')),
        $valid.Replace('</TestDefinitions>', '<UnitTest id="one"><TestMethod className="Haby.Tests.Fixture" name="Other" /></UnitTest></TestDefinitions>'),
        $valid.Replace('total="1"', 'total="2"'),
        $valid.Replace('00:00:01"', ($sentinel + '"')),
        $valid.Replace('</TestRun>', ('<' + $sentinel + '>')),
        $valid.Replace('Assert.Equal() Failure', ($sentinel + [char]1)),
        ('<!DOCTYPE TestRun [<!ENTITY e SYSTEM "file:///' + $sentinel + '">]>' + $valid)
    )) {
        $valid | Set-Content $raw
        $null = Export-Fixture
        $invalid | Set-Content $raw
        Assert-Refused
    }
    $valid.Replace('<Message>Assert.Equal() Failure</Message>', '<Message>Assert.Equal() Failure</Message><Message>Assert.True() Failure</Message>') | Set-Content $raw
    $e = Export-Fixture
    Assert-That ($e.records[0].classificationMode -ceq 'unavailable') 'Ambiguous ErrorInfo classified.'
    $valid.Replace('<Message>Assert.Equal() Failure</Message>', '<Message><value>Assert.Equal() Failure</value></Message>') | Set-Content $raw
    $e = Export-Fixture
    Assert-That ($e.records[0].classificationMode -ceq 'unavailable') 'Nested message classified.'
    # Force a failure after the first canonical file is moved into public output.
    $valid | Set-Content $raw
    $null = Export-Fixture
    function Move-Item { Microsoft.PowerShell.Management\Move-Item @args; throw 'PROTECTED_SYNTHETIC_188 publication failure' }
    try { Assert-Refused } finally { Remove-Item Function:Move-Item }
    ('<TestRun>' + ('x' * 67108865) + '</TestRun>') | Set-Content $raw
    Assert-Refused
    Write-Output 'Synthetic failure evidence: exact codes/schema/IDs, unknowns, privacy, deterministic caps and fail-safe publication passed.'
} finally {
    if ([IO.Path]::GetFullPath($directory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $directory -Recurse -Force }
}

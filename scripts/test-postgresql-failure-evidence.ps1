param([string]$ExporterPath = '')
$ErrorActionPreference = 'Stop'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('haby-failure-fixtures-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$sentinel = 'PROTECTED_SYNTHETIC_188'
$exporter = if ($ExporterPath) { $ExporterPath } else { Join-Path $PSScriptRoot 'export-postgresql-timing.ps1' }
$owned = @('cases.json','counts.json','top-duration.csv','class-duration.csv','results.trx','failure-evidence.json')
$inputs = @((Join-Path $directory 'raw.trx'))
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
    $console = (& $exporter -TrxPath $inputs -OutputDirectory (Join-Path $directory 'safe') *>&1 | Out-String)
    Assert-That (-not $console.Contains($sentinel)) 'Console disclosed protected text.'
    foreach ($file in Get-ChildItem (Join-Path $directory 'safe') -File) {
        $text = [IO.File]::ReadAllText($file.FullName)
        Assert-That (-not $text.Contains($sentinel)) 'Safe file disclosed protected text.'
    }
    Assert-That (Test-Path (Join-Path $directory 'safe/failure-evidence.json')) 'Required failure-evidence sidecar absent.'
    $json = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw
    $schema = Join-Path $PSScriptRoot '../docs/development/schemas/postgresql-failure-evidence-v1.schema.json'
    Assert-That ($json | Test-Json -SchemaFile $schema -ErrorAction Stop) 'Sidecar violates schema.'
    $e = $json | ConvertFrom-Json
    $cases = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)
    foreach ($record in $e.records) {
        Assert-That (@($cases | Where-Object { $_.row -ceq $record.row -and $_.case -ceq $record.case -and $_.method -ceq $record.method -and $_.outcome -eq 'Failed' }).Count -eq 1) 'Failure record lost canonical identity.'
        Assert-That ($record.rootCause -ceq 'unknown' -and $record.phase -ceq 'unavailable') 'Parsed signature claimed cause/phase.'
    }
    Assert-That ($e.emittedRecords -eq $e.records.Count -and $e.failedCases -eq @($cases | Where-Object outcome -CEQ 'Failed').Count -and $e.omittedRecords -eq $e.failedCases - $e.emittedRecords -and $e.truncated -eq ($e.omittedRecords -gt 0)) 'Diagnostic accounting changed.'
    Assert-That ((Get-Item (Join-Path $directory 'safe/failure-evidence.json')).Length -le 1048576) 'Sidecar exceeds byte cap.'
    return $e
}
function Assert-Refused {
    $caught = $null
    $console = [Text.StringBuilder]::new()
    try {
        & $exporter -TrxPath $inputs -OutputDirectory (Join-Path $directory 'safe') *>&1 |
            ForEach-Object { [void]$console.AppendLine(($_ | Out-String)) }
    } catch { $caught = $_; [void]$console.AppendLine(($_ | Out-String)) }
    Assert-That ($null -ne $caught) 'Invalid fixture accepted.'
    Assert-That ($caught.Exception.Message -ceq 'Safe timing export failed; report withheld.') 'Raw parser error escaped.'
    Assert-That (-not $console.ToString().Contains($sentinel)) 'Protected parser/console content escaped.'
    foreach ($file in Get-ChildItem (Join-Path $directory 'safe') -File) {
        Assert-That (-not ([IO.File]::ReadAllText($file.FullName)).Contains($sentinel)) 'Failure path disclosed protected text.'
        Assert-That ($file.Name -cnotin $owned) 'Stale/partial export survived rejection.'
        Assert-That ([IO.File]::ReadAllText($file.FullName) -ceq 'runner-owned') 'Exporter changed another owner report.'
    }
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
    foreach ($badSchema in @('root-field','raw-message','runtime-proof','unknown-code','invalid-row')) {
        $invalid = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw | ConvertFrom-Json -AsHashtable
        switch ($badSchema) {
            'root-field' { $invalid['raw'] = $sentinel }
            'raw-message' { $invalid.records[0]['message'] = $sentinel }
            'runtime-proof' { $invalid.records[0].classificationMode = 'runtime-type' }
            'unknown-code' { $invalid.records[0].category = 'arbitrary' }
            'invalid-row' { $invalid.records[0].row = 'a' * 63 }
        }
        $accepted = ($invalid | ConvertTo-Json -Depth 5 | Test-Json -SchemaFile (Join-Path $PSScriptRoot '../docs/development/schemas/postgresql-failure-evidence-v1.schema.json') -ErrorAction SilentlyContinue)
        Assert-That (-not $accepted) 'Closed schema accepted extra fields, runtime claims or invalid codes/identity.'
    }
    $first = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw
    $null = Export-Fixture
    Assert-That ($first -ceq (Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw)) 'Sidecar is nondeterministic.'
    $unknown = @($null, '', $sentinel, "$sentinel Assert.Equal() Failure", "Assert.Equal() Failure`nAssert.True() Failure", "Assert.Equal() Failure`nExpected: $sentinel`nActual: $sentinel", 'assert.equal() Failure', ('x' * 4096), ('x' * 4097), ('x' * 4096 + 'Assert.Equal() Failure'), 'Ошибка: время ожидания истекло', "`u{202e}$sentinel", "$sentinel <>&`" \ Grpc.Core.RpcException : Status(StatusCode=`"Unavailable`")")
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
    Write-Fixture (@('Assert.Equal() Failure') * 1025)
    $e = Export-Fixture
    $cases = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)
    Assert-That ($e.failedCases -eq 1025 -and $e.records.Count -eq 1024 -and $e.omittedRecords -eq 1 -and $e.truncated -and $cases.Count -eq 1025) 'Record cap lost canonical cases.'
    Write-Fixture @('Assert.Equal() Failure') @() ('A' * (513 - 'Haby.Tests.Fixture.'.Length))
    $e = Export-Fixture
    Assert-That ($e.omittedRecords -eq 1 -and $e.records.Count -eq 0 -and $e.truncated) 'Method bound not explicit.'
    $overMethod = @(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json)[0].method
    Assert-That ($overMethod -ceq ('Haby.Tests.Fixture.' + ('A' * (513 - 'Haby.Tests.Fixture.'.Length)))) 'Method admission truncated canonical identity.'
    Write-Fixture (@('Assert.Equal() Failure') * 1024) @() ('A' * (512 - 'Haby.Tests.Fixture.'.Length))
    $e = Export-Fixture
    Assert-That ($e.records.Count -eq 1024 -and (Get-Item (Join-Path $directory 'safe/failure-evidence.json')).Length -le 1048576) 'Maximum-width records violate byte bound.'
    Assert-That (@($e.records | Where-Object { $_.method.Length -ne 512 }).Count -eq 0) 'Exact method admission boundary changed.'
    # A serializer producing extra valid JSON whitespace exercises the independent
    # byte guard at exactly 1 MiB and one byte above, without a product failure.
    Write-Fixture @('Assert.Equal() Failure')
    $null = Export-Fixture
    $normalBytes = (Get-Item (Join-Path $directory 'safe/failure-evidence.json')).Length
    foreach ($extra in @(0,1)) {
        $diagnosticPadding = ' ' * (1048576 - $normalBytes + $extra)
        function ConvertTo-Json {
            param([Parameter(ValueFromPipeline)]$InputObject, [int]$Depth)
            process {
                $json = Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $InputObject -Depth $Depth
                if ($InputObject -is [Collections.IDictionary] -and $InputObject.Contains('schemaVersion')) { $json += $diagnosticPadding }
                $json
            }
        }
        try { $e = Export-Fixture } finally { Remove-Item Function:ConvertTo-Json }
        Assert-That ($e.failedCases -eq 1 -and $e.emittedRecords -eq (1 - $extra) -and $e.omittedRecords -eq $extra) 'Exact/over byte bound accounting changed.'
        if ($extra -eq 0) {
            $actualBytes = (Get-Item (Join-Path $directory 'safe/failure-evidence.json')).Length
            Assert-That ($actualBytes -eq 1048576) "Exact byte-bound fixture did not execute: $actualBytes bytes."
        }
        Assert-That (@(Get-Content (Join-Path $directory 'safe/cases.json') -Raw | ConvertFrom-Json).Count -eq 1) 'Byte cap changed canonical inventory.'
    }
    Write-Fixture @('Assert.Equal() Failure')
    $raw = Join-Path $directory 'raw.trx'
    $valid = Get-Content $raw -Raw
    foreach ($name in @('measurement.json','resources.json','setup.csv','other.txt')) {
        [IO.File]::WriteAllText((Join-Path $directory "safe/$name"), 'runner-owned')
    }
    foreach ($invalid in @(
        $valid.Replace('className="Haby.Tests.Fixture" name="Case"', 'className="A" name=""'),
        $valid.Replace('className="Haby.Tests.Fixture"', 'className=""'),
        $valid.Replace(' className="Haby.Tests.Fixture"', ''),
        $valid.Replace('name="Case"', 'name=""'),
        $valid.Replace(' name="Case"', ''),
        $valid.Replace('id="one"', 'id=""'),
        $valid.Replace(' id="one"', ''),
        $valid.Replace('testId="one"', 'testId=""'),
        $valid.Replace(' testId="one"', ''),
        $valid.Replace('testName="case-0-' + $sentinel + '"', 'testName=""'),
        $valid.Replace('<TestMethod className="Haby.Tests.Fixture" name="Case" />', ''),
        $valid.Replace('</UnitTest>', '<TestMethod className="Haby.Tests.Fixture" name="Case" /></UnitTest>'),
        $valid.Replace('name="Case"', ('name="' + $sentinel + ' /"')),
        $valid.Replace('</TestDefinitions>', '<UnitTest id="one"><TestMethod className="Haby.Tests.Fixture" name="Other" /></UnitTest></TestDefinitions>'),
        $valid.Replace('total="1"', 'total="2"'),
        $valid.Replace('outcome="Failed"', 'outcome="failed"'),
        $valid.Replace('</Results>', '</Results><Results />'),
        $valid.Replace('</ResultSummary>', '<Counters total="1" /></ResultSummary>'),
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
    $valid.Replace('</ErrorInfo>', '</ErrorInfo><ErrorInfo><Message>Assert.True() Failure</Message></ErrorInfo>') | Set-Content $raw
    $e = Export-Fixture
    Assert-That ($e.records[0].category -ceq 'unknown') 'Conflicting ErrorInfo acquired certainty.'
    $valid.Replace('<Message>Assert.Equal() Failure</Message>', '<Message><![CDATA[Assert.Equal() Failure]]></Message>') | Set-Content $raw
    $e = Export-Fixture
    Assert-That ($e.records[0].classificationMode -ceq 'parsed-signature' -and $e.records[0].rootCause -ceq 'unknown') 'Forged exact signature claimed runtime authority.'
    # Equal display identities in different TRXs are distinct actual failed rows.
    $valid | Set-Content $raw
    $second = Join-Path $directory 'second.trx'
    $valid.Replace('id="one"','id="two"').Replace('testId="one"','testId="two"').Replace('Assert.Equal() Failure','System.TimeoutException : The operation has timed out.') | Set-Content $second
    $inputs = @($raw,$second)
    $e = Export-Fixture
    Assert-That ($e.records.Count -eq 2 -and $e.records[0].case -ceq $e.records[1].case -and $e.records[0].row -cne $e.records[1].row) 'Cross-TRX repeated display lost failure-row joins.'
    $firstRow = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($e.records[0].case + "`none"))).ToLowerInvariant()
    Assert-That (@($e.records | Where-Object { $_.row -ceq $firstRow -and $_.category -ceq 'assertion' }).Count -eq 1 -and @($e.records | Where-Object { $_.row -cne $firstRow -and $_.category -ceq 'timeout' }).Count -eq 1) 'Failure metadata was associated by case rather than row.'
    $aggregate = Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw
    $inputs = @($second,$raw); $null = Export-Fixture
    Assert-That ($aggregate -ceq (Get-Content (Join-Path $directory 'safe/failure-evidence.json') -Raw)) 'Input order changed failure evidence.'
    $individual = @(foreach ($input in @($raw,$second)) {
        $inputs = @($input); $single = Export-Fixture; $single.records
    })
    Assert-That ((@($individual | Sort-Object row | ConvertTo-Json -Depth 5) -join '') -ceq (@($e.records | Sort-Object row | ConvertTo-Json -Depth 5) -join '')) 'Per-suite and aggregate diagnostics diverged.'
    $inputs = @($raw,$raw); Assert-Refused
    $inputs = @($raw)
    # Force a failure after the first canonical file is moved into public output.
    $valid | Set-Content $raw
    $null = Export-Fixture
    function Move-Item { Microsoft.PowerShell.Management\Move-Item @args; throw 'PROTECTED_SYNTHETIC_188 publication failure' }
    try { Assert-Refused } finally { Remove-Item Function:Move-Item }
    # Also fail before any move and after the sidecar was physically published.
    foreach ($at in @('before','sidecar')) {
        $publicationFailurePoint = $at
        $null = Export-Fixture
        function Move-Item {
            if ($publicationFailurePoint -ceq 'before') { throw 'PROTECTED_SYNTHETIC_188 before publication' }
            Microsoft.PowerShell.Management\Move-Item @args
            if (($args -join ' ').Contains('failure-evidence.json')) { throw 'PROTECTED_SYNTHETIC_188 after sidecar publication' }
        }
        try { Assert-Refused } finally { Remove-Item Function:Move-Item }
    }
    $null = Export-Fixture
    Remove-Item -LiteralPath (Join-Path $directory 'safe/counts.json')
    New-Item -ItemType Directory (Join-Path $directory 'safe/counts.json') | Out-Null
    Assert-Refused
    Remove-Item -LiteralPath (Join-Path $directory 'safe/counts.json')
    if ($IsLinux) {
        # Actual dangling-link obstruction: never follow it into another owner.
        $target = Join-Path $directory 'outside-owned-output.json'
        $link = Join-Path $directory 'safe/counts.json'
        [IO.File]::CreateSymbolicLink($link, $target) | Out-Null
        $caught = $null
        try { & $exporter -TrxPath $raw -OutputDirectory (Join-Path $directory 'safe') } catch { $caught = $_ }
        Assert-That ($caught.Exception.Message -ceq 'Safe timing export failed; report withheld.' -and -not (Test-Path $target) -and -not (Test-Path (Join-Path $directory 'safe/failure-evidence.json'))) 'Dangling output alias escaped ownership.'
        [IO.File]::Delete($link)
    }
    # Refuse an overlapping input without deleting or replacing that raw input.
    $overlap = Join-Path $directory 'overlap'; New-Item -ItemType Directory $overlap | Out-Null
    $protectedRaw = Join-Path $overlap 'cases.json'; $valid | Set-Content $protectedRaw
    $before = (Get-FileHash $protectedRaw).Hash; $caught = $null
    try { & $exporter -TrxPath $protectedRaw -OutputDirectory $overlap } catch { $caught = $_ }
    Assert-That ($caught.Exception.Message -ceq 'Safe timing export failed; report withheld.' -and (Get-FileHash $protectedRaw).Hash -ceq $before) 'Overlapping raw input was mutated.'
    # Valid XML at the parser limit succeeds; one additional character fails.
    $padding = ' ' * (67108864 - $valid.TrimEnd().Length)
    ($valid.TrimEnd() + $padding) | Set-Content $raw -NoNewline
    $null = Export-Fixture
    ($valid.TrimEnd() + $padding + ' ') | Set-Content $raw -NoNewline
    Assert-Refused
    Write-Output 'Synthetic failure evidence: exact codes/schema/IDs, unknowns, privacy, deterministic caps and fail-safe publication passed.'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($directory).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($directory) -match '^haby-failure-fixtures-[a-f0-9]{32}$') { Remove-Item -LiteralPath $directory -Recurse -Force }
}

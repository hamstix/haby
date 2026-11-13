param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$TrxPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

# Only these names belong to this exporter. Runner/resource reports and raw
# inputs are never staged, replaced or removed. The sidecar is published last.
$ownedNames = @('cases.json', 'counts.json', 'top-duration.csv', 'class-duration.csv', 'results.trx', 'failure-evidence.json')
$destination = $null; $stage = $null; $cleanupArmed = $false
function Clear-OwnedReports([bool]$Strict) {
    foreach ($name in $ownedNames) {
        $path = Join-Path $destination $name
        try {
            $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            if ($null -ne $item) {
                if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Output obstruction.' }
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            }
        } catch { if ($Strict) { throw 'Owned report could not be invalidated.' } }
    }
}
function New-FailureRecord($Result, [string]$Method, [string]$Case, [string]$Row, $Namespace) {
    $record = [ordered]@{
        method = $Method; case = $Case; row = $Row; classificationMode = 'unavailable'
        category = 'unknown'; assertionKind = 'unknown'; exceptionCategory = 'unknown'
        grpcCode = 'unavailable'; socketCode = 'unavailable'; phase = 'unavailable'; rootCause = 'unknown'
    }
    # ErrorInfo is untrusted text, never runtime exception/type/cause evidence.
    # No stack/stdout, substring matching, captures or inferred phases.
    $errors = @($Result.SelectNodes('t:Output/t:ErrorInfo', $Namespace))
    if ($errors.Count -eq 1) {
        $messages = @($errors[0].SelectNodes('t:Message', $Namespace))
        if ($messages.Count -eq 1 -and $messages[0].ChildNodes.Count -eq 1 -and
            $messages[0].FirstChild.NodeType -in @([Xml.XmlNodeType]::Text, [Xml.XmlNodeType]::CDATA)) {
            $text = $messages[0].FirstChild.Value
            # Check length before inspecting text; do not truncate into a signature.
            if ($text.Length -le 4096) {
                switch -CaseSensitive -Exact ($text) {
                    'Assert.Equal() Failure' { $record.category = 'assertion'; $record.assertionKind = 'equal' }
                    'Assert.True() Failure' { $record.category = 'assertion'; $record.assertionKind = 'true' }
                    'Assert.False() Failure' { $record.category = 'assertion'; $record.assertionKind = 'false' }
                    'Assert.Null() Failure' { $record.category = 'assertion'; $record.assertionKind = 'null' }
                    'Assert.NotNull() Failure' { $record.category = 'assertion'; $record.assertionKind = 'not-null' }
                    'System.TimeoutException : The operation has timed out.' { $record.category = 'timeout'; $record.exceptionCategory = 'timeout' }
                    'System.OperationCanceledException : The operation was canceled.' { $record.category = 'cancellation'; $record.exceptionCategory = 'cancellation' }
                    'System.Net.Sockets.SocketException (111): Connection refused' { $record.category = 'transport'; $record.exceptionCategory = 'socket'; $record.socketCode = 'connection-refused' }
                    'System.Net.Sockets.SocketException (10061): No connection could be made because the target machine actively refused it.' { $record.category = 'transport'; $record.exceptionCategory = 'socket'; $record.socketCode = 'connection-refused' }
                    'Grpc.Core.RpcException : Status(StatusCode="Unavailable", Detail="Service is unavailable.")' { $record.category = 'transport'; $record.exceptionCategory = 'grpc'; $record.grpcCode = 'unavailable' }
                    'Grpc.Core.RpcException : Status(StatusCode="DeadlineExceeded", Detail="Deadline exceeded.")' { $record.category = 'timeout'; $record.exceptionCategory = 'grpc'; $record.grpcCode = 'deadline-exceeded' }
                }
                if ($record.category -cne 'unknown') { $record.classificationMode = 'parsed-signature' }
            }
        }
    }
    return [pscustomobject]$record
}
try {
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($path in @($destination) + @($TrxPath | ForEach-Object { [IO.Path]::GetFullPath($_) })) {
        # Reject aliases before touching any output. This is an owned-file writer,
        # not an arbitrary symlink/reparse-point traversal utility.
        $current = $path
        while ($current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
            if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Aliased path.' }
            $current = [IO.Path]::GetDirectoryName($current)
        }
    }
    foreach ($inputTrx in $TrxPath) {
        $inputPath = [IO.Path]::GetFullPath($inputTrx)
        if ($inputPath.Equals($destination, $comparison) -or
            $inputPath.StartsWith($destination.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, $comparison)) { throw 'Input overlaps output.' }
    }
    $cleanupArmed = $true
    Clear-OwnedReports $true
    $failureRecords = [Collections.Generic.SortedDictionary[string, object]]::new([StringComparer]::Ordinal)
# Raw TRX can contain protected assertion messages, stdout and attachments.
# Reconstruct an allowlisted report; never copy raw XML nodes or parameter text.
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 67108864
$counterFields = @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')
$counterTotals = @{}
foreach ($field in $counterFields) { $counterTotals[$field] = 0 }
$cases = @(foreach ($inputTrx in $TrxPath) {
    $reader = [Xml.XmlReader]::Create((Resolve-Path -LiteralPath $inputTrx).Path, $settings)
    try { $document = [Xml.XmlDocument]::new(); $document.Load($reader) } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    if ($document.SelectNodes('/t:TestRun/t:TestDefinitions', $ns).Count -ne 1 -or
        $document.SelectNodes('/t:TestRun/t:Results', $ns).Count -ne 1 -or
        $document.SelectNodes('/t:TestRun/t:ResultSummary/t:Counters', $ns).Count -ne 1) { throw 'Invalid TRX structure.' }
    $definitions = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($definition in $document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns)) {
        $methods = @($definition.SelectNodes('t:TestMethod', $ns))
        if ($methods.Count -ne 1) { throw 'Missing/ambiguous method definition.' }
        $className = $methods[0].GetAttribute('className'); $methodName = $methods[0].GetAttribute('name')
        $id = $definition.GetAttribute('id'); $qualified = $className + '.' + $methodName
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrEmpty($className) -or
            [string]::IsNullOrEmpty($methodName) -or $qualified -cnotmatch '^[A-Za-z0-9_.+`]+$') { throw 'Unsafe method identity.' }
        if ($definitions.ContainsKey($id)) { throw 'Duplicate test definition.' }
        $definitions.Add($id, $qualified)
    }
    $results = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    $counters = $document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
    if ($null -eq $counters -or [int]$counters.total -ne $results.Count -or $results.Count -eq 0) { throw 'TRX inventory does not match counters.' }
    foreach ($field in $counterFields) { $counterTotals[$field] += [int]$counters.GetAttribute($field) }
    foreach ($result in $results) {
        $testId = $result.GetAttribute('testId'); $testName = $result.GetAttribute('testName')
        if ([string]::IsNullOrWhiteSpace($testId) -or [string]::IsNullOrWhiteSpace($testName) -or
            -not $definitions.ContainsKey($testId)) { throw 'Missing test identity/definition.' }
        $method = $definitions[$testId]
        $identity = $method + "`n" + $testName
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
        $rowHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hash + "`n" + $testId))).ToLowerInvariant()
        if ($result.outcome -cnotin @('Passed', 'Failed', 'NotExecuted')) { throw 'Unexpected test outcome.' }
        if ($result.outcome -ceq 'Failed' -and $method.Length -le 512) {
            $key = $method + '#' + $hash + '#' + $rowHash
            # Retain at most 1024 diagnostic candidates, in canonical ordinal
            # order regardless of input order. No unbounded per-failure map.
            if ($failureRecords.Count -eq 1024) {
                $last = @($failureRecords.Keys)[-1]
                if ([StringComparer]::Ordinal.Compare($key, $last) -lt 0) { $failureRecords.Remove($last) | Out-Null }
            }
            if ($failureRecords.Count -lt 1024 -and -not $failureRecords.ContainsKey($key)) {
                $failureRecords.Add($key, (New-FailureRecord $result $method $hash $rowHash $ns))
            }
        }
        [pscustomobject]@{
            method = $method; case = $hash; row = $rowHash; outcome = $result.outcome
            seconds = [TimeSpan]::Parse($result.duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds
            start = [DateTimeOffset]::Parse($result.startTime).ToUniversalTime().ToString('O')
            end = [DateTimeOffset]::Parse($result.endTime).ToUniversalTime().ToString('O')
        }
    }
})
$cases = @($cases | Sort-Object method, case, row)
if ($cases.Count -eq 0) { throw 'No executed cases in TRX.' }
if (@($cases | Group-Object row | Where-Object Count -ne 1).Count) { throw 'Duplicate case execution.' }
New-Item -ItemType Directory -Force $destination | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ('haby-safe-export-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
$OutputDirectory = $stage
$records = [Collections.Generic.List[object]]::new()
foreach ($record in $failureRecords.Values) { $records.Add($record) }
$failed = @($cases | Where-Object outcome -CEQ 'Failed').Count
do {
    $omitted = $failed - $records.Count
    $evidence = [ordered]@{
        schemaVersion = 1; failedCases = $failed; emittedRecords = $records.Count; omittedRecords = $omitted; truncated = $omitted -gt 0
        maxRecords = 1024; maxBytes = 1048576; maxMethodCharacters = 512; maxMessageCharacters = 4096; records = @($records.ToArray())
    }
    $evidenceJson = (ConvertTo-Json -InputObject $evidence -Depth 5) + "`n"
    $fits = [Text.Encoding]::UTF8.GetByteCount($evidenceJson) -le 1048576
    if (-not $fits) { $records.RemoveAt($records.Count - 1) }
} until ($fits)
[IO.File]::WriteAllText((Join-Path $stage 'failure-evidence.json'), $evidenceJson, [Text.UTF8Encoding]::new($false))
$cases | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'cases.json') -Encoding utf8
$cases | Sort-Object seconds -Descending | Select-Object -First 20 method, case, row, outcome, @{n='seconds';e={$_.seconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)}}, start, end |
    Export-Csv (Join-Path $OutputDirectory 'top-duration.csv') -NoTypeInformation -Encoding utf8
$cases | Group-Object { $_.method.Substring(0, $_.method.LastIndexOf('.')) } | ForEach-Object {
    $starts = @($_.Group | ForEach-Object { [DateTimeOffset]::Parse($_.start) } | Sort-Object)
    $ends = @($_.Group | ForEach-Object { [DateTimeOffset]::Parse($_.end) } | Sort-Object)
    [pscustomobject]@{ collectionClass = $_.Name; cases = $_.Count; testSeconds = ($_.Group | Measure-Object seconds -Sum).Sum; spanSeconds = ($ends[-1] - $starts[0]).TotalSeconds }
} | Sort-Object spanSeconds -Descending | Select-Object collectionClass, cases, @{n='testSeconds';e={$_.testSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)}}, @{n='spanSeconds';e={$_.spanSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)}} |
    Export-Csv (Join-Path $OutputDirectory 'class-duration.csv') -NoTypeInformation -Encoding utf8

# A minimal TRX retains per-case outcome/timing, with hashed parameter identities.
$xmlSettings = [Xml.XmlWriterSettings]::new(); $xmlSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create((Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'results.trx'), $xmlSettings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('TestRun', $ns.LookupNamespace('t'))
    $writer.WriteAttributeString('id', [Guid]::NewGuid().ToString())
    $writer.WriteAttributeString('name', 'Allowlisted Haby measurement')
    $writer.WriteStartElement('Results')
    foreach ($case in $cases) {
        $id = [Guid]::ParseExact($case.row.Substring(0, 32), 'N').ToString()
        $writer.WriteStartElement('UnitTestResult')
        foreach ($pair in @{
            testId = $id; executionId = $id; testName = $case.method + '#' + $case.case + '#' + $case.row; computerName = 'withheld'
            testType = '13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b'; testListId = '8c43106b-9dc1-4907-a29f-aa66a61bf5b6'
            outcome = $case.outcome; duration = [TimeSpan]::FromSeconds($case.seconds).ToString('c')
            startTime = $case.start; endTime = $case.end
        }.GetEnumerator()) { $writer.WriteAttributeString($pair.Key, [string]$pair.Value) }
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('TestDefinitions')
    foreach ($case in $cases) {
        $id = [Guid]::ParseExact($case.row.Substring(0, 32), 'N').ToString()
        $writer.WriteStartElement('UnitTest'); $writer.WriteAttributeString('id', $id)
        $writer.WriteAttributeString('name', $case.method + '#' + $case.case + '#' + $case.row)
        $writer.WriteStartElement('Execution'); $writer.WriteAttributeString('id', $id); $writer.WriteEndElement()
        $writer.WriteStartElement('TestMethod')
        $split = $case.method.LastIndexOf('.')
        $writer.WriteAttributeString('className', $case.method.Substring(0, $split))
        $writer.WriteAttributeString('name', $case.method.Substring($split + 1)); $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('TestEntries')
    foreach ($case in $cases) {
        $id = [Guid]::ParseExact($case.row.Substring(0, 32), 'N').ToString()
        $writer.WriteStartElement('TestEntry')
        $writer.WriteAttributeString('testId', $id); $writer.WriteAttributeString('executionId', $id)
        $writer.WriteAttributeString('testListId', '8c43106b-9dc1-4907-a29f-aa66a61bf5b6'); $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('TestLists'); $writer.WriteStartElement('TestList')
    $writer.WriteAttributeString('name', 'Results Not in a List'); $writer.WriteAttributeString('id', '8c43106b-9dc1-4907-a29f-aa66a61bf5b6')
    $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteStartElement('ResultSummary')
    $writer.WriteAttributeString('outcome', $(if (@($cases | Where-Object outcome -ne Passed).Count) { 'Failed' } else { 'Completed' }))
    $writer.WriteStartElement('Counters')
    foreach ($field in $counterFields) {
        $writer.WriteAttributeString($field, [string][int]$counterTotals[$field])
    }
    $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteEndElement(); $writer.WriteEndDocument()
} finally { $writer.Dispose() }
[pscustomobject]@{ total = $cases.Count; passed = @($cases | Where-Object outcome -eq Passed).Count; failed = @($cases | Where-Object outcome -eq Failed).Count; skipped = @($cases | Where-Object outcome -eq NotExecuted).Count } |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'counts.json') -Encoding utf8
foreach ($name in $ownedNames) { Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $destination $name) -Force }
} catch {
    if ($cleanupArmed) { Clear-OwnedReports $false }
    throw 'Safe timing export failed; report withheld.'
} finally {
    try { if ($stage -and (Test-Path -LiteralPath $stage)) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $ownedStage = [IO.Path]::GetFullPath($stage)
        if ($ownedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($ownedStage) -match '^haby-safe-export-[a-f0-9]{32}$') { Remove-Item -LiteralPath $ownedStage -Recurse -Force }
    } } catch {
        if ($cleanupArmed) { Clear-OwnedReports $false }
        throw 'Safe timing export failed; report withheld.'
    }
}

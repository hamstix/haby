param(
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

# Only these exporter-owned names may be replaced or removed on failure.
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$ownedNames = @('cases.json', 'counts.json', 'top-duration.csv', 'class-duration.csv', 'results.trx', 'failure-evidence.json')
$stage = $null
try {
    foreach ($name in $ownedNames) {
        $path = Join-Path $destination $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
# Raw TRX can contain protected assertion messages, stdout and attachments.
# Reconstruct an allowlisted report; never copy raw XML nodes or parameter text.
$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$settings.MaxCharactersInDocument = 67108864
$reader = [Xml.XmlReader]::Create((Resolve-Path $TrxPath).Path, $settings)
try { $document = [Xml.XmlDocument]::new(); $document.Load($reader) } finally { $reader.Dispose() }
$ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$definitions = @{}
foreach ($definition in $document.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
    $method = $definition.TestMethod
    $qualified = $method.className + '.' + $method.name
    if ($qualified -notmatch '^[A-Za-z0-9_.+`]+$') { throw 'Unsafe test method identity; report withheld.' }
    if ($definitions.ContainsKey([string]$definition.id)) { throw 'Duplicate test definition.' }
    $definitions[[string]$definition.id] = $qualified
}
$failureRecords = @{}
$cases = @($document.SelectNodes('//t:Results/t:UnitTestResult', $ns) | ForEach-Object {
    if (-not $definitions.ContainsKey($_.testId)) { throw 'Missing test definition.' }
    $method = $definitions[$_.testId]
    $identity = $method + "`n" + $_.testName
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    if ($_.outcome -notin @('Passed', 'Failed', 'NotExecuted')) { throw 'Unexpected test outcome.' }
    if ($_.outcome -eq 'Failed') {
        # ErrorInfo is untrusted text, never runtime exception metadata. Match only
        # an entire curated Message; never parse stack/stdout or copy captures.
        $record = [ordered]@{
            method = $method; case = $hash; classificationMode = 'unavailable'
            category = 'unknown'; assertionKind = 'unknown'; exceptionCategory = 'unknown'
            grpcCode = 'unavailable'; socketCode = 'unavailable'
            phase = 'unavailable'; rootCause = 'unknown'
        }
        $errors = @($_.SelectNodes('t:Output/t:ErrorInfo', $ns))
        if ($errors.Count -eq 1) {
            $messages = @($errors[0].SelectNodes('t:Message', $ns))
            if ($messages.Count -eq 1 -and $messages[0].ChildNodes.Count -eq 1 -and
                $messages[0].FirstChild.NodeType -in @([Xml.XmlNodeType]::Text, [Xml.XmlNodeType]::CDATA)) {
                $message = $messages[0].InnerText
                if ($message.Length -le 4096) {
                    # Ordinal, whole-message comparison deliberately refuses embedded,
                    # conflicting, localized, multiline and overlong signatures.
                    switch -CaseSensitive -Exact ($message) {
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
                    if ($record.category -ne 'unknown') { $record.classificationMode = 'parsed-signature' }
                }
            }
        }
        $failureRecords[$hash] = [pscustomobject]$record
    }

    [pscustomobject]@{
        method = $method; case = $hash; outcome = $_.outcome
        seconds = [TimeSpan]::Parse($_.duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds
        start = [DateTimeOffset]::Parse($_.startTime).ToUniversalTime().ToString('O')
        end = [DateTimeOffset]::Parse($_.endTime).ToUniversalTime().ToString('O')
    }
} | Sort-Object method, case)
if ($cases.Count -eq 0) { throw 'No executed cases in TRX.' }
if (@($cases | Group-Object case | Where-Object Count -ne 1).Count) { throw 'Duplicate case execution.' }
$counters = $document.SelectSingleNode('//t:ResultSummary/t:Counters', $ns)
if ([int]$counters.total -ne $cases.Count) { throw 'TRX inventory does not match counters.' }
# Stage the complete report away from the public destination. The sidecar is
# published last, and removed with the canonical files if any publication fails.
New-Item -ItemType Directory -Force $destination | Out-Null
$stage = Join-Path ([IO.Path]::GetTempPath()) ('haby-safe-export-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
$OutputDirectory = $stage
$records = [Collections.Generic.List[object]]::new()
$omitted = 0
$failed = @($cases | Where-Object outcome -eq Failed)
foreach ($case in $failed) {
    # Canonical IDs remain unchanged and complete. Oversize method IDs are
    # omitted only from the bounded diagnostic sidecar, with explicit accounting.
    if ($records.Count -ge 1024 -or $case.method.Length -gt 512) { $omitted++; continue }
    $records.Add($failureRecords[$case.case])
}
$evidence = [ordered]@{
    schemaVersion = 1; failedCases = $failed.Count; emittedRecords = $records.Count
    omittedRecords = $omitted; truncated = $omitted -gt 0
    maxRecords = 1024; maxBytes = 1048576; maxMethodCharacters = 512; maxMessageCharacters = 4096
    records = @($records.ToArray())
}
$evidenceJson = ConvertTo-Json -InputObject $evidence -Depth 5
if ([Text.Encoding]::UTF8.GetByteCount($evidenceJson) + 1 -gt 1048576) { throw 'Failure evidence bound exceeded.' }
[IO.File]::WriteAllText((Join-Path $stage 'failure-evidence.json'), $evidenceJson + "`n", [Text.UTF8Encoding]::new($false))
$cases | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'cases.json') -Encoding utf8
$cases | Sort-Object seconds -Descending | Select-Object -First 20 method, case, outcome, @{n='seconds';e={$_.seconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture)}}, start, end |
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
    $writer.WriteAttributeString('name', 'Allowlisted PostgreSQL measurement')
    $writer.WriteStartElement('Results')
    foreach ($case in $cases) {
        $id = [Guid]::ParseExact($case.case.Substring(0, 32), 'N').ToString()
        $writer.WriteStartElement('UnitTestResult')
        foreach ($pair in @{
            testId = $id; executionId = $id; testName = $case.method + '#' + $case.case; computerName = 'withheld'
            testType = '13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b'; testListId = '8c43106b-9dc1-4907-a29f-aa66a61bf5b6'
            outcome = $case.outcome; duration = [TimeSpan]::FromSeconds($case.seconds).ToString('c')
            startTime = $case.start; endTime = $case.end
        }.GetEnumerator()) { $writer.WriteAttributeString($pair.Key, [string]$pair.Value) }
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('TestDefinitions')
    foreach ($case in $cases) {
        $id = [Guid]::ParseExact($case.case.Substring(0, 32), 'N').ToString()
        $writer.WriteStartElement('UnitTest'); $writer.WriteAttributeString('id', $id)
        $writer.WriteAttributeString('name', $case.method + '#' + $case.case)
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
        $id = [Guid]::ParseExact($case.case.Substring(0, 32), 'N').ToString()
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
    foreach ($field in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending')) {
        $writer.WriteAttributeString($field, [string][int]$counters.GetAttribute($field))
    }
    $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteEndElement(); $writer.WriteEndDocument()
} finally { $writer.Dispose() }
[pscustomobject]@{ total = $cases.Count; passed = @($cases | Where-Object outcome -eq Passed).Count; failed = @($cases | Where-Object outcome -eq Failed).Count; skipped = @($cases | Where-Object outcome -eq NotExecuted).Count } |
    ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'counts.json') -Encoding utf8

foreach ($name in $ownedNames) { Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $destination $name) -Force }
} catch {
    # Suppress raw parser/conversion/path errors even for direct invocation.
    foreach ($name in $ownedNames) {
        $path = Join-Path $destination $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }
    throw 'Safe timing export failed; report withheld.'
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

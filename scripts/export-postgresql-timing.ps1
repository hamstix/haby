param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$TrxPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

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
    $definitions = @{}
    foreach ($definition in $document.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $definition.TestMethod
        $qualified = $method.className + '.' + $method.name
        if ($qualified -notmatch '^[A-Za-z0-9_.+`]+$') { throw 'Unsafe test method identity; report withheld.' }
        $definitions[$definition.id] = $qualified
    }
    $results = @($document.SelectNodes('//t:Results/t:UnitTestResult', $ns))
    $counters = $document.SelectSingleNode('//t:ResultSummary/t:Counters', $ns)
    if ($null -eq $counters -or [int]$counters.total -ne $results.Count -or $results.Count -eq 0) { throw 'TRX inventory does not match counters.' }
    foreach ($field in $counterFields) { $counterTotals[$field] += [int]$counters.GetAttribute($field) }
    foreach ($result in $results) {
        if (-not $definitions.ContainsKey($result.testId)) { throw 'Missing test definition.' }
        $method = $definitions[$result.testId]
        $identity = $method + "`n" + $result.testName
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
        $rowHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hash + "`n" + $result.testId))).ToLowerInvariant()
        if ($result.outcome -notin @('Passed', 'Failed', 'NotExecuted')) { throw 'Unexpected test outcome.' }
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
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
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

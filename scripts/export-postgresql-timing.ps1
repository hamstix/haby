param(
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

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
    $definitions[$definition.id] = $qualified
}
$cases = @($document.SelectNodes('//t:Results/t:UnitTestResult', $ns) | ForEach-Object {
    if (-not $definitions.ContainsKey($_.testId)) { throw 'Missing test definition.' }
    $method = $definitions[$_.testId]
    $identity = $method + "`n" + $_.testName
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    if ($_.outcome -notin @('Passed', 'Failed', 'NotExecuted')) { throw 'Unexpected test outcome.' }
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
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$cases | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'cases.json') -Encoding utf8
$cases | Sort-Object seconds -Descending | Select-Object -First 20 | Export-Csv (Join-Path $OutputDirectory 'top-duration.csv') -NoTypeInformation -Encoding utf8
$cases | Group-Object { $_.method.Substring(0, $_.method.LastIndexOf('.')) } | ForEach-Object {
    $starts = @($_.Group | ForEach-Object { [DateTimeOffset]::Parse($_.start) } | Sort-Object)
    $ends = @($_.Group | ForEach-Object { [DateTimeOffset]::Parse($_.end) } | Sort-Object)
    [pscustomobject]@{ collectionClass = $_.Name; cases = $_.Count; testSeconds = ($_.Group | Measure-Object seconds -Sum).Sum; spanSeconds = ($ends[-1] - $starts[0]).TotalSeconds }
} | Sort-Object spanSeconds -Descending | Export-Csv (Join-Path $OutputDirectory 'class-duration.csv') -NoTypeInformation -Encoding utf8

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
        $writer.WriteStartElement('UnitTestResult')
        foreach ($pair in @{
            testId = $case.case; executionId = $case.case; testName = $case.method + '#' + $case.case
            testType = '13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b'; testListId = '8c43106b-9dc1-4907-a29f-aa66a61bf5b6'
            outcome = $case.outcome; duration = [TimeSpan]::FromSeconds($case.seconds).ToString('c')
            startTime = $case.start; endTime = $case.end
        }.GetEnumerator()) { $writer.WriteAttributeString($pair.Key, [string]$pair.Value) }
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteStartElement('TestDefinitions')
    foreach ($case in $cases) {
        $writer.WriteStartElement('UnitTest'); $writer.WriteAttributeString('id', $case.case)
        $writer.WriteAttributeString('name', $case.method + '#' + $case.case)
        $writer.WriteStartElement('Execution'); $writer.WriteAttributeString('id', $case.case); $writer.WriteEndElement()
        $writer.WriteStartElement('TestMethod')
        $split = $case.method.LastIndexOf('.')
        $writer.WriteAttributeString('className', $case.method.Substring(0, $split))
        $writer.WriteAttributeString('name', $case.method.Substring($split + 1)); $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
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

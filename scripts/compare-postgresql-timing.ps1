param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After, [string[]]$AdditionalMethods = @())
$ErrorActionPreference = 'Stop'
$baseline = @(Get-Content (Join-Path $Before 'cases.json') -Raw | ConvertFrom-Json)
$candidate = @(Get-Content (Join-Path $After 'cases.json') -Raw | ConvertFrom-Json)
$added = @($candidate | Where-Object method -in $AdditionalMethods)
if (@(Compare-Object @($AdditionalMethods | Sort-Object) @($added.method | Sort-Object)).Count) { throw 'Declared additional tests were not each executed exactly once.' }
$existing = @($candidate | Where-Object method -notin $AdditionalMethods)
$difference = @(Compare-Object @($baseline.case | Sort-Object) @($existing.case | Sort-Object))
if ($difference.Count) { throw 'Case inventories differ; no comparable full-suite result.' }
foreach ($run in @($baseline, $candidate)) {
    if (@($run | Where-Object outcome -ne Passed).Count) { throw 'A run contains failed or skipped cases; no successful speedup claim.' }
}
foreach ($directory in @($Before, $After)) {
    $measurement = Get-Content (Join-Path $directory 'measurement.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{ source = $measurement.source; existingCases = $baseline.Count; additionalCases = $added.Count; wallSeconds = $measurement.wallSeconds; phases = $measurement.phases }
}

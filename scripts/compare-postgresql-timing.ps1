param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
$ErrorActionPreference = 'Stop'
$baseline = @(Get-Content (Join-Path $Before 'cases.json') -Raw | ConvertFrom-Json)
$candidate = @(Get-Content (Join-Path $After 'cases.json') -Raw | ConvertFrom-Json)
$difference = @(Compare-Object @($baseline.case | Sort-Object) @($candidate.case | Sort-Object))
if ($difference.Count) { throw 'Case inventories differ; no comparable full-suite result.' }
foreach ($run in @($baseline, $candidate)) {
    if (@($run | Where-Object outcome -ne Passed).Count) { throw 'A run contains failed or skipped cases; no successful speedup claim.' }
}
foreach ($directory in @($Before, $After)) {
    $measurement = Get-Content (Join-Path $directory 'measurement.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{ source = $measurement.source; cases = $baseline.Count; wallSeconds = $measurement.wallSeconds; phases = $measurement.phases }
}

function Get-HabyPostgreSqlSuites([string]$Selection) {
    switch -CaseSensitive ($Selection) {
        'full' { return @('functional', 'infrastructure-qualification') }
        'functional' { return @('functional') }
        'infrastructure-qualification' { return @('infrastructure-qualification') }
        default { throw 'Suite must be full, functional or infrastructure-qualification; empty selection is invalid.' }
    }
}

function Test-HabyFocusedPostgreSqlArguments([string]$TestCase, [string]$TestGroup) {
    if ($TestCase -and $TestGroup) { throw 'Select one PostgreSQL testcase or group, not both.' }
    foreach ($value in @($TestCase, $TestGroup)) {
        if ($value -and $value -cnotmatch '^Haby\.PostgreSql\.IntegrationTests\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?$') {
            throw 'Focused selection requires an exact fully qualified method or class name.'
        }
    }
    return [bool]($TestCase -or $TestGroup)
}

function Get-HabyFocusedPostgreSqlSelection([object[]]$Cases, [string]$TestCase, [string]$TestGroup) {
    if (-not (Test-HabyFocusedPostgreSqlArguments $TestCase $TestGroup)) { return $null }
    $selected = @($Cases | Where-Object {
        if ($TestCase) { $_.method -ceq $TestCase }
        else { $_.method.Substring(0, $_.method.LastIndexOf('.')) -ceq $TestGroup }
    })
    if ($selected.Count -eq 0) { throw 'Focused selection matched zero discovered cases.' }
    $methods = @($selected.method | Sort-Object -Unique)
    return [pscustomobject]@{
        cases = $selected
        suites = @($selected.suite | Sort-Object -Unique)
        filter = ($methods | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
    }
}

# Static current-plan admission precedes owned runtime setup; live discovery and
# executed theory identities are independently reconciled later.
function Assert-HabyFocusedPostgreSqlPlan([string]$PlanPath, [string]$TestCase, [string]$TestGroup) {
    if (-not (Test-HabyFocusedPostgreSqlArguments $TestCase $TestGroup)) { return }
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $rows = @($plan.entries | Where-Object { $_.gate -ceq 'postgresql-integration' })
    try { $null = Get-HabyFocusedPostgreSqlSelection $rows $TestCase $TestGroup }
    catch { throw 'Focused selector absent from current coverage plan.' }
}

function Assert-HabyFocusedGatePlan([object[]]$Cases, [string]$PlanPath, [string]$TestCase, [string]$TestGroup, [string[]]$Suites) {
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $planned = @($plan.entries | Where-Object { $_.gate -ceq 'postgresql-integration' -and $_.suite -cin $Suites })
    $selection = Get-HabyFocusedPostgreSqlSelection $planned $TestCase $TestGroup
    Assert-HabyInventory $selection.cases $Cases
}

function Get-HabySuiteFilter([object[]]$Cases, [string]$Suite) {
    $selected = @($Cases | Where-Object suite -CEQ $Suite)
    if ($selected.Count -eq 0) { throw 'Selected suite has no discovered cases.' }
    if ($Suite -ceq 'functional') {
        $qualification = @($Cases | Where-Object suite -CEQ 'infrastructure-qualification')
        if ($qualification.Count -eq 0) { throw 'Qualification partition is missing.' }
        $classes = @($qualification | ForEach-Object { $_.method.Substring(0, $_.method.LastIndexOf('.')) } | Sort-Object -Unique)
        # Include all other tests, including a mistakenly uncategorized new test:
        # execution accounting must reject it rather than silently omit it.
        return ($classes | ForEach-Object { 'FullyQualifiedName!~' + $_ + '.' }) -join '&'
    }
    $classes = @($selected | ForEach-Object { $_.method.Substring(0, $_.method.LastIndexOf('.')) } | Sort-Object -Unique)
    # Deduplicate class predicates only, never case identities/theory rows.
    return ($classes | ForEach-Object { 'FullyQualifiedName~' + $_ + '.' }) -join '|'
}

function Assert-HabyInventory([object[]]$Expected, [object[]]$Actual) {
    $left = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $right = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($row in $Expected) {
        $key = $row.method + '#' + $row.case
        if (-not $left.ContainsKey($key)) { $left[$key] = 0 }; $left[$key]++
    }
    foreach ($row in $Actual) {
        $key = $row.method + '#' + $row.case
        if (-not $right.ContainsKey($key)) { $right[$key] = 0 }; $right[$key]++
    }
    if ($Expected.Count -eq 0 -or $Expected.Count -ne $Actual.Count -or $left.Count -ne $right.Count) { throw 'Suite inventory count mismatch.' }
    foreach ($key in $left.Keys) {
        if (-not $right.ContainsKey($key) -or $right[$key] -ne $left[$key]) { throw 'Suite identity/parameter multiplicity mismatch.' }
    }
}

function Assert-HabyGatePlan([object[]]$Cases, [string]$PlanPath, [string]$Gate, [string[]]$Suites) {
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $expected = @($plan.entries | Where-Object { $_.gate -ceq $Gate -and $_.suite -cin $Suites })
    Assert-HabyInventory $expected $Cases
}

function Assert-HabyResourceCounts($Resources, [int]$Limit, [int]$HostLimit) {
    if ($null -eq $Resources -or $Resources.limit -ne $Limit -or $Resources.active -ne 0 -or
        $Resources.cleanupFailed -ne 0 -or $Resources.peakReservedServers -gt $Limit -or
        $Resources.readyActive -ne 0 -or $Resources.peakReadyServers -gt $Limit -or
        $Resources.startupAttempts -ne $Resources.cleaned -or
        $Resources.catalogDatabasesCreated -ne $Resources.catalogDatabasesDropped -or
        $Resources.hostGroupLimit -ne $HostLimit -or $Resources.hostGroupsActive -ne 0 -or
        $Resources.peakHostGroups -gt $HostLimit) { throw 'Owned resource accounting incomplete.' }
}

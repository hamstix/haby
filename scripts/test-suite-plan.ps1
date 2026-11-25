function Get-HabyPostgreSqlSuites([string]$Selection) {
    switch -CaseSensitive ($Selection) {
        'full' { return @('functional', 'infrastructure-qualification') }
        'functional' { return @('functional') }
        'infrastructure-qualification' { return @('infrastructure-qualification') }
        default { throw 'Suite must be full, functional or infrastructure-qualification; empty selection is invalid.' }
    }
}

function Test-HabyFocusedPostgreSqlArguments([string]$TestCase, [string]$TestGroup, [string]$SelectionPath = '') {
    if ($SelectionPath) {
        if ($TestCase -or $TestGroup) { throw 'Union selection cannot be combined with author filters.' }
        $selection = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
        if ($selection.version -ne 1 -or $selection.scope -cne 'targeted' -or @($selection.cases).Count -lt 2) { throw 'Invalid targeted selection record.' }
        $fixed = @(
            'Haby.PostgreSql.IntegrationTests.ManifestCatalogPersistenceTests.Migration_and_fresh_provider_preserve_complete_typed_revision_and_original_history#47a4427250a45570b86cc662504bee1709141be4c30be469aaafb3333f96a90f',
            'Haby.PostgreSql.IntegrationTests.OperatorHostIntegrationTests.Explicit_execution_uses_production_singleton_and_pinned_inputs_and_survives_restart#4d3138b78192e0daf927d9bfac70b8669e7960c53f66fea13e7544293b6b9b4d')
        foreach ($key in $fixed) { if (@($selection.cases | Where-Object { ($_.method + '#' + $_.case) -ceq $key }).Count -ne 1) { throw 'Fixed smoke missing, duplicated or changed.' } }
        return $true
    }
    if ($TestCase -and $TestGroup) { throw 'Select one PostgreSQL testcase or group, not both.' }
    foreach ($value in @($TestCase, $TestGroup)) {
        if ($value -and $value -cnotmatch '^Haby\.PostgreSql\.IntegrationTests\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?$') {
            throw 'Focused selection requires an exact fully qualified method or class name.'
        }
    }
    return [bool]($TestCase -or $TestGroup)
}

function Get-HabyFocusedPostgreSqlSelection([object[]]$Cases, [string]$TestCase, [string]$TestGroup, [string]$SelectionPath = '') {
    if (-not (Test-HabyFocusedPostgreSqlArguments $TestCase $TestGroup $SelectionPath)) { return $null }
    if ($SelectionPath) {
        $selection = Get-Content -LiteralPath $SelectionPath -Raw | ConvertFrom-Json
        $wanted = @($selection.cases)
        $selected = @($Cases | Where-Object { $row = $_; @($wanted | Where-Object { $_.method -ceq $row.method -and $_.case -ceq $row.case }).Count -eq 1 })
        Assert-HabyInventory $wanted $selected
        $methods = @($selected.method | Sort-Object -Unique)
        # A method filter executes every theory row: partial theory selection is forbidden.
        Assert-HabyInventory $selected @($Cases | Where-Object { $_.method -cin $methods })
        return [pscustomobject]@{ cases=$selected; suites=@($selected.suite | Sort-Object -Unique); filter=(($methods | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|') }
    }
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
function Assert-HabyFocusedPostgreSqlPlan([string]$PlanPath, [string]$TestCase, [string]$TestGroup, [string]$SelectionPath = '') {
    if (-not (Test-HabyFocusedPostgreSqlArguments $TestCase $TestGroup $SelectionPath)) { return }
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $rows = @($plan.entries | Where-Object { $_.gate -ceq 'postgresql-integration' })
    try { $null = Get-HabyFocusedPostgreSqlSelection $rows $TestCase $TestGroup $SelectionPath }
    catch { throw 'Focused selector absent from current coverage plan.' }
}

function Assert-HabyFocusedGatePlan([object[]]$Cases, [string]$PlanPath, [string]$TestCase, [string]$TestGroup, [string[]]$Suites, [string]$SelectionPath = '') {
    $plan = Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $planned = @($plan.entries | Where-Object { $_.gate -ceq 'postgresql-integration' -and $_.suite -cin $Suites })
    $selection = Get-HabyFocusedPostgreSqlSelection $planned $TestCase $TestGroup $SelectionPath
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

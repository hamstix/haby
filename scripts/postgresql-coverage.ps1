function Get-PostgreSqlCoverageCases([string]$DiscoveryPath, [string]$MapPath) {
    $map = Get-Content -LiteralPath $MapPath -Raw | ConvertFrom-Json -AsHashtable
    $migrationPath = Join-Path (Split-Path $MapPath -Parent) 'case-migrations.json'
    $migrations = if (Test-Path -LiteralPath $migrationPath) { Get-Content -LiteralPath $migrationPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
    $cases = [Collections.Generic.List[object]]::new()
    foreach ($line in [IO.File]::ReadAllLines($DiscoveryPath)) {
        $display = $line.Trim()
        if (-not $display.StartsWith('Haby.PostgreSql.IntegrationTests.', [StringComparison]::Ordinal)) {
            if ($display -match '^[A-Za-z_][A-Za-z0-9_.+`]*\.[A-Za-z_][A-Za-z0-9_`]*(\(|$)') { throw 'Discovered test outside mapped namespace; report withheld.' }
            continue
        }
        $method = $display.Split('(', 2)[0]
        if ($method -notmatch '^[A-Za-z0-9_.+]+$') { throw 'Unsafe discovery method; report withheld.' }
        $split = $method.LastIndexOf('.')
        $class = $method.Substring(0, $split).Split('.')[-1]
        if (-not $map.classes.ContainsKey($class)) { throw 'Unmapped integration class; update coverage-map.json.' }
        $entry = $map.classes[$class]
        if (@($entry.responsibilities).Count -eq 0) { throw 'Missing responsibility map.' }
        foreach ($role in $entry.responsibilities) {
            if ($role -cnotin @('Fast', 'Persistence', 'Provider integration', 'Operator/E2E', 'Test infrastructure')) { throw 'Invalid responsibility map.' }
        }
        if ($entry.isolation -cnotin @('none', 'case-database-group-server', 'class-owned-server', 'class-owned-server-exclusive-login', 'class-owned-server-exclusive-provider-alias', 'exclusive-owned-infrastructure') -or
            $entry.gate -cne 'postgresql-integration') { throw 'Invalid isolation/gate map.' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($method + [char]10 + $display))).ToLowerInvariant()
        $baselineMethod = if ($migrations.ContainsKey($method)) { $migrations[$method] } else { $method }
        $baselineDisplay = $baselineMethod + $display.Substring($method.Length)
        $baselineCase = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($baselineMethod + [char]10 + $baselineDisplay))).ToLowerInvariant()
        # Keep every row, including repeated identities; never deduplicate theory rows.
        if ($entry.suite -cnotin @('functional', 'infrastructure-qualification')) { throw 'Missing/invalid suite assignment.' }
        $cases.Add([pscustomobject]@{ method = $method; case = $hash; baselineMethod = $baselineMethod; baselineCase = $baselineCase; suite = $entry.suite
            responsibilities = $entry.responsibilities; isolation = $entry.isolation; requirements = $entry.requirements; gate = $entry.gate })
    }
    if ($cases.Count -eq 0) { throw 'No discovered integration cases.' }
    return $cases.ToArray()
}

function Assert-PostgreSqlBaselineCoverage([object[]]$Cases, [string]$BaselinePath) {
    $expected = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $actual = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($key in (Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json).cases) {
        if (-not $expected.ContainsKey($key)) { $expected[$key] = 0 }; $expected[$key]++
    }
    foreach ($row in $Cases) {
        $key = $row.baselineMethod + '#' + $row.baselineCase
        if (-not $actual.ContainsKey($key)) { $actual[$key] = 0 }; $actual[$key]++
    }
    foreach ($key in $expected.Keys) {
        if (-not $actual.ContainsKey($key) -or $actual[$key] -ne $expected[$key]) { throw 'Baseline case/parameter migration mismatch.' }
    }
}

function Assert-PostgreSqlCoverage([object[]]$Discovered, [object[]]$Executed) {
    $expected = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $actual = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    foreach ($row in $Discovered) {
        $key = $row.method + '#' + $row.case
        if (-not $expected.ContainsKey($key)) { $expected[$key] = 0 }; $expected[$key]++
    }
    foreach ($row in $Executed) {
        if ($row.outcome -cnotin @('Passed', 'Failed')) { throw 'Skipped/unexecuted integration case; coverage incomplete.' }
        $key = $row.method + '#' + $row.case
        if (-not $actual.ContainsKey($key)) { $actual[$key] = 0 }; $actual[$key]++
    }
    if ($Discovered.Count -ne $Executed.Count -or $expected.Count -ne $actual.Count) { throw 'Discovery/execution inventory mismatch.' }
    foreach ($key in $expected.Keys) {
        if (-not $actual.ContainsKey($key) -or $actual[$key] -ne $expected[$key]) { throw 'Discovery/execution multiplicity mismatch.' }
    }
}

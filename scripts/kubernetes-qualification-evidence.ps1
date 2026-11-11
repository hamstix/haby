# Sanitized evidence is accepted only for this invocation and its intended path.
function Assert-QualificationScenario {
    param([string]$Path,[string]$EvidenceId,[string]$Scenario,[bool]$Caught,[bool]$ListenerArranged)
    if($Caught -ne ($Scenario -ne 'success')){throw 'Unexpected outcome'}
    $lines=@(Get-Content -LiteralPath $Path)
    if(@($lines | Where-Object {$_ -eq "Evidence ID: $EvidenceId"}).Count -ne 1 -or @($lines | Where-Object {$_ -match '^Run ID: haby179-[a-f0-9]{12}$'}).Count -ne 1){throw 'Missing or stale invocation identity'}
    $stages=@($lines | Where-Object {$_ -like 'Stage: *'})
    $sequence=($stages -join '|') -replace '(Stage: readiness-probe)(\|Stage: readiness-probe)+','$1'
    $prefix='Stage: launch-start|Stage: launch-complete|Stage: readiness-start|Stage: readiness-probe'
    $full=$prefix+'|Stage: readiness-complete|Stage: copy-start|Stage: copy-complete|Stage: harness-start'
    $failures=@($lines | Where-Object {$_ -like 'Failure: *'})
    switch($Scenario){
        'success' {if($sequence -ne $full -or $failures.Count -ne 0 -or 'Harness: PASS' -notin $lines){throw 'Successful harness path not proven'}}
        'stock' {if($sequence -ne $full -or $failures.Count -ne 1 -or $failures[0] -ne 'Failure: stage=harness; reason=nonzero; category=RuntimeException' -or !($lines -match '^FAIL stock SslCaCerts accepted mismatched server name')){throw 'Stock probe path not proven'}}
        'port-collision' {if(!$ListenerArranged -or $sequence -ne 'Stage: launch-start' -or $failures.Count -ne 1 -or $failures[0] -ne 'Failure: stage=launch; reason=nonzero; category=RuntimeException'){throw 'Arranged launch refusal not proven'}}
        'readiness-failure' {
            $budget=@($lines | Where-Object {$_ -match '^Readiness budget seconds: [0-9]+$'})
            $elapsed=@($lines | Where-Object {$_ -match '^Readiness elapsed milliseconds: [0-9]+$'})
            if($budget.Count -ne 1 -or $elapsed.Count -ne 1 -or [long]($budget[0].Split(': ')[1]) -ne 3 -or [long]($elapsed[0].Split(': ')[1]) -lt 3000){throw 'Intended readiness budget not exhausted'}
            if($sequence -ne $prefix -or $failures.Count -ne 1 -or $failures[0] -ne 'Failure: stage=readiness; reason=deadline; category=TimeoutException'){throw 'Readiness deadline path not proven'}}
        default {throw 'Unknown scenario'}
    }
    $storage=($lines -match '^Run-owned container and 4 anonymous volumes absent\.$') -or ($Scenario -eq 'port-collision' -and 'Run-owned container absent after attempted launch.' -in $lines)
    if(!$storage -or 'Run-owned private directory removed.' -notin $lines -or ($lines -match 'cleanup: FAIL|stop: FAIL|Evidence write: FAIL')){throw 'Independent cleanup not proven'}
}

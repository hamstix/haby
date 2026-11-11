$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'kubernetes-qualification-evidence.ps1')
$path=Join-Path ([IO.Path]::GetTempPath()) ('haby-oracle-'+[Guid]::NewGuid().ToString('N')+'.txt')
$identity=@('Evidence ID: fresh','Run ID: haby179-012345678901')
$cleanup=@('Run-owned container and 4 anonymous volumes absent.','Run-owned private directory removed.')
$ready=@('Readiness budget seconds: 3','Readiness elapsed milliseconds: 3001','Stage: launch-start','Stage: launch-complete','Stage: readiness-start','Stage: readiness-probe','Stage: readiness-probe','Failure: stage=readiness; reason=deadline; category=TimeoutException')
$port=@('Stage: launch-start','Failure: stage=launch; reason=nonzero; category=RuntimeException')
try {
    foreach($scenario in @('port-collision','readiness-failure')){
        $events=if($scenario -eq 'port-collision'){$port}else{$ready}
        ($identity+$events+$cleanup) | Set-Content $path
        Assert-QualificationScenario $path 'fresh' $scenario $true $true
        Write-Output "PASS intended $scenario oracle (synthetic)"
    }
    foreach($case in @('earlier-copy-failure','earlier-launch-failure','late-stop-failure','stale-report','listener-not-arranged','probe-never-reached','harness-unexpectedly-ran','cleanup-failed','premature-timeout')){
        $scenario='readiness-failure';$id='fresh';$arranged=$true;$events=$ready
        switch($case){
            'earlier-copy-failure' {$events=@('Stage: launch-start','Stage: launch-complete','Stage: copy-start','Failure: stage=copy; reason=command; category=RuntimeException')}
            'earlier-launch-failure' {$events=$port}
            'late-stop-failure' {$scenario='port-collision';$events=@('Stage: launch-start','Stage: launch-complete','Stage: harness-start','Harness: PASS','Container stop: FAIL')}
            'stale-report' {$id='another-invocation'}
            'listener-not-arranged' {$scenario='port-collision';$events=$port;$arranged=$false}
            'probe-never-reached' {$events=@($ready | Where-Object {$_ -ne 'Stage: readiness-probe'})}
            'harness-unexpectedly-ran' {$events=$ready+@('Stage: harness-start')}
            'premature-timeout' {$events=$ready -replace 'milliseconds: 3001','milliseconds: 1'}
            'cleanup-failed' {$events=$ready+@('Container/storage cleanup: FAIL')}
        }
        ($identity+$events+$cleanup) | Set-Content $path
        $rejected=$false
        try {Assert-QualificationScenario $path $id $scenario $true $arranged}catch{$rejected=$true}
        if(!$rejected){throw "False positive: $case"}
        Write-Output "PASS rejected $case (synthetic)"
    }
    $before=[IO.File]::ReadAllText($path);$rejected=$false
    try{& (Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1') -EvidencePath $path | Out-Null}catch{$rejected=$true}
    if(!$rejected -or [IO.File]::ReadAllText($path) -ne $before){throw 'Existing evidence overwritten'}
    Write-Output 'PASS existing report rejected before launch and preserved'
}finally{Remove-Item -LiteralPath $path -Force}

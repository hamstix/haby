# Synthetic lifecycle controls only. Real API/storage evidence is separate.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'kubernetes-qualification-process.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-evidence.ps1')
$launcher=Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1'
foreach($runtime in @('wslc','docker')){
foreach($scenario in @('success','inspect-failure','stop-failure','remove-failure','foreign','evidence-failure','partial-launch','absent-launch','readiness-deadline','acl-failure','copy-failure','harness-timeout','journal-invalid','journal-invalid-primary')){
    $state=@{Name='';Private='';Removed=$false;Volumes=$true;Created=$false;Calls=@()}
    $runner={param($file,$arguments,$seconds)
        $state.Calls+=,$arguments
        $result=[pscustomobject]@{ExitCode=0;Output='';Error=''}
        if($file -in @('icacls','chmod')){$state.Private=$arguments[0];if($file -eq 'chmod'){$state.Private=$arguments[1]};[IO.File]::WriteAllText((Join-Path $state.Private 'sentinel'),'harmless');if($scenario -eq 'acl-failure'){$result.ExitCode=17};return $result}
        if($file -eq 'dotnet'){
            if($arguments[0] -eq 'run' -and $scenario -in @('harness-timeout','journal-invalid','journal-invalid-primary')){
                $path=Join-Path $state.Private 'diagnostic-progress.txt'
                [IO.File]::WriteAllText($path, $(if($scenario -like 'journal-invalid*'){ "1 protected-JWT-config-sentinel`n" }else{ "1 durable-entry`n2 catalog-init-start`n" }))
                if($scenario -eq 'harness-timeout'){throw (New-QualificationProcessFailure 'command-deadline' 180001)}
                if($scenario -eq 'journal-invalid-primary'){$result.ExitCode=17}
            }
            $result.Output='synthetic-sdk/harness';return $result
        }
        switch($arguments[0]){
            '--version' {$result.Output='synthetic-runtime'}
            'run' {$state.Name=$arguments[[Array]::IndexOf($arguments,'--name')+1];$state.Created=$scenario -ne 'absent-launch';if($scenario -in @('partial-launch','absent-launch')){$result.ExitCode=17}}
            'exec' {if('--request-timeout=3s' -notin $arguments){throw 'Missing kubectl timeout'};if($scenario -eq 'readiness-deadline'){if($seconds -gt 1){throw 'Unbounded command budget'};throw [TimeoutException]::new('synthetic timeout')};$result.Output='ok'}
            'cp' {if($scenario -eq 'copy-failure'){$result.ExitCode=17;break};[IO.File]::WriteAllText($arguments[-1],'harmless sentinel')}
            'inspect' {if($scenario -eq 'inspect-failure' -or !$state.Created){$result.ExitCode=17}else{$label=if($scenario -eq 'foreign'){'foreign'}else{$state.Name};$result.Output=ConvertTo-Json -Depth 5 -Compress @(@{Id='owned-id';Config=@{Labels=@{'haby.qualification'=$label}};Mounts=@(@{Type='volume';Name='owned-volume';Destination='/var/lib/rancher/k3s'})})}}
            'stop' {if($scenario -eq 'stop-failure'){$result.ExitCode=17}}
            'remove' {if($scenario -eq 'remove-failure'){$result.ExitCode=17}else{if('--volumes' -notin $arguments -or $arguments[-1] -ne 'owned-id'){throw 'Unsafe removal'};$state.Removed=$true;$state.Volumes=$false}}
            'list' {if($file -eq 'docker'){throw 'docker list is not a supported alias'};if($state.Created -and !$state.Removed){$result.Output='owned-id'}}
            'ps' {if($state.Created -and !$state.Removed){$result.Output='owned-id'}}
            'volume' {if($state.Volumes){$result.Output='owned-volume'};if($arguments[-1] -eq 'name=foreign-volume'){$result.Output='foreign-volume'}}
            default {throw 'Unexpected synthetic command'}
        }
        return $result
    }.GetNewClosure()
    $evidence=Join-Path $PSScriptRoot ("../.artifacts/regression-"+[Guid]::NewGuid().ToString("N")+".txt")
    if($scenario -eq 'evidence-failure'){$evidence=((Get-Process -Id $PID).Path+[IO.Path]::DirectorySeparatorChar+[Guid]::NewGuid().ToString('N'))}
    $caught=$false
    $diagnosticMode=$scenario -in @('harness-timeout','journal-invalid','journal-invalid-primary')
    try{& $launcher -Runtime $runtime -CommandRunner $runner -CommandTimeoutSeconds 1 -ReadinessTimeoutSeconds 1 -EvidencePath $evidence -DurableActions:$diagnosticMode | Out-Null}catch{$caught=$true}
    if($caught -ne ($scenario -ne 'success')){throw "Wrong exit: $scenario"}
    if(Test-Path -LiteralPath $state.Private){throw "Private leak: $scenario"}
    if($scenario -in @('success','stop-failure','evidence-failure','partial-launch','readiness-deadline') -and (!$state.Removed -or $state.Volumes)){throw "Owned storage leaked: $scenario"}
    if($scenario -eq 'foreign' -and $state.Removed){throw 'Foreign container deleted'}
    foreach($call in $state.Calls){if('foreign-volume' -in $call -and $call[0] -in @('remove','volume')){throw 'Foreign volume targeted'}}
    if($diagnosticMode){
        $lines=Get-Content $evidence
        if($lines -match 'protected-|JWT|config-sentinel'){throw 'Protected journal content leaked'}
        if($scenario -eq 'harness-timeout' -and ('Failure: stage=harness; reason=command; category=TimeoutException' -notin $lines -or
            'Diagnostic process: command-deadline; elapsed-ms=180001' -notin $lines -or 'Diagnostic progress: 2 catalog-init-start' -notin $lines -or
            'Harness: PASS' -in $lines -or 'Diagnostic inner catalog: unknown' -notin $lines)){throw 'Primary timeout/progress/unknown cleanup not retained'}
        if($scenario -like 'journal-invalid*' -and 'Diagnostic journal: invalid' -notin $lines){throw 'Diagnostic failure missing'}
        if($scenario -eq 'journal-invalid-primary' -and 'Failure: stage=harness; reason=nonzero; category=RuntimeException' -notin $lines){throw 'Diagnostic failure replaced primary harness failure'}
    }
    if($scenario -in @('partial-launch','acl-failure','copy-failure')){
        $idLine=Get-Content $evidence | Where-Object {$_ -like 'Evidence ID: *'}
        $rejected=$false
        try{Assert-QualificationScenario $evidence $idLine.Substring(13) 'readiness-failure' $caught $true}catch{$rejected=$true}
        if(!$rejected){throw "Earlier injected $scenario accepted as readiness failure"}
    }
    Write-Output "PASS lifecycle $runtime $scenario (synthetic)"
}
}
# Actual child-process deadline, independent of the fake runtime.
$clock=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
try{Invoke-QualificationProcess -File (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 60') -TimeoutSeconds 1 | Out-Null}catch [TimeoutException]{
    $timedOut=$true
    $diagnostic=Get-QualificationProcessDiagnostic $_.Exception
    if($diagnostic.Kind -cne 'command-deadline' -or $diagnostic.ElapsedMilliseconds -lt 1000 -or $diagnostic.ElapsedMilliseconds -gt 8000){throw 'Command deadline attribution missing'}
}
if(!$timedOut -or $clock.Elapsed.TotalSeconds -gt 8){throw 'Child deadline failed'}
Write-Output 'PASS real owned child process timeout; no global process kill'

# A pending output task exercises the real existing five-second drain bound without infrastructure.
$pending=[Threading.Tasks.TaskCompletionSource[string]]::new()
$drain=$false
try{Wait-QualificationOutput @($pending.Task,$pending.Task) ([Diagnostics.Stopwatch]::StartNew())}catch [TimeoutException]{
    $drain=(Get-QualificationProcessDiagnostic $_.Exception).Kind -ceq 'output-drain-deadline'
}
if(!$drain){throw 'Output drain deadline attribution missing'}
$termination=New-QualificationProcessFailure 'owned-termination-failure' 1234
if($termination -is [TimeoutException] -or (Get-QualificationProcessDiagnostic $termination).Kind -cne 'owned-termination-failure'){throw 'Termination failure type/attribution changed'}
$unsafe=[TimeoutException]::new('protected-raw-sentinel')
$unsafe.Data['QualificationOutcome']='protected-raw-sentinel'
$unsafe.Data['QualificationElapsedMilliseconds']='protected-raw-sentinel'
if((Get-QualificationProcessDiagnostic $unsafe).Kind -cne 'unknown'){throw 'Unrecognized exception data exposed'}
Write-Output 'PASS bounded process outcome attribution and private exception data'

# Read only fixed journal records, never arbitrary child output or paths.
. (Join-Path $PSScriptRoot 'kubernetes-qualification-diagnostics.ps1')
$journalRoot=Join-Path ([IO.Path]::GetTempPath()) ('haby179-'+[Guid]::NewGuid().ToString('N').Substring(0,12))
New-Item -ItemType Directory -Path $journalRoot | Out-Null
try{
    $journal=Join-Path $journalRoot 'diagnostic-progress.txt'
    if((Read-QualificationJournal $journalRoot).Status -cne 'missing'){throw 'Missing journal not explicit'}
    Initialize-QualificationJournal $journalRoot
    if((Read-QualificationJournal $journalRoot).Status -cne 'empty'){throw 'Empty journal not explicit'}
    $rejected=$false;try{Initialize-QualificationJournal $journalRoot}catch{$rejected=$true}
    if(!$rejected){throw 'CreateNew journal overwrote an existing file'}
    $valid="1 durable-entry`n2 catalog-init-start`n3 catalog-init-completed`n4 namespace-create-start`n5 namespace-create-completed`n6 durable-suite-start`n7 namespace-cleanup-start`n8 namespace-absence-confirmed`n9 catalog-dispose-start`n10 catalog-dispose-completed`n"
    [IO.File]::WriteAllText($journal,$valid)
    $parsed=Read-QualificationJournal $journalRoot
    if($parsed.Status -cne 'valid' -or $parsed.Records.Count -ne 10){throw 'Valid journal rejected'}
    $published=@(Format-QualificationJournal $parsed)
    if($published -match '^PASS |^Harness: PASS$'){throw 'Progress became a passing gate row'}
    foreach($case in @(
        @{Text="1 durable-entry`n2 catalog-init-start";Status='partial'},
        @{Text="1 durable-entry`n3 catalog-init-start`n";Status='invalid'},
        @{Text="1 durable-entry`n2 catalog-dispose-completed`n";Status='invalid'},
        @{Text="1 durable-entry`n2 catalog-init-start`n3 catalog-init-completed`n4 namespace-create-start`n5 namespace-create-completed`n6 namespace-cleanup-start`n7 catalog-dispose-start`n8 namespace-absence-confirmed`n";Status='invalid'},
        @{Text="1 durable-entry`n2 protected-JWT-SQL-config-argument-sentinel`n";Status='invalid'},
        @{Text=('x'*16385);Status='overflow'},
        @{Text=("1 durable-entry`n"*257);Status='overflow'}
    )){
        [IO.File]::WriteAllText($journal,$case.Text)
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed) -join "`n"
        if($parsed.Status -cne $case.Status -or $output -match 'protected-|JWT|SQL|config|argument|PASS'){throw 'Unsafe/incorrect rejected journal evidence'}
        if($output -notmatch 'namespace: unknown' -or $output -notmatch 'catalog: unknown'){throw 'Invalid journal claimed inner cleanup'}
    }
    Remove-Item -LiteralPath $journal
    $outside=Join-Path $journalRoot 'outside.txt'
    [IO.File]::WriteAllText($outside,'protected-link-sentinel')
    if($IsWindows){New-Item -ItemType Junction -Path $journal -Target $journalRoot | Out-Null}
    else{[IO.File]::CreateSymbolicLink($journal,$outside) | Out-Null}
    if((Read-QualificationJournal $journalRoot).Status -cne 'path-refused'){throw 'Linked journal accepted'}
    $rejected=$false;try{Initialize-QualificationJournal $journalRoot}catch{$rejected=$true}
    if(!$rejected){throw 'Existing linked journal opened for writing'}
    if((Read-QualificationJournal (Join-Path $journalRoot '../escape')).Status -cne 'path-refused'){throw 'Escaped directory accepted'}
}finally{
    # Only this independently created exact directory is removed.
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($journalRoot)) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)){throw 'Test cleanup ownership mismatch'}
    if(Test-Path -LiteralPath $journal){Remove-Item -LiteralPath $journal -Force}
    Remove-Item -LiteralPath $journalRoot -Recurse -Force
}
Write-Output 'PASS bounded journal content, ordering, path refusal and no false gate success'

# Linux PATH may expose the same executable through both /usr/bin and /bin.
& {
    function Get-Command { param($Name,$CommandType,$ErrorAction) @([pscustomobject]@{Source='first-path'},[pscustomobject]@{Source='second-path'}) }
    if((Resolve-QualificationExecutable 'fixture') -ne 'first-path'){throw 'Executable resolution joined multiple paths'}
}
Write-Output 'PASS executable resolution selects one application path'

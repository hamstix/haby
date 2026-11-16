# Synthetic lifecycle controls only. Real API/storage evidence is separate.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'kubernetes-qualification-process.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-evidence.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-diagnostics.ps1')
$launcher=Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1'
foreach($runtime in @('wslc','docker')){
foreach($scenario in @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range','inspect-failure','stop-failure','remove-failure','foreign','evidence-failure','partial-launch','absent-launch','readiness-deadline','acl-failure','copy-failure','copy-ca-failure','copy-certificate-failure','copy-key-failure','copy-managed-failure','copy-invalid-exit','copy-primary-cleanup-failure','copy-timeout','harness-timeout','journal-invalid','journal-invalid-primary')){
    $state=@{Name='';Private='';Removed=$false;Volumes=$true;Created=$false;Calls=@();CopyCalls=0}
    $syntheticTimeout=$null
    $copyTimeout=if($scenario -ceq 'copy-timeout'){New-QualificationProcessFailure 'command-deadline' 42L}else{$null}
    if($scenario -eq 'harness-timeout'){
        $syntheticTimeout=New-QualificationProcessFailure 'command-deadline' 180001
        $syntheticTimeoutDiagnostic=Get-QualificationProcessDiagnostic $syntheticTimeout
        if($syntheticTimeout.GetType() -ne [TimeoutException] -or $syntheticTimeoutDiagnostic.Kind -cne 'command-deadline' -or
            $syntheticTimeoutDiagnostic.ElapsedMilliseconds -isnot [long] -or $syntheticTimeoutDiagnostic.ElapsedMilliseconds -ne 180001){throw 'Synthetic timeout fixture construction failed'}
    }
    $runner={param($file,$arguments,$seconds)
        $state.Calls+=,$arguments
        $result=[pscustomobject]@{ExitCode=0;Output='';Error='';ElapsedMilliseconds=42L}
        if($file -in @('icacls','chmod')){$state.Private=$arguments[0];if($file -eq 'chmod'){$state.Private=$arguments[1]};[IO.File]::WriteAllText((Join-Path $state.Private 'sentinel'),'harmless');if($scenario -eq 'acl-failure'){$result.ExitCode=17};return $result}
        if($file -eq 'dotnet'){
            if($arguments[0] -eq 'run'){
                if($scenario -eq 'elapsed-missing'){$result.PSObject.Properties.Remove('ElapsedMilliseconds')}
                if($scenario -eq 'elapsed-malformed'){$result.ElapsedMilliseconds='protected-raw-sentinel'}
                if($scenario -eq 'elapsed-out-of-range'){$result.ElapsedMilliseconds=3600001L}
            }
            if($arguments[0] -eq 'run' -and $scenario -in @('harness-timeout','journal-invalid','journal-invalid-primary')){
                $path=Join-Path $state.Private 'diagnostic-progress.txt'
                [IO.File]::WriteAllText($path, $(if($scenario -like 'journal-invalid*'){ "1 protected-JWT-config-sentinel 0`n" }else{ "1 durable-entry 0`n2 catalog-init-start 10`n" }))
                if($scenario -eq 'harness-timeout'){throw $syntheticTimeout}
                if($scenario -eq 'journal-invalid-primary'){$result.ExitCode=17}
            }
            $result.Output='synthetic-sdk/harness';return $result
        }
        switch($arguments[0]){
            '--version' {$result.Output='synthetic-runtime'}
            'run' {$state.Name=$arguments[[Array]::IndexOf($arguments,'--name')+1];$state.Created=$scenario -ne 'absent-launch';if($scenario -in @('partial-launch','absent-launch')){$result.ExitCode=17}}
            'exec' {if('--request-timeout=3s' -notin $arguments){throw 'Missing kubectl timeout'};if($scenario -eq 'readiness-deadline'){if($seconds -gt 1){throw 'Unbounded command budget'};throw [TimeoutException]::new('synthetic timeout')};$result.Output='ok'}
            'cp' {
                $state.CopyCalls++
                $failAt=switch($scenario){'copy-certificate-failure'{2};'copy-key-failure'{3};default{1}}
                if($scenario -like 'copy-*' -and $state.CopyCalls -eq $failAt){
                    $result.Output='protected-raw-copy-sentinel';$result.Error='protected-raw-copy-sentinel'
                    if($scenario -ceq 'copy-managed-failure'){throw [InvalidOperationException]::new('protected-raw-copy-sentinel')}
                    if($scenario -ceq 'copy-timeout'){throw $copyTimeout}
                    $result.ExitCode=if($scenario -ceq 'copy-invalid-exit'){'protected-raw-copy-sentinel'}elseif($scenario -ceq 'copy-primary-cleanup-failure'){23}else{17}
                    break
                }
                [IO.File]::WriteAllText($arguments[-1],'harmless sentinel')
            }
            'inspect' {if($scenario -eq 'inspect-failure' -or !$state.Created){$result.ExitCode=17}else{$label=if($scenario -eq 'foreign'){'foreign'}else{$state.Name};$result.Output=ConvertTo-Json -Depth 5 -Compress @(@{Id='owned-id';Config=@{Labels=@{'haby.qualification'=$label}};Mounts=@(@{Type='volume';Name='owned-volume';Destination='/var/lib/rancher/k3s'})})}}
            'stop' {if($scenario -in @('stop-failure','copy-primary-cleanup-failure')){$result.ExitCode=17}}
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
    if($caught -ne ($scenario -cnotin @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range'))){throw "Wrong exit: $scenario"}
    if($scenario -cin @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range')){
        $lines=Get-Content $evidence
        $expectedElapsed=if($scenario -ceq 'success'){'Harness command elapsed milliseconds: 42'}else{'Harness command elapsed milliseconds: unavailable'}
        if($expectedElapsed -notin $lines -or 'Harness command elapsed origin: before-process-start-through-output-drain; includes-build-startup' -notin $lines -or
            'Harness: PASS' -notin $lines -or $lines -match 'protected-raw-sentinel|3600001'){throw 'Unsafe or missing successful command timing'}
    }
    if(Test-Path -LiteralPath $state.Private){throw "Private leak: $scenario"}
    if($scenario -like 'copy-*'){
        $lines=@(Get-Content $evidence)
        $ids=@('copy-server-ca','copy-admin-certificate','copy-admin-key')
        $failedIndex=switch($scenario){'copy-certificate-failure'{1};'copy-key-failure'{2};default{0}}
        $expected=@(foreach($index in 0..$failedIndex){'Copy operation: '+$ids[$index]+'; state=start';if($index -lt $failedIndex){'Copy operation: '+$ids[$index]+'; state=complete'}})
        if((@($lines | Where-Object {$_ -clike 'Copy operation:*'}) -join "`n") -cne ($expected -join "`n") -or
            ('Copy failure: operation='+$ids[$failedIndex]) -cnotin $lines -or 'Stage: harness-start' -cin $lines -or
            !$state.Removed -or $state.Volumes -or $lines -match 'protected-raw-copy-sentinel'){throw 'Unsafe or incomplete copy failure attribution'}
        $exitRecords=@($lines | Where-Object {$_ -clike 'Copy native exit:*'})
        if($scenario -cin @('copy-managed-failure','copy-invalid-exit','copy-timeout')){
            if($exitRecords.Count -ne 0){throw 'Native result invented for managed or invalid failure'}
        }elseif($exitRecords.Count -ne 1 -or $exitRecords[0] -cne $(if($scenario -ceq 'copy-primary-cleanup-failure'){'Copy native exit: 23'}else{'Copy native exit: 17'})){throw 'Primary copy exit lost or overwritten by cleanup'}
        if($scenario -ceq 'copy-managed-failure' -and 'Diagnostic process: unknown' -cnotin $lines){throw 'Managed failure relabeled as a native process outcome'}
        if($scenario -ceq 'copy-timeout' -and 'Diagnostic process: command-deadline; elapsed-ms=42' -cnotin $lines){throw 'Existing process kind lost'}
        if($scenario -ceq 'copy-primary-cleanup-failure' -and 'Container stop: FAIL' -cnotin $lines){throw 'Independent cleanup failure lost'}
    }
    if($scenario -in @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range','stop-failure','evidence-failure','partial-launch','readiness-deadline') -and (!$state.Removed -or $state.Volumes)){throw "Owned storage leaked: $scenario"}
    if($scenario -eq 'foreign' -and $state.Removed){throw 'Foreign container deleted'}
    foreach($call in $state.Calls){if('foreign-volume' -in $call -and $call[0] -in @('remove','volume')){throw 'Foreign volume targeted'}}
    if($diagnosticMode){
        $lines=Get-Content $evidence
        if($lines -match 'protected-|JWT|config-sentinel'){throw 'Protected journal content leaked'}
        if($scenario -eq 'harness-timeout'){
            $hasPrimaryFailureRecord=-not ('Failure: stage=harness; reason=command; category=TimeoutException' -notin $lines)
            $hasProcessKindElapsedRecord=-not ('Diagnostic process: command-deadline; elapsed-ms=180001' -notin $lines)
            $hasJournalProgressRecord=-not ('Diagnostic progress: 2 catalog-init-start 10' -notin $lines)
            $hasNoHarnessPass=-not ('Harness: PASS' -in $lines)
            $hasCatalogUnknownRecord=-not ('Diagnostic inner catalog: unknown' -notin $lines)
            if(!$hasPrimaryFailureRecord -or !$hasProcessKindElapsedRecord -or !$hasJournalProgressRecord -or !$hasNoHarnessPass -or !$hasCatalogUnknownRecord){
                Write-Output ('primary-failure-record='+$hasPrimaryFailureRecord.ToString().ToLowerInvariant())
                Write-Output ('process-kind-elapsed-record='+$hasProcessKindElapsedRecord.ToString().ToLowerInvariant())
                Write-Output ('journal-progress-record='+$hasJournalProgressRecord.ToString().ToLowerInvariant())
                Write-Output ('harness-pass-absent='+$hasNoHarnessPass.ToString().ToLowerInvariant())
                Write-Output ('catalog-unknown-record='+$hasCatalogUnknownRecord.ToString().ToLowerInvariant())
                $primaryRecords=@($lines | Where-Object {$_ -cmatch '^Failure:'})
                $primaryCategory='other'
                if($primaryRecords.Count -eq 0){$primaryCategory='missing'}
                elseif($primaryRecords.Count -gt 1){$primaryCategory='ambiguous'}
                elseif($primaryRecords[0] -cmatch '^Failure: stage=(?:setup|launch|readiness|copy|harness); reason=(?:command|nonzero|deadline|child-deadline); category=([A-Za-z][A-Za-z0-9]{0,63})$'){
                    $primaryCategory=switch -CaseSensitive ($Matches[1]){
                        'TimeoutException' {'timeout'}
                        'RuntimeException' {'runtime'}
                        'MethodInvocationException' {'method-invocation'}
                        'CommandNotFoundException' {'command-not-found'}
                        'ParameterBindingException' {'parameter-binding'}
                        default {'other'}
                    }
                }
                $processRecords=@($lines | Where-Object {$_ -cmatch '^Diagnostic process:'})
                $processOutcome='invalid'
                if($processRecords.Count -eq 0){$processOutcome='missing'}
                elseif($processRecords.Count -gt 1){$processOutcome='ambiguous'}
                elseif($processRecords[0] -ceq 'Diagnostic process: unknown'){$processOutcome='unknown'}
                elseif($processRecords[0] -cmatch '^Diagnostic process: (command-deadline|output-drain-deadline|owned-termination-failure); elapsed-ms=([0-9]{1,7})$'){
                    if([long]$Matches[2] -le 3600000){$processOutcome=$Matches[1]}
                }
                Write-Output ('primary-category='+$primaryCategory)
                Write-Output ('process-outcome='+$processOutcome)
                throw 'Primary timeout/progress/unknown cleanup not retained'
            }
        }
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
# Keep configured bridge/certificate controls with an isolated synthetic binding;
# the fixture copy can never become an accepted repository/public image mapping.
if($IsLinux){
    . (Join-Path $PSScriptRoot 'runtime-consumer-image.ps1')
    $inputModel=Get-HabyConsumerImageInputs (Split-Path $PSScriptRoot -Parent)
    $fixture=Join-Path (Split-Path $PSScriptRoot -Parent) ('.artifacts/haby-image-controls-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $fixture|Out-Null
    try{
        foreach($entry in $inputModel.files.GetEnumerator()){$target=Join-Path $fixture $entry.Key;New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::WriteAllText($target,$entry.Value,[Text.UTF8Encoding]::new($false))}
        foreach($name in @('run-kubernetes-qualification.ps1','kubernetes-qualification-process.ps1','kubernetes-qualification-diagnostics.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture ('scripts/'+$name))}
        $binding=Join-Path $fixture ('tests/fixtures/runtime-consumer-images/'+$inputModel.key+'.json')
        New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($binding))|Out-Null
        [ordered]@{format=1;package=$HabyConsumerPackage;inputKey=$inputModel.key;digest=('sha256:'+('a'*64));builtSource=('b'*40);publisherSource=('c'*40);publisherRunId=1;proofArtifactSha256=('d'*64)}|ConvertTo-Json|Set-Content -LiteralPath $binding -Encoding utf8
        foreach($operation in @('none','inspect-bridge-gateway','validate-bridge-gateway','write-bridge-address')){
            $state=@{Name='';Private='';Removed=$false;Volumes=$true;NetworkInspected=$false}
            $runner={param($file,$arguments,$seconds)
                $result=[pscustomobject]@{ExitCode=0;Output='';Error='';ElapsedMilliseconds=42L}
                if($file -ceq 'chmod'){$state.Private=$arguments[1];return $result}
                if($file -ceq 'dotnet'){$result.Output='synthetic-sdk/harness';return $result}
                switch($arguments[0]){
                    '--version' {$result.Output='synthetic-runtime'}
                    'run' {$state.Name=$arguments[[Array]::IndexOf($arguments,'--name')+1]}
                    'exec' {if('nodes' -cin $arguments){$result.Output='{"items":[{"status":{"conditions":[{"type":"Ready","status":"True"}]}}]}'}elseif('--request-timeout=3s' -cin $arguments){$result.Output='ok'}else{throw 'Forbidden consumer archive/import call'}}
                    'cp' {[IO.File]::WriteAllText($arguments[-1],'protected-key-sentinel')}
                    'inspect' {
                        $networkRead=!$state.NetworkInspected;$state.NetworkInspected=$true
                        if($networkRead -and $operation -ceq 'inspect-bridge-gateway'){$result.ExitCode=17;break}
                        if($networkRead -and $operation -ceq 'write-bridge-address'){New-Item -ItemType Directory (Join-Path $state.Private 'pod-host-address')|Out-Null}
                        $gateway=if($operation -ceq 'validate-bridge-gateway'){'protected-address-sentinel'}else{'192.0.2.2'}
                        $result.Output=ConvertTo-Json -Depth 8 -Compress @(@{Id='owned-id';Config=@{Labels=@{'haby.qualification'=$state.Name}};Mounts=@(@{Type='volume';Name='owned-volume'});NetworkSettings=@{Networks=@{bridge=@{Gateway=$gateway}}}})
                    }
                    'stop' {}
                    'remove' {$state.Removed=$true;$state.Volumes=$false}
                    'ps' {if(!$state.Removed){$result.Output='owned-id'}}
                    'volume' {if($state.Volumes){$result.Output='owned-volume'}}
                    default {throw 'Forbidden image build/save/publish or unexpected control command'}
                }
                return $result
            }.GetNewClosure()
            $evidence=Join-Path $fixture ($operation+'.txt');$caught=$false
            try{& (Join-Path $fixture 'scripts/run-kubernetes-qualification.ps1') -Runtime docker -ConfiguredDeployment -CommandRunner $runner -EvidencePath $evidence|Out-Null}catch{$caught=$true}
            $lines=@(Get-Content -LiteralPath $evidence)
            if($caught -ne ($operation -cne 'none') -or !$state.Removed -or $state.Volumes -or (Test-Path -LiteralPath $state.Private) -or $lines -match 'protected-.*-sentinel'){throw 'Configured bridge or cleanup control failed'}
            if($operation -cne 'none' -and ('Stage: harness-start' -cin $lines -or ('Copy failure: operation='+$operation) -cnotin $lines)){throw 'Configured primary failure lost'}
            Write-Output ('PASS configured bridge '+$operation+' (synthetic; no registry/image operation)')
        }
    }finally{Set-Location (Split-Path $PSScriptRoot -Parent);Remove-Item -LiteralPath $fixture -Recurse -Force}
}
# The real setup path uses only a fake existing fixture lifetime; no Docker/API is opened.
& dotnet run --project (Join-Path $PSScriptRoot '../tools/Haby.Kubernetes.Qualification') --no-restore -- --configured-setup-controls
if($LASTEXITCODE -ne 0){throw 'Configured setup lifetime controls failed'}
# Actual success uses the same process-start-through-output-drain clock; no timing threshold or sleep.
$success=Invoke-QualificationProcess -File (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-Command',"'harmless-owned-child'")
if($success.ExitCode -ne 0 -or $success.Output.Trim() -cne 'harmless-owned-child' -or $success.ElapsedMilliseconds -isnot [long] -or
    $success.ElapsedMilliseconds -lt 0 -or $success.ElapsedMilliseconds -gt 3600000){throw 'Owned success elapsed missing or invalid'}
Write-Output 'PASS real owned child success elapsed; separate parent command clock'
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
    $prefix="1 durable-entry 0`n2 catalog-init-start 10`n3 catalog-init-completed 20`n4 namespace-create-start 30`n5 namespace-create-completed 40`n6 durable-suite-start 50`n"
    $pairs="7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 initial-delivery-start 80`n10 runtime-auth-start 90`n11 runtime-auth-completed 100`n"
    $valid=$prefix+$pairs+"12 mixed-delivery-start 110`n13 mixed-preparation-completed 120`n14 mixed-documents-completed 130`n15 mixed-identity-completed 140`n16 mixed-publication-completed 150`n17 mixed-delivery-completed 160`n18 namespace-cleanup-start 170`n19 namespace-absence-confirmed 180`n20 catalog-dispose-start 190`n21 catalog-dispose-completed 200`n"
    [IO.File]::WriteAllText($journal,$valid)
    $parsed=Read-QualificationJournal $journalRoot
    if($parsed.Status -cne 'valid' -or $parsed.Records.Count -ne 21){throw 'Valid journal rejected'}
    $published=@(Format-QualificationJournal $parsed)
    if($published -match '^PASS |^Harness: PASS$'){throw 'Progress became a passing gate row'}
    if('Diagnostic managed elapsed origin: journal-initialization; excludes-build-startup; clock=monotonic' -notin $published -or
        'Diagnostic inner namespace: confirmed' -notin $published -or 'Diagnostic inner catalog: confirmed' -notin $published){throw 'Managed clock origin or actual cleanup evidence missing'}
    $entryIds=@('durable-entry','catalog-init-start','catalog-init-completed','namespace-create-start','namespace-create-completed','durable-suite-start','runtime-auth-start','runtime-auth-completed')
    $finalIds=@('runtime-final-revoke-start','runtime-final-revoke-completed','runtime-final-history-start','runtime-final-history-completed','runtime-final-authorization-start','runtime-final-authorization-completed')
    $disposeIds=@('runtime-candidate-dispose-start','runtime-candidate-dispose-completed')
    $firstIds=$entryIds+$finalIds+$disposeIds
    $secondIds=$firstIds+@('initial-delivery-start','runtime-auth-start','runtime-auth-completed')+$finalIds+$disposeIds
    $cleanupIds=@('inner-cleanup-start','namespace-cleanup-start','namespace-absence-confirmed','catalog-dispose-start','catalog-dispose-completed','inner-cleanup-completed')
    $fullIds=$secondIds+@('mixed-delivery-start','mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed','mixed-delivery-completed')+$cleanupIds
    if($fullIds.Count -ne 39){throw 'Closed boundary control inventory changed'}
    # Optional finite scenario-group boundaries preserve old journals and interrupted cleanup.
    $earlyIds=@('workload-actions-start','accepted-setup-start','accepted-setup-completed','restore-start','restore-completed',
        'accepted-setup-start','accepted-setup-completed','workload-actions-completed','identity-actions-start',
        'runtime-setup-start','runtime-setup-completed','restore-start','restore-completed','identity-actions-completed')
    $earlyPrefix=$entryIds[0..5]+$earlyIds
    $earlyFull=$earlyPrefix+$secondIds[6..($secondIds.Count-1)]+@('mixed-delivery-start','mixed-baseline-prepared',
        'mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed','mixed-delivery-completed')+$cleanupIds
    foreach($case in @(
        @{Ids=$earlyFull;Status='valid'},
        @{Ids=($entryIds[0..5]+$earlyIds[0..1]);Status='valid'},
        @{Ids=($entryIds[0..5]+$earlyIds[0..1]+$cleanupIds);Status='valid'},
        @{Ids=($entryIds[0..5]+@('accepted-setup-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','restore-start'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','workload-actions-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','accepted-setup-completed','accepted-setup-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('restore-start'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start')+(@('accepted-setup-start','accepted-setup-completed')*17));Status='invalid'},
        @{Ids=($secondIds+@('mixed-delivery-start','mixed-preparation-completed','mixed-baseline-prepared'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','protected-raw-sentinel'));Status='invalid'}
    )){
        $records=@(for($index=0;$index -lt $case.Ids.Count;$index++){($index+1).ToString()+' '+$case.Ids[$index]+' '+($index*10).ToString()})
        [IO.File]::WriteAllText($journal,($records -join "`n")+"`n")
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status -or $output -match '^PASS |^Harness: PASS$|protected-'){
            throw 'Early scenario boundaries, finite repeats or value exclusion changed'
        }
    }
    Write-Output 'PASS early scenario preparation, restore, action-group and mixed-baseline boundaries (synthetic)'

    foreach($case in @(
        @{Ids=$fullIds;Status='valid';Outcome='completed'},
        @{Ids=($entryIds+@('runtime-final-revoke-start')+$disposeIds+$cleanupIds);Status='valid';Outcome='completed'},
        @{Ids=($entryIds[0..6]+$disposeIds+$cleanupIds);Status='valid';Outcome='completed'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-start','runtime-candidate-dispose-failed','inner-cleanup-start','namespace-cleanup-start','namespace-cleanup-failed','catalog-dispose-start','catalog-dispose-failed','inner-cleanup-failed'));Status='valid';Outcome='failed'},
        @{Ids=($entryIds+@('runtime-final-history-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($firstIds+@('runtime-candidate-dispose-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+$disposeIds);Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','inner-cleanup-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+$cleanupIds+@('runtime-auth-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-start','inner-cleanup-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','namespace-cleanup-start','namespace-cleanup-failed','namespace-absence-confirmed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','namespace-cleanup-start','namespace-absence-confirmed','catalog-dispose-start','catalog-dispose-failed','catalog-dispose-completed'));Status='invalid';Outcome='unknown'}
    )){
        $records=@(for($index=0;$index -lt $case.Ids.Count;$index++){($index+1).ToString()+' '+$case.Ids[$index]+' '+($index*10).ToString()})
        [IO.File]::WriteAllText($journal,($records -join "`n")+"`n")
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status -or ('Diagnostic inner cleanup outcome: '+$case.Outcome) -cnotin $output -or $output -match '^PASS |^Harness: PASS$|protected-'){
            throw 'Closed final-boundary ordering, terminal outcome or partial cleanup control failed'
        }
    }
    Write-Output 'PASS final durable boundaries, partial cancellation, disposal and cleanup outcome controls (synthetic)'
    # Incomplete auth pairs are valid progress, including failure paths that reach owned cleanup.
    foreach($text in @(
        "1 durable-entry 3600000`n2 catalog-init-start 3600000`n",
        ($prefix+"7 runtime-auth-start 60`n"),
        ($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 60`n"),
        ($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 initial-delivery-start 80`n10 runtime-auth-start 90`n"),
        ($prefix+"7 runtime-auth-start 60`n8 namespace-cleanup-start 70`n9 namespace-absence-confirmed 80`n10 catalog-dispose-start 90`n11 catalog-dispose-completed 100`n")
    )){
        [IO.File]::WriteAllText($journal,$text)
        $partial=Read-QualificationJournal $journalRoot
        if($partial.Status -cne 'valid'){throw 'Valid equal/bounded timing or incomplete pair rejected'}
        $output=@(Format-QualificationJournal $partial)
        if($output -match '^PASS |^Harness: PASS$'){throw 'Incomplete phase became case success'}
        if($text -notmatch 'catalog-dispose-completed' -and ('Diagnostic inner namespace: unknown' -notin $output -or 'Diagnostic inner catalog: unknown' -notin $output)){throw 'Incomplete phase claimed cleanup'}
    }
    foreach($case in @(
        @{Text="1 durable-entry 0`n2 catalog-init-start 10";Status='partial'},
        @{Text="1 durable-entry 0`n3 catalog-init-start 10`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-dispose-completed 10`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-init-start 10`n3 catalog-init-completed 20`n4 namespace-create-start 30`n5 namespace-create-completed 40`n6 namespace-cleanup-start 50`n7 catalog-dispose-start 60`n8 namespace-absence-confirmed 70`n";Status='invalid'},
        @{Text=($prefix+$pairs+"12 mixed-delivery-start 110`n13 mixed-documents-completed 120`n");Status='invalid'},
        @{Text=($prefix+$pairs+"12 mixed-delivery-start 110`n13 namespace-cleanup-start 120`n14 mixed-preparation-completed 130`n");Status='invalid'},
        @{Text="1 durable-entry 0`n2 protected-JWT-SQL-config-argument-sentinel 10`n";Status='invalid'},
        @{Text="1 durable-entry 00`n";Status='invalid'},
        @{Text="1 durable-entry -1`n";Status='invalid'},
        @{Text="1 durable-entry +1`n";Status='invalid'},
        @{Text="1 durable-entry 1.0`n";Status='invalid'},
        @{Text="1 durable-entry 1e2`n";Status='invalid'},
        @{Text="1 durable-entry 3600001`n";Status='invalid'},
        @{Text="1 durable-entry 99999999999999999999`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-init-start 0 extra`n";Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 49`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-completed 60`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-start 70`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 runtime-auth-completed 80`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 runtime-auth-start 80`n");Status='invalid'},
        @{Text=($prefix+$pairs+"12 runtime-auth-start 110`n");Status='invalid'},
        @{Text=($prefix+"7 initial-delivery-start 60`n");Status='invalid'},
        @{Text=($prefix+"7 mixed-delivery-start 60`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 namespace-cleanup-start 70`n9 runtime-auth-completed 80`n");Status='invalid'},
        @{Text=('x'*16385);Status='overflow'},
        @{Text=("1 durable-entry 0`n"*257);Status='overflow'}
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

# This channel accepts only fixed progress IDs and bounded monotonic managed elapsed milliseconds.
function Get-QualificationJournalPath([string]$PrivateDirectory) {
    $directory=[IO.Path]::GetFullPath($PrivateDirectory)
    $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if([IO.Path]::GetDirectoryName($directory) -ne $parent -or (Split-Path $directory -Leaf) -cnotmatch '^haby179-[0-9a-f]{12}$'){throw 'Diagnostic directory ownership refused.'}
    $item=Get-Item -LiteralPath $directory -Force -ErrorAction Stop
    if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Diagnostic directory link refused.'}
    $path=Join-Path $directory 'diagnostic-progress.txt'
    if(Test-Path -LiteralPath $path){
        $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Diagnostic journal link refused.'}
    }
    return $path
}
function Initialize-QualificationJournal([string]$PrivateDirectory) {
    $path=Get-QualificationJournalPath $PrivateDirectory
    $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $stream.Dispose()
}
# Caller-only spans have finite lexical contexts. Server hooks emit no records.
function Test-QualificationCallerSequence([string[]]$Ids,[string[]]$Stems) {
    $context=0; $hostRole=''; $requestCompleted=$false; $disposing=$false; $disposed=$false
    $body=@(); $cursor=0; $active=''; $failed=$false; $enhanced=$false; $disposeOutcome=''
    $startupActive=''; $startupCursor=0; $startupFailed=$false; $startupCleaning=$false
    $podCreated=$false; $podCleaned=$false; $directoryCleaned=$false; $catalogSeen=$false
    $children=@('startup-pod','startup-token','startup-file','startup-fingerprints','startup-before-state',
        'startup-first-fetch','startup-after-state','startup-restart-fetch','startup-revoke','startup-denial-probe','startup-republish')
    $counts=@{}; $outerCleanup=$false
    foreach($id in $Ids){
        $stem=''; $terminal=''
        if($id -cmatch '^(.*)-(start|completed|failed)$' -and $Matches[1] -cin $Stems){$stem=$Matches[1];$terminal=$Matches[2]}
        if(!$stem){
            if($active -or $startupActive){return $false}
            if($failed -and $id -cin @('runtime-auth-start','initial-delivery-start','mixed-delivery-start')){return $false}
            if($id -cin @('inner-cleanup-start','namespace-cleanup-start','catalog-dispose-start')){$outerCleanup=$true}
            switch -CaseSensitive ($id){
                'initial-delivery-start'{$context=1;$catalogSeen=$false}
                'mixed-delivery-start'{$context=2;$catalogSeen=$false}
                {$_ -cin @('delivery-host-first-start','delivery-host-restart-start')}{
                    if($failed){return $false}
                    $hostRole=if($id -ceq 'delivery-host-first-start'){'first'}else{'restart'}
                    $requestCompleted=$false;$disposing=$false;$disposed=$false;$cursor=0;$enhanced=$false;$disposeOutcome=''
                    $body=@('delivery-after-state','delivery-result-check')
                    if($hostRole -ceq 'first'){
                        $body+=@('delivery-review-refusal','delivery-review-republish','delivery-authority-call','delivery-authority-refusal','delivery-authority-republish')
                        if($context -eq 1){$body+=@('delivery-startup')}
                    }
                }
                'delivery-first-request-completed'{$requestCompleted=$true}
                {$_ -cin @('delivery-host-first-dispose-start','delivery-host-restart-dispose-start')}{$disposing=$true}
                {$_ -cin @('delivery-host-first-dispose-completed','delivery-host-restart-dispose-completed')}{
                    if($enhanced -and $disposeOutcome -cne 'completed'){return $false}
                    $disposed=$true
                }
                'delivery-pod-cleanup-start'{$hostRole=''}
            }
            continue
        }
        if($context -eq 0 -or $outerCleanup){return $false}
        if($terminal -ceq 'start'){
            if(!$counts.ContainsKey($stem)){$counts[$stem]=0}
            $counts[$stem]++
            $maximum=if($stem.StartsWith('startup-',[StringComparison]::Ordinal) -or $stem -ceq 'delivery-startup'){1}
                elseif($stem -cin @('delivery-after-state','delivery-result-check','delivery-host-stop','delivery-host-dispose')){4}else{2}
            if($counts[$stem] -gt $maximum){return $false}
        }
        if($stem.StartsWith('startup-',[StringComparison]::Ordinal)){
            if($active -cne 'delivery-startup' -or $context -ne 1 -or $hostRole -cne 'first'){return $false}
            if($terminal -ceq 'start'){
                if($startupActive){return $false}
                if($stem -ceq 'startup-pod-cleanup'){
                    if(!$podCreated -or $podCleaned -or $directoryCleaned){return $false}
                    $startupCleaning=$true
                }elseif($stem -ceq 'startup-directory-cleanup'){
                    if(($podCreated -and !$podCleaned) -or $directoryCleaned){return $false}
                    $startupCleaning=$true
                }elseif($startupFailed -or $startupCleaning -or $startupCursor -ge $children.Count -or $stem -cne $children[$startupCursor]){return $false}
                $startupActive=$stem
            }else{
                if($startupActive -cne $stem){return $false}
                $startupActive=''
                if($stem -ceq 'startup-pod-cleanup'){$podCleaned=$true}
                elseif($stem -ceq 'startup-directory-cleanup'){$directoryCleaned=$true}
                else{$startupCursor++}
                if($stem -ceq 'startup-pod' -and $terminal -ceq 'completed'){$podCreated=$true}
                if($terminal -ceq 'failed'){$startupFailed=$true}
            }
            continue
        }
        if($terminal -ceq 'start'){
            if($active){return $false}
            if($stem -ceq 'delivery-catalog-verify'){
                if($hostRole -cne 'restart' -or !$disposed -or $failed -or $catalogSeen){return $false}
                $catalogSeen=$true
            }elseif($stem -ceq 'delivery-host-dispose'){
                if(!$hostRole -or !$disposing -or $disposed -or $disposeOutcome){return $false}
            }elseif($stem -ceq 'delivery-host-stop'){
                if(!$hostRole -or !$disposing -or $disposed -or $failed -or ($enhanced -and $cursor -ne $body.Count)){return $false}
            }else{
                if(!$hostRole -or !$requestCompleted -or $disposing -or $failed -or $cursor -ge $body.Count -or $stem -cne $body[$cursor]){return $false}
                if($stem -ceq 'delivery-startup'){
                    $startupCursor=0;$startupFailed=$false;$startupCleaning=$false;$podCreated=$false;$podCleaned=$false;$directoryCleaned=$false
                }
            }
            $active=$stem;$enhanced=$true
        }else{
            if($active -cne $stem -or $startupActive){return $false}
            if($stem -ceq 'delivery-startup' -and $terminal -ceq 'completed' -and
                ($startupFailed -or $startupCursor -ne $children.Count -or !$podCleaned -or !$directoryCleaned)){return $false}
            $active=''
            if($stem -ceq 'delivery-host-dispose'){$disposeOutcome=$terminal}
            elseif($stem -cnotin @('delivery-host-stop','delivery-catalog-verify')){$cursor++}
            if($terminal -ceq 'failed'){$failed=$true}
        }
    }
    return $true
}

function Read-QualificationJournal([string]$PrivateDirectory) {
    function Result([string]$Status,[string[]]$Records=@()){return [pscustomobject]@{Status=$Status;Records=$Records}}
    try{$path=Get-QualificationJournalPath $PrivateDirectory}catch{return Result 'path-refused'}
    try{
        if(!(Test-Path -LiteralPath $path)){return Result 'missing'}
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try{
            if($stream.Length -gt 16384){return Result 'overflow'}
            $bytes=[byte[]]::new(16385); $length=0
            while($length -lt $bytes.Length){$read=$stream.Read($bytes,$length,$bytes.Length-$length);if($read -eq 0){break};$length+=$read}
            if($length -gt 16384){return Result 'overflow'}
        }finally{$stream.Dispose()}
        if($length -eq 0){return Result 'empty'}
        # ASCII only; replacement decoding must not turn malformed bytes into accepted IDs.
        for($i=0;$i -lt $length;$i++){if($bytes[$i] -gt 127 -or $bytes[$i] -eq 0){return Result 'invalid'}}
        $partial=$bytes[$length-1] -ne 10
        $lines=[Text.Encoding]::ASCII.GetString($bytes,0,$length).Split("`n")
        if($lines.Length-1+[int]$partial -gt 256){return Result 'overflow'}
        if($partial -and $lines[-1].Length -gt 61){return Result 'invalid'}
        $requires=@{
            'durable-entry'='';'catalog-init-start'='durable-entry';'catalog-init-completed'='catalog-init-start'
            'namespace-create-start'='catalog-init-completed';'namespace-create-completed'='namespace-create-start'
            'durable-suite-start'='namespace-create-completed';'runtime-auth-start'='durable-suite-start'
            'workload-actions-start'='durable-suite-start';'workload-actions-completed'='workload-actions-start'
            'accepted-setup-start'='workload-actions-start';'accepted-setup-completed'='accepted-setup-start'
            'restore-start'='durable-suite-start';'restore-completed'='restore-start'
            'identity-actions-start'='workload-actions-completed';'identity-actions-completed'='identity-actions-start'
            'runtime-setup-start'='identity-actions-start';'runtime-setup-completed'='runtime-setup-start'
            'mixed-baseline-prepared'='mixed-delivery-start'
            'runtime-auth-completed'='runtime-auth-start'
            'runtime-final-revoke-start'='runtime-auth-completed';'runtime-final-revoke-completed'='runtime-final-revoke-start'
            'runtime-final-history-start'='runtime-final-revoke-completed';'runtime-final-history-completed'='runtime-final-history-start'
            'runtime-final-authorization-start'='runtime-final-history-completed';'runtime-final-authorization-completed'='runtime-final-authorization-start'
            'runtime-candidate-dispose-start'='durable-suite-start';'runtime-candidate-dispose-completed'='runtime-candidate-dispose-start';'runtime-candidate-dispose-failed'='runtime-candidate-dispose-start'
            'inner-cleanup-start'='catalog-init-completed';'inner-cleanup-completed'='inner-cleanup-start';'inner-cleanup-failed'='inner-cleanup-start'
            'namespace-cleanup-failed'='namespace-cleanup-start';'catalog-dispose-failed'='catalog-dispose-start'
            'initial-delivery-start'='runtime-auth-completed';'mixed-delivery-start'='durable-suite-start'
            'mixed-preparation-completed'='mixed-delivery-start';'mixed-documents-completed'='mixed-preparation-completed'
            'mixed-identity-completed'='mixed-documents-completed';'mixed-publication-completed'='mixed-identity-completed'
            'mixed-delivery-completed'='mixed-publication-completed'
            'delivery-provenance-start'='durable-suite-start';'delivery-provenance-completed'='delivery-provenance-start'
            'delivery-pod-start'='delivery-provenance-completed';'delivery-pod-completed'='delivery-pod-start'
            'delivery-token-start'='delivery-pod-completed';'delivery-token-completed'='delivery-token-start'
            'delivery-host-first-start'='delivery-token-completed';'delivery-host-first-built'='delivery-host-first-start'
            'delivery-host-first-credential-ready'='delivery-host-first-built';'delivery-host-first-started'='delivery-host-first-credential-ready'
            'delivery-host-first-dispose-start'='delivery-host-first-start';'delivery-host-first-dispose-completed'='delivery-host-first-dispose-start'
            'delivery-host-restart-start'='delivery-host-first-dispose-completed';'delivery-host-restart-built'='delivery-host-restart-start'
            'delivery-host-restart-credential-ready'='delivery-host-restart-built';'delivery-host-restart-started'='delivery-host-restart-credential-ready'
            'delivery-host-restart-dispose-start'='delivery-host-restart-start';'delivery-host-restart-dispose-completed'='delivery-host-restart-dispose-start'
            'delivery-first-request-start'='delivery-token-completed';'delivery-first-request-completed'='delivery-first-request-start'
            'delivery-pod-cleanup-start'='delivery-pod-completed';'delivery-pod-cleanup-completed'='delivery-pod-cleanup-start';'delivery-pod-cleanup-failed'='delivery-pod-cleanup-start'
            'namespace-cleanup-start'='namespace-create-completed';'namespace-absence-confirmed'='namespace-cleanup-start'
            'catalog-dispose-start'='catalog-init-completed';'catalog-dispose-completed'='catalog-dispose-start'
        }
        $callerStems=@(
            'delivery-after-state','delivery-result-check','delivery-review-refusal','delivery-review-republish',
            'delivery-authority-call','delivery-authority-refusal','delivery-authority-republish','delivery-startup',
            'delivery-host-stop','delivery-host-dispose','delivery-catalog-verify','startup-pod',
            'startup-token','startup-file','startup-fingerprints','startup-before-state',
            'startup-first-fetch','startup-after-state','startup-restart-fetch','startup-revoke',
            'startup-denial-probe','startup-republish','startup-pod-cleanup','startup-directory-cleanup'
        )
        $callerIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($stem in $callerStems){foreach($suffix in @('start','completed','failed')){$callerIds.Add($stem+'-'+$suffix) | Out-Null}}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $records=[Collections.Generic.List[string]]::new()
        $previousElapsed=0L; $authStarts=0; $authCompleted=0; $authOpen=$false; $postAuthPhase=0; $candidateDisposals=0
        $deliveryContext=0; $deliveryPhase=0
        $setupOpen=@{'accepted-setup'=$false;'restore'=$false;'runtime-setup'=$false}
        $setupCounts=@{'accepted-setup'=0;'restore'=0;'runtime-setup'=0}
        $repeat=@('accepted-setup-start','accepted-setup-completed','restore-start','restore-completed','runtime-setup-start','runtime-setup-completed','runtime-auth-start','runtime-auth-completed','runtime-final-revoke-start','runtime-final-revoke-completed',
            'runtime-final-history-start','runtime-final-history-completed','runtime-final-authorization-start','runtime-final-authorization-completed',
            'runtime-candidate-dispose-start','runtime-candidate-dispose-completed','runtime-candidate-dispose-failed')
        $repeat+=@($requires.Keys | Where-Object { $_.StartsWith('delivery-', [StringComparison]::Ordinal) })
        for($i=0;$i -lt $lines.Length-1;$i++){
            $line=$lines[$i]
            if($line -cnotmatch '^([1-9][0-9]{0,2}) ([a-z-]+) (0|[1-9][0-9]{0,6})$' -or [int]$Matches[1] -ne $i+1){return Result 'invalid'}
            $id=$Matches[2]
            $elapsed=[long]$Matches[3]
            if($elapsed -gt 3600000 -or $elapsed -lt $previousElapsed){return Result 'invalid'}
            $previousElapsed=$elapsed
            if($callerIds.Contains($id)){$records.Add($line);continue}
            if(!$requires.ContainsKey($id) -or ($requires[$id] -and !$seen.Contains($requires[$id])) -or
                ($seen.Contains($id) -and $id -cnotin $repeat) -or
                ($i -eq 0 -and $id -cne 'durable-entry')){return Result 'invalid'}
            if(($id -ceq 'workload-actions-start' -and $seen.Contains('runtime-auth-start')) -or
                ($id -ceq 'mixed-baseline-prepared' -and $seen.Contains('mixed-preparation-completed'))){return Result 'invalid'}
            foreach($boundary in @('accepted-setup','restore','runtime-setup')){
                if($id -ceq ($boundary+'-start')){
                    if($setupOpen.Values -contains $true -or $setupCounts[$boundary] -ge 16){return Result 'invalid'}
                    if(($boundary -ceq 'accepted-setup' -and $seen.Contains('workload-actions-completed')) -or
                        ($boundary -ceq 'runtime-setup' -and $seen.Contains('identity-actions-completed')) -or
                        ($boundary -ceq 'restore' -and $seen.Contains('identity-actions-completed'))){return Result 'invalid'}
                    if($boundary -ceq 'restore' -and
                        !(($seen.Contains('workload-actions-start') -and !$seen.Contains('workload-actions-completed')) -or
                          ($seen.Contains('identity-actions-start') -and !$seen.Contains('identity-actions-completed')))){return Result 'invalid'}
                    $setupOpen[$boundary]=$true; $setupCounts[$boundary]++
                }elseif($id -ceq ($boundary+'-completed')){
                    if(!$setupOpen[$boundary]){return Result 'invalid'}
                    $setupOpen[$boundary]=$false
                }
            }
            if($id -cin @('workload-actions-completed','identity-actions-start','identity-actions-completed','runtime-auth-start') -and
                $setupOpen.Values -contains $true){return Result 'invalid'}
            if($id -ceq 'runtime-auth-start' -and $seen.Contains('workload-actions-start') -and !$seen.Contains('identity-actions-completed')){return Result 'invalid'}
            if($id -ceq 'runtime-auth-start'){
                if($authOpen -or $authStarts -ge 2 -or $postAuthPhase -notin @(0,1) -or ($authStarts -eq 1 -and !$seen.Contains('initial-delivery-start'))){return Result 'invalid'}
                $authStarts++; $authOpen=$true; $postAuthPhase=0
            }elseif($id -ceq 'runtime-auth-completed'){
                if(!$authOpen){return Result 'invalid'}
                $authOpen=$false; $authCompleted++; $postAuthPhase=1
            }elseif(($id -ceq 'initial-delivery-start' -and $authCompleted -ne 1) -or
                ($id -ceq 'mixed-delivery-start' -and $authCompleted -ne 2)){return Result 'invalid'}
            # Static nested delivery boundaries: two outer scenarios, first/restarted Host, no arbitrary IDs or unbounded repeats.
            if($id -cin @('initial-delivery-start','mixed-delivery-start')){
                if($deliveryPhase -notin @(0,20)){return Result 'invalid'}
                $deliveryContext=if($id -ceq 'initial-delivery-start'){1}else{2}; $deliveryPhase=0
            }
            if($id.StartsWith('delivery-', [StringComparison]::Ordinal)){
                if($deliveryContext -eq 0){return Result 'invalid'}
                $nextDelivery=switch -CaseSensitive ($id){
                    'delivery-provenance-start'{if($deliveryPhase -eq 0){1}else{-1}}
                    'delivery-provenance-completed'{if($deliveryPhase -eq 1){2}else{-1}}
                    'delivery-pod-start'{if($deliveryPhase -eq 2){3}else{-1}}
                    'delivery-pod-completed'{if($deliveryPhase -eq 3){4}else{-1}}
                    'delivery-token-start'{if($deliveryPhase -eq 4){5}else{-1}}
                    'delivery-token-completed'{if($deliveryPhase -eq 5){6}else{-1}}
                    'delivery-host-first-start'{if($deliveryPhase -eq 6){7}else{-1}}
                    'delivery-host-first-built'{if($deliveryPhase -eq 7){22}else{-1}}
                    'delivery-host-first-credential-ready'{if($deliveryPhase -eq 22){23}else{-1}}
                    'delivery-host-first-started'{if($deliveryPhase -eq 23){8}else{-1}}
                    'delivery-first-request-start'{if($deliveryPhase -eq 8){9}elseif($deliveryPhase -eq 14){15}else{-1}}
                    'delivery-first-request-completed'{if($deliveryPhase -eq 9){10}elseif($deliveryPhase -eq 15){16}else{-1}}
                    'delivery-host-first-dispose-start'{if($deliveryPhase -in @(7,22,23,8,9,10)){11}else{-1}}
                    'delivery-host-first-dispose-completed'{if($deliveryPhase -eq 11){12}else{-1}}
                    'delivery-host-restart-start'{if($deliveryPhase -eq 12){13}else{-1}}
                    'delivery-host-restart-built'{if($deliveryPhase -eq 13){24}else{-1}}
                    'delivery-host-restart-credential-ready'{if($deliveryPhase -eq 24){25}else{-1}}
                    'delivery-host-restart-started'{if($deliveryPhase -eq 25){14}else{-1}}
                    'delivery-host-restart-dispose-start'{if($deliveryPhase -in @(13,24,25,14,15,16)){17}else{-1}}
                    'delivery-host-restart-dispose-completed'{if($deliveryPhase -eq 17){18}else{-1}}
                    'delivery-pod-cleanup-start'{if(($deliveryPhase -ge 4 -and $deliveryPhase -le 18) -or $deliveryPhase -in @(22,23,24,25)){19}else{-1}}
                    'delivery-pod-cleanup-completed'{if($deliveryPhase -eq 19){20}else{-1}}
                    'delivery-pod-cleanup-failed'{if($deliveryPhase -eq 19){21}else{-1}}
                    default{-1}
                }
                if($nextDelivery -lt 0){return Result 'invalid'}
                $deliveryPhase=$nextDelivery
            }
            $phaseTransition=switch -CaseSensitive ($id){
                'runtime-final-revoke-start'{@(1,2)};'runtime-final-revoke-completed'{@(2,3)}
                'runtime-final-history-start'{@(3,4)};'runtime-final-history-completed'{@(4,5)}
                'runtime-final-authorization-start'{@(5,6)};'runtime-final-authorization-completed'{@(6,7)}
                default{$null}
            }
            if($null -ne $phaseTransition){
                if($postAuthPhase -ne $phaseTransition[0]){return Result 'invalid'}
                $postAuthPhase=$phaseTransition[1]
            }elseif($id -ceq 'runtime-candidate-dispose-start'){
                if($postAuthPhase -eq 8 -or $candidateDisposals -ge 2){return Result 'invalid'}
                $candidateDisposals++;$postAuthPhase=8
            }elseif($id -cin @('runtime-candidate-dispose-completed','runtime-candidate-dispose-failed')){
                if($postAuthPhase -ne 8){return Result 'invalid'}
                $postAuthPhase=0
            }
            $cleanupIds=@('inner-cleanup-start','inner-cleanup-completed','inner-cleanup-failed','namespace-cleanup-start','namespace-absence-confirmed',
                'namespace-cleanup-failed','catalog-dispose-start','catalog-dispose-completed','catalog-dispose-failed')
            if($authOpen -and $id -cnotin (@('runtime-auth-start','runtime-candidate-dispose-start','runtime-candidate-dispose-completed','runtime-candidate-dispose-failed')+$cleanupIds)){return Result 'invalid'}
            $cleanupStarted=$seen.Contains('inner-cleanup-start') -or $seen.Contains('namespace-cleanup-start') -or $seen.Contains('catalog-dispose-start')
            if($id -ceq 'inner-cleanup-start' -and $postAuthPhase -eq 8){return Result 'invalid'}
            if(($cleanupStarted -or $seen.Contains('runtime-candidate-dispose-failed')) -and $id -cnotin $cleanupIds){return Result 'invalid'}
            if($seen.Contains('catalog-dispose-start') -and $id -cnotin @('catalog-dispose-completed','catalog-dispose-failed','inner-cleanup-completed','inner-cleanup-failed')){return Result 'invalid'}
            if(($id -ceq 'namespace-absence-confirmed' -and $seen.Contains('namespace-cleanup-failed')) -or
                ($id -ceq 'namespace-cleanup-failed' -and $seen.Contains('namespace-absence-confirmed')) -or
                ($id -ceq 'catalog-dispose-completed' -and $seen.Contains('catalog-dispose-failed')) -or
                ($id -ceq 'catalog-dispose-failed' -and $seen.Contains('catalog-dispose-completed')) -or
                ($id -ceq 'inner-cleanup-completed' -and ($seen.Contains('inner-cleanup-failed') -or !$seen.Contains('catalog-dispose-completed') -or
                    ($seen.Contains('namespace-create-completed') -and !$seen.Contains('namespace-absence-confirmed')))) -or
                ($id -ceq 'inner-cleanup-failed' -and ($seen.Contains('inner-cleanup-completed') -or
                    (!$seen.Contains('namespace-cleanup-failed') -and !$seen.Contains('catalog-dispose-failed'))))){return Result 'invalid'}
            $seen.Add($id) | Out-Null; $records.Add($line)
        }
        $ids=@($records | ForEach-Object {($_ -split ' ')[1]})
        if(!(Test-QualificationCallerSequence $ids $callerStems)){return Result 'invalid'}
        if($partial){return Result 'partial-prefix' $records.ToArray()}
        return Result 'valid' $records.ToArray()
    }catch{return Result 'io-failure'}
}
function Format-QualificationJournal($Journal) {
    'Diagnostic journal: '+$Journal.Status
    if($Journal.Status -cin @('valid','partial-prefix')){
        if($Journal.Status -ceq 'partial-prefix'){'Diagnostic journal incomplete: final unterminated fragment discarded; FAILURE'}
        'Diagnostic managed elapsed origin: journal-initialization; excludes-build-startup; clock=monotonic'
        foreach($line in $Journal.Records){'Diagnostic progress: '+$line}
    }
    'Diagnostic inner namespace: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ namespace-absence-confirmed [0-9]{1,7}$'){'confirmed'}else{'unknown'})
    'Diagnostic inner catalog: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ catalog-dispose-completed [0-9]{1,7}$'){'confirmed'}else{'unknown'})
    'Diagnostic inner cleanup outcome: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ inner-cleanup-completed [0-9]{1,7}$'){'completed'}elseif($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ inner-cleanup-failed [0-9]{1,7}$'){'failed'}else{'unknown'})
}

# Only this finite workload channel is retained from child output, including on nonzero exit.
function Read-QualificationWorkloadEvidence([string]$Mode,[string]$Output,[string]$ErrorOutput) {
    $invalid=[pscustomobject]@{Status='invalid';Records=@()}
    $oversized=[pscustomobject]@{Status='oversized';Records=@()}
    if($Mode -cnotin @('kernel','configured')){return $invalid}
    if($Output.Length+$ErrorOutput.Length -gt 1048576){return $oversized}
    $passes=if($Mode -ceq 'kernel'){
        @('PASS kernel-K01-create-mtls',
          'PASS kernel-K02-occupied-no-adoption',
          'PASS kernel-K03-foreign-owner',
          'PASS kernel-K04-keyed-update-preservation-removal',
          'PASS kernel-K05-no-attempt-template-annotation',
          'PASS kernel-K06-owned-drift',
          'PASS kernel-K07-real-RV-race-ambiguous-422-no-retry',
          'PASS kernel-K08-saved-UID-refusal',
          'PASS kernel-K09-one-accepted-response-loss',
          'PASS kernel-K10-stateless-reload-observe-zero-writes',
          'PASS kernel-K11-missing-marker-no-success',
          'PASS kernel-K12-wrong-marker-no-success',
          'PASS kernel-K13-absence-no-replay',
          'PASS kernel-K14-replacement-UID-no-success',
          'PASS kernel-K15-unavailable-not-nonexecution',
          'PASS kernel-K16-create-bearer',
          'PASS kernel-K17-invalid-bearer',
          'PASS kernel-K18-production-Online-revocation-no-fallback',
          'PASS kernel-K19-production-system-trust-refusal',
          'PASS kernel-K20-client-key-mismatch',
          'PASS kernel-K21-scoped-secret-exclusion',
          'PASS kernel-K22-new-factory-hostname-refusal',
          'PASS kernel-K23-new-factory-custom-root-refusal',
          'PASS kernel-K24-real-UID-race-replacement-preserved',
          'PASS kernel-K25-limit-only-template-absence',
          'PASS kernel-K26-ports-probes-API-defaults-readback',
          'PASS kernel-K27-owned-probe-action-port-change-removal-preserves-foreign',
          'PASS kernel-K28-uncertain-patch-observes-exact-probes-without-second-write',
          'PASS kernel-K29-server-defaulted-protocol-and-probe-fields-confirm',
          'PASS kernel-cleanup-ownership',
          'PASS kernel-namespace-absence-after-UID-delete')
    }else{
        @('PASS configured-C01-exact-two-documents-prepared',
          'PASS configured-C02-confirmed-stable-identity',
          'PASS configured-C03-missing-publication-no-Deployment',
          'PASS configured-C04-explicit-publication',
          'PASS configured-C05-production-generated-create-confirmed',
          'PASS configured-C06-real-projected-token-HTTPS-two-exact-documents-primary',
          'PASS configured-C07-complete-saved-projection-and-Pod-identity',
          'PASS configured-R01-controller-produced-available-minReadySeconds-exact-UID',
          'PASS configured-R04-audited-resume-renews-observation-only',
          'PASS configured-R06-native-public-expired-resume-observation-only',
          'PASS configured-R05-native-public-wait-whole-set-zero-writes',
          'PASS configured-A01-native-wait-exact-retirement-proof-and-Applied',
          'PASS configured-B01-second-exact-configured-generation-submitted',
          'PASS configured-B02-native-batch-failure-preserves-another-fresh-success',
          'PASS configured-B03-native-batch-complete-fresh-exact-generations-zero-extra-writes',
          'PASS configured-B04-native-batch-replay-history-is-not-fresh-ready',
          'PASS configured-C08-restarted-Host-fresh-Pod-same-bootstrap',
          'PASS configured-A02-runtime-retrieval-after-Applied-and-Host-restart',
          'PASS configured-R02-reconstructed-observer-original-attempt-deadline-zero-writes',
          'PASS configured-C09-later-document-revoke-no-primary',
          'PASS configured-R03-startup-refusal-is-current-incomplete-despite-historical-Ready',
          'PASS configured-C10-revoked-admission-and-no-publication-repair',
          'PASS configured-C11-Applied-preserves-publications-and-stable-identities',
          'PASS configured-A03-revocation-keeps-historical-completion-without-regrant',
          'PASS configured-C12-Pod-defaulted-resource-requests',
          'PASS configured-C13-configured-Deployment-Pod-ports-probes-defaults',
          'PASS configured-R07-failing-configured-readiness-probe-blocks-fresh-Ready-Applied',
          'PASS configured-U01-public-update-remains-closed',
          'PASS configured-U02-new-exact-documents-prepared',
          'PASS configured-U03-stable-account-original-proof-new-generation-use',
          'PASS configured-U04-new-rights-before-patch-old-rights-retained',
          'PASS configured-U05-target-bootstrap-new-exact-revisions-stable-binding',
          'PASS configured-U06-conditional-configured-patch-response-readback-lost-response-recovery',
          'PASS configured-U07-sidecar-foreign-metadata-and-api-defaulting-preserved',
          'PASS configured-U08-runtime-new-revision-delivery-old-permissions-and-Applied-boundary',
          'PASS configured-owned-namespace-absent',
          'PASS configured-owned-catalog-cleanup')
    }
    $stages=@('entry','namespace-create','namespace-created','catalog-init','accepted-plan','host-build','host-start',
        'first-submit','uncertain-submit','cli-submit','pod-startup','readback','prepare-update','cleanup-ownership',
        'cleanup-namespace','cleanup-catalog','completed','unknown')
    if($Mode -ceq 'configured'){$stages+=@('update-target-envelope','update-import','update-image-baseline','update-plan')}
    $outcomes=@('Confirmed','Refused','Conflict','Uncertain','CanceledBeforeDispatch','ObservationUnavailable','NotConfirmed','unknown')
    $codes=@('response-confirmed','observation-confirmed','evidence-not-confirmed','response-unconfirmed','invalid-input',
        'connection-unavailable','dispatch-uncertain','occupied-name','owned-state-conflict','uid-conflict','api-conflict',
        'api-access-denied','action-canceled','managed-object-missing','action-budget','precondition-limit','object-absent','observation-unavailable','unknown')
    $rpcCodes=@('OK','Cancelled','Unknown','InvalidArgument','DeadlineExceeded','NotFound','AlreadyExists','PermissionDenied',
        'ResourceExhausted','FailedPrecondition','Aborted','OutOfRange','Unimplemented','Internal','Unavailable','DataLoss','Unauthenticated','unknown')
    $records=[Collections.Generic.List[string]]::new();$entry=$false;$hasFailure=$false
    foreach($line in (($Output+"`n"+$ErrorOutput) -split "`r?`n")){
        if($line -cin $passes){$records.Add($line)}
        elseif($line.StartsWith('Q243 ',[StringComparison]::Ordinal)){
            if($line.Length -gt 160){return $invalid}
            $parts=$line.Split(' ')
            if($parts.Count -lt 4 -or $parts[1] -cne $Mode){return $invalid}
            if($parts[2] -cin @('k14','k24')){
                $replacementPhases=if($parts[2] -ceq 'k14'){@('initial-create','uid-delete','absence-poll','replacement-create','old-uid-observe','assertion')}else{@('uid-delete','absence-poll','replacement-create')}
                if($Mode -cne 'kernel' -or $parts.Count -lt 5 -or $parts[3] -cnotin $replacementPhases){return $invalid}
                $phase=$parts[3];$state=$parts[4];$shape=$false
                if($parts.Count -eq 5){
                    $shape=$state -ceq 'start' -or
                        ($phase -cin @('uid-delete','replacement-create') -and $state -ceq 'completed') -or
                        ($phase -ceq 'assertion' -and $state -cin @('passed','failed'))
                }elseif($parts.Count -eq 9){
                    $shape=$phase -cin @('initial-create','old-uid-observe') -and $state -ceq 'result' -and
                        $parts[5] -cin $outcomes -and $parts[6] -cin $codes -and $parts[7] -cmatch '^([0-3]|unknown)$' -and
                        $parts[8] -cin @('true','false')
                }elseif($parts.Count -eq 7){
                    if($phase -ceq 'absence-poll' -and $state -ceq 'result'){
                        $shape=$parts[5] -cin @('true','false') -and $parts[6] -cmatch '^([0-9]|[1-9][0-9]|[1-5][0-9]{2}|60[01])$'
                    }elseif($state -ceq 'exception'){
                        $shape=$parts[5] -cin @('http','canceled','null-reference','contract','invalid-operation','unknown') -and
                            $parts[6] -cmatch '^([1-5][0-9]{2}|unknown)$' -and ($parts[5] -ceq 'http' -or $parts[6] -ceq 'unknown')
                    }
                }
                if(!$shape){return $invalid}
                if($state -ceq 'exception' -or ($phase -ceq 'assertion' -and $state -ceq 'failed')){$hasFailure=$true}
                $records.Add('Diagnostic workload: '+$line.Substring(5))
            }elseif($parts.Count -eq 4 -and $Mode -ceq 'configured' -and $parts[2] -ceq 'rpc'){
                if($parts[3] -cnotin $rpcCodes){return $invalid}
                $records.Add('Diagnostic workload: configured rpc '+$parts[3])
            }elseif($parts.Count -eq 4){
                $stage=$parts[2];$state=$parts[3]
                $case=if($Mode -ceq 'kernel'){$stage -cmatch '^K(0[1-9]|1[0-9]|2[0-9])$'}else{$stage -cmatch '^(C(0[1-9]|1[0-3])|R0[1-7]|B0[1-4]|A0[1-3])$'}
                if(($stage -cnotin $stages -and !$case) -or $state -cnotin @('start','passed','failed','completed','skipped')){return $invalid}
                if($stage -ceq 'entry' -and $state -ceq 'start'){$entry=$true}
                if($state -ceq 'failed'){$hasFailure=$true}
                $records.Add('Diagnostic workload: '+$Mode+' '+$stage+' '+$state)
            }elseif($parts.Count -eq 6 -and $parts[2] -ceq 'result'){
                if($parts[3] -cnotin $outcomes -or $parts[4] -cnotin $codes -or $parts[5] -cnotmatch '^([0-3]|unknown)$'){return $invalid}
                $records.Add('Diagnostic workload: '+$Mode+' result '+$parts[3]+' '+$parts[4]+' '+$parts[5])
            }else{return $invalid}
        }
        if($records.Count -gt 256){return $oversized}
    }
    if(!$entry){return [pscustomobject]@{Status='missing';Records=@()}}
    return [pscustomobject]@{Status='valid';Records=@($records.ToArray());HasFailure=$hasFailure}
}

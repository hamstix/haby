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
        if($bytes[$length-1] -ne 10){return Result 'partial'}
        # ASCII only; replacement decoding must not turn malformed bytes into accepted IDs.
        for($i=0;$i -lt $length;$i++){if($bytes[$i] -gt 127 -or $bytes[$i] -eq 0){return Result 'invalid'}}
        $lines=[Text.Encoding]::ASCII.GetString($bytes,0,$length).Split("`n")
        if($lines.Length-1 -gt 256){return Result 'overflow'}
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
            'namespace-cleanup-start'='namespace-create-completed';'namespace-absence-confirmed'='namespace-cleanup-start'
            'catalog-dispose-start'='catalog-init-completed';'catalog-dispose-completed'='catalog-dispose-start'
        }
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $records=[Collections.Generic.List[string]]::new()
        $previousElapsed=0L; $authStarts=0; $authCompleted=0; $authOpen=$false; $postAuthPhase=0; $candidateDisposals=0
        $setupOpen=@{'accepted-setup'=$false;'restore'=$false;'runtime-setup'=$false}
        $setupCounts=@{'accepted-setup'=0;'restore'=0;'runtime-setup'=0}
        $repeat=@('accepted-setup-start','accepted-setup-completed','restore-start','restore-completed','runtime-setup-start','runtime-setup-completed','runtime-auth-start','runtime-auth-completed','runtime-final-revoke-start','runtime-final-revoke-completed',
            'runtime-final-history-start','runtime-final-history-completed','runtime-final-authorization-start','runtime-final-authorization-completed',
            'runtime-candidate-dispose-start','runtime-candidate-dispose-completed','runtime-candidate-dispose-failed')
        for($i=0;$i -lt $lines.Length-1;$i++){
            $line=$lines[$i]
            if($line -cnotmatch '^([1-9][0-9]{0,2}) ([a-z-]+) (0|[1-9][0-9]{0,6})$' -or [int]$Matches[1] -ne $i+1){return Result 'invalid'}
            $id=$Matches[2]
            $elapsed=[long]$Matches[3]
            if($elapsed -gt 3600000 -or $elapsed -lt $previousElapsed){return Result 'invalid'}
            $previousElapsed=$elapsed
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
        return Result 'valid' $records.ToArray()
    }catch{return Result 'io-failure'}
}
function Format-QualificationJournal($Journal) {
    'Diagnostic journal: '+$Journal.Status
    if($Journal.Status -ceq 'valid'){
        'Diagnostic managed elapsed origin: journal-initialization; excludes-build-startup; clock=monotonic'
        foreach($line in $Journal.Records){'Diagnostic progress: '+$line}
    }
    'Diagnostic inner namespace: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ namespace-absence-confirmed [0-9]{1,7}$'){'confirmed'}else{'unknown'})
    'Diagnostic inner catalog: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ catalog-dispose-completed [0-9]{1,7}$'){'confirmed'}else{'unknown'})
    'Diagnostic inner cleanup outcome: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ inner-cleanup-completed [0-9]{1,7}$'){'completed'}elseif($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ inner-cleanup-failed [0-9]{1,7}$'){'failed'}else{'unknown'})
}

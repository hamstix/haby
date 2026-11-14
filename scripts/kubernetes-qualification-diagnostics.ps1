# This channel accepts only fixed progress IDs. It never reads child output or credentials.
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
            'initial-delivery-start'='durable-suite-start';'mixed-delivery-start'='durable-suite-start'
            'mixed-preparation-completed'='mixed-delivery-start';'mixed-documents-completed'='mixed-preparation-completed'
            'mixed-identity-completed'='mixed-documents-completed';'mixed-publication-completed'='mixed-identity-completed'
            'mixed-delivery-completed'='mixed-publication-completed'
            'namespace-cleanup-start'='namespace-create-completed';'namespace-absence-confirmed'='namespace-cleanup-start'
            'catalog-dispose-start'='catalog-init-completed';'catalog-dispose-completed'='catalog-dispose-start'
        }
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $records=[Collections.Generic.List[string]]::new()
        for($i=0;$i -lt $lines.Length-1;$i++){
            $line=$lines[$i]
            if($line -cnotmatch '^([1-9][0-9]{0,2}) ([a-z-]+)$' -or [int]$Matches[1] -ne $i+1){return Result 'invalid'}
            $id=$Matches[2]
            if(!$requires.ContainsKey($id) -or ($requires[$id] -and !$seen.Contains($requires[$id])) -or
                ($seen.Contains($id) -and $id -cne 'runtime-auth-start') -or
                ($i -eq 0 -and $id -cne 'durable-entry')){return Result 'invalid'}
            if(($seen.Contains('namespace-cleanup-start') -or $seen.Contains('catalog-dispose-start')) -and
                $id -cin @('runtime-auth-start','initial-delivery-start','mixed-delivery-start','durable-suite-start','namespace-create-start','namespace-create-completed',
                    'mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed','mixed-delivery-completed')){return Result 'invalid'}
            if($seen.Contains('catalog-dispose-start') -and $id -cne 'catalog-dispose-completed'){return Result 'invalid'}
            $seen.Add($id) | Out-Null; $records.Add($line)
        }
        return Result 'valid' $records.ToArray()
    }catch{return Result 'io-failure'}
}
function Format-QualificationJournal($Journal) {
    'Diagnostic journal: '+$Journal.Status
    if($Journal.Status -ceq 'valid'){foreach($line in $Journal.Records){'Diagnostic progress: '+$line}}
    'Diagnostic inner namespace: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ namespace-absence-confirmed$'){'confirmed'}else{'unknown'})
    'Diagnostic inner catalog: '+$(if($Journal.Status -ceq 'valid' -and $Journal.Records -cmatch '^[0-9]+ catalog-dispose-completed$'){'confirmed'}else{'unknown'})
}

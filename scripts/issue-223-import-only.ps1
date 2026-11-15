param([string]$ProductRoot, [string]$EvidencePath, [switch]$ControlsOnly)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false

# Temporary token-level sanitizer, not a native-error classifier.
# Every published word is fixed vocabulary; all other input is redacted.
function ConvertTo-ImportExcerpt([AllowNull()][string]$Text) {
    if($null -eq $Text){$Text=''}
    $inputTruncated=$Text.Length -gt 8192
    $bounded=$Text.Substring(0,[Math]::Min(8192,$Text.Length))
    $bounded=[regex]::Replace($bounded,'(?im)^.*(?:authorization|password|passwd|credential|secret|token|api[-_ ]?key|private[-_ ]?key|connection[-_ ]?string)\s*[:=].*$',' [redacted] ')
    $bounded=[regex]::Replace($bounded,'(?s)-----BEGIN.*?-----END[^\r\n]*',' [redacted] ')
    $bounded=[regex]::Replace($bounded,'["''][^"''\r\n]*["'']',' [redacted] ')
    $bounded=[regex]::Replace($bounded,'(?i)(?:[a-z][a-z0-9+.-]*://|[a-z]:[\\/]|/|\\\\)[^\s]+',' [redacted] ')
    $bounded=[regex]::Replace($bounded,'(?i)\b(?:sha256:)?[a-f0-9]{16,}\b|\b[0-9]+(?:\.[0-9]+){1,}\b|\b[a-z0-9._%+-]+@[a-z0-9.-]+\b',' [redacted] ')
    $vocabulary='ctr k3s rpc error code desc failed failure unable cannot to the a an is was be been not no found missing invalid unsupported unrecognized unknown unexpected unavailable denied permission unauthorized forbidden already exists content digest image images archive import importing unpack unpacking resolve resolving reference media type format manifest index layer layers blob blobs platform platforms architecture operating system operating-system namespace snapshot snapshotter snapshotters containerd runtime daemon server client connection connect connecting closed refused reset timeout timed out deadline exceeded context canceled cancelled eof end of file read reading write writing open opening stat lstat extract extracting tar gzip compressed compression decompress decompressing checksum mismatch corrupt corrupted malformed truncated size offset length expected actual duplicate incompatible execute executable binary exec input output argument arguments option options flag flags requires required operation resource exhausted internal unimplemented data loss notfound invalidargument deadlineexceeded permissiondenied unauthenticated resourceexhausted failedprecondition outofrange dataloss aborted sha256 socket unix tcp http https port address network host service busy locked lock too many requests broken pipe storage space left on device read-only filesystem directory directories regular symlink symbolic link links path invalid-argument'.Split(' ',[StringSplitOptions]::RemoveEmptyEntries)
    $allowed=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($word in $vocabulary){[void]$allowed.Add($word)}
    $safeLines=[Collections.Generic.List[string]]::new()
    foreach($line in ($bounded -split '\r?\n')){
        $words=[Collections.Generic.List[string]]::new()
        foreach($match in [regex]::Matches($line,'[A-Za-z][A-Za-z0-9_-]*|[0-9]+')){
            $word=$match.Value.ToLowerInvariant()
            $safe=if($allowed.Contains($word)){$word}else{'[redacted]'}
            if($safe -cne '[redacted]' -or $words.Count -eq 0 -or $words[$words.Count-1] -cne '[redacted]'){$words.Add($safe)}
        }
        if($words.Count){$safeLines.Add(($words -join ' '))}
    }
    $excerpt=$safeLines -join "`n"; $outputTruncated=$excerpt.Length -gt 4096
    if($outputTruncated){$excerpt=$excerpt.Substring(0,4096)}
    return [pscustomobject]@{text=$excerpt;inputTruncated=$inputTruncated;excerptTruncated=$outputTruncated;inputCharactersRetained=[Math]::Min(8192,$Text.Length)}
}
if($ControlsOnly){return}
if(!$IsLinux){throw 'Import-only requires Linux Docker; no runtime substitution.'}
$ProductRoot=[IO.Path]::GetFullPath($ProductRoot); $EvidencePath=[IO.Path]::GetFullPath($EvidencePath)
if(Test-Path -LiteralPath $EvidencePath){throw 'Fresh evidence path required.'}
$source='47dcc8c3dc2e0fa720230a480b36340a834c4863'; $tree='1df9e045ab1e1c872ae6fa134ec48ee3c3dce6ab'
$orchestration=$env:HABY_IMPORT_ORCHESTRATION_SHA
if($orchestration -cnotmatch '^[a-f0-9]{40}$'){throw 'Reviewed orchestration identity required.'}
if($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/codex/ci-223-import-only' -or $env:GITHUB_RUN_ATTEMPT -cne '1'){throw 'One approved topic dispatch only.'}
$orchestrationRoot=Split-Path $PSScriptRoot -Parent
if((& git -C $orchestrationRoot rev-parse HEAD) -cne $orchestration -or (& git -C $orchestrationRoot status --porcelain)){throw 'Orchestration checkout mismatch.'}
Set-Location -LiteralPath $ProductRoot
if((& git rev-parse HEAD) -cne $source -or (& git rev-parse 'HEAD^{tree}') -cne $tree -or (& git status --porcelain)){throw 'Frozen product identity mismatch.'}
. ./scripts/kubernetes-qualification-process.ps1
$run='haby179-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()); $private=[IO.Path]::GetFullPath((Join-Path $tempRoot $run))
$consumerImage='localhost/haby-runtime-consumer:'+$run
$nodeImage='rancher/k3s@sha256:10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248'
$privateOwned=$false; $imageAttempted=$false; $launchAttempted=$false; $consumerImageId=$null
$stage='setup'; $primaryFailed=$false; $cleanupFailed=$false; $cleanupInProgress=$false
$evidence=[ordered]@{productSource=$source;productTree=$tree;orchestrationSource=$orchestration;scope='import-only-no-acceptance-credit';utc=[DateTimeOffset]::UtcNow.ToString('O');nodeImage=$nodeImage;primary=[ordered]@{stage='setup';status='not-reached';nativeExit=$null;processKind=$null};cleanup=[ordered]@{container='not-created';stop='not-required';anonymousVolumes='not-created';image='not-created';privateDirectory='not-created'};import=[ordered]@{attempts=0;stdout=$null;stderr=$null}}
function Call([string]$File,[string[]]$Arguments,[int]$Seconds=20){return Invoke-QualificationProcess -File (Resolve-QualificationExecutable $File) -Arguments $Arguments -TimeoutSeconds $Seconds}
function Checked([string]$File,[string[]]$Arguments,[int]$Seconds=20){
    $result=Call $File $Arguments $Seconds
    if($result.ExitCode -isnot [int]){throw 'Native exit is not Int32.'}
    if($result.ExitCode -ne 0){if(!$cleanupInProgress){$evidence.primary.nativeExit=$result.ExitCode};throw 'Bounded operation failed.'}
    return $result.Output
}
function Docker([string[]]$Arguments,[int]$Seconds=20){return Checked 'docker' $Arguments $Seconds}
function Digest([string]$Path){return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Save-SafeEvidence {
    New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($EvidencePath)) | Out-Null
    [IO.File]::WriteAllText($EvidencePath,($evidence|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
}
try {
    New-Item -ItemType Directory -Path $private | Out-Null; $privateOwned=$true
    $null=Checked 'chmod' @('700',$private)
    $evidence.os=[Runtime.InteropServices.RuntimeInformation]::OSDescription
    $evidence.architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $version=(Docker @('--version')).Trim(); $evidence.dockerVersion=if($version -cmatch '^Docker version [0-9.]+, build [a-zA-Z0-9]+$'){$version}else{'unavailable'}
    $sdk=(Checked 'dotnet' @('--version')).Trim(); $evidence.sdk=if($sdk -cmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][a-zA-Z0-9.-]+)?$'){$sdk}else{'unavailable'}
    $evidence.dockerfileSha256=Digest 'examples/Haby.RuntimeStartup.Consumer/Dockerfile'
    $stage='publish'; $consumerOutput=Join-Path $private 'consumer'
    $null=Checked 'dotnet' @('publish','examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj','-c','Release','-o',$consumerOutput) 60
    $evidence.consumerAssemblySha256=Digest (Join-Path $consumerOutput 'Haby.RuntimeStartup.Consumer.dll')
    $stage='build'; $imageAttempted=$true
    $null=Checked 'docker' @('build','--label',"haby.qualification=$run",'--tag',$consumerImage,'--file','examples/Haby.RuntimeStartup.Consumer/Dockerfile',$consumerOutput) 60
    $consumerImageId=(Docker @('image','inspect','--format','{{.Id}}',$consumerImage)).Trim()
    if($consumerImageId -cnotmatch '^sha256:[a-f0-9]{64}$'){throw 'Built image identity invalid.'}
    $evidence.consumerImageId=$consumerImageId
    $base=Call 'docker' @('image','inspect','--format','{{.Id}}','mcr.microsoft.com/dotnet/runtime:10.0')
    $evidence.publicBaseTagObservedImageId=if($base.ExitCode -eq 0 -and $base.Output.Trim() -cmatch '^sha256:[a-f0-9]{64}$'){$base.Output.Trim()}else{'unavailable'}
    $stage='save'; $archive=Join-Path $private 'consumer.tar'
    $null=Docker @('image','save','--output',$archive,$consumerImage) 60
    $evidence.archiveSha256=Digest $archive; $evidence.archiveBytes=(Get-Item -LiteralPath $archive).Length
    $stage='launch'; $launchAttempted=$true
    $null=Checked 'docker' @('run','-d','--privileged','--name',$run,'--label',"haby.qualification=$run",'-p','127.0.0.1:16443:6443',$nodeImage,'server','--disable','traefik','--disable','servicelb','--disable','metrics-server','--disable','coredns','--disable','local-storage','--egress-selector-mode','disabled','--tls-san','127.0.0.1','--write-kubeconfig-mode','600') 180
    $stage='readiness'; $timer=[Diagnostics.Stopwatch]::StartNew(); $ready=$false
    while($timer.Elapsed.TotalSeconds -lt 120){
        $remaining=[Math]::Max(1,[int][Math]::Ceiling(120-$timer.Elapsed.TotalSeconds))
        $probe=Call 'docker' @('exec',$run,'/bin/kubectl','--kubeconfig','/etc/rancher/k3s/k3s.yaml','--request-timeout=3s','get','--raw=/readyz') ([Math]::Min(20,$remaining))
        if($probe.ExitCode -eq 0){
            $nodes=Call 'docker' @('exec',$run,'/bin/kubectl','--kubeconfig','/etc/rancher/k3s/k3s.yaml','--request-timeout=3s','get','nodes','-o','json') ([Math]::Min(20,$remaining))
            if($nodes.ExitCode -eq 0){$list=$nodes.Output|ConvertFrom-Json;if(@($list.items|Where-Object {@($_.status.conditions|Where-Object {$_.type -ceq 'Ready' -and $_.status -ceq 'True'}).Count -eq 1}).Count -eq 1){$ready=$true;break}}
        }
        Start-Sleep -Milliseconds 200
    }
    if(!$ready){throw 'Existing readiness deadline exceeded.'}
    $stage='copy'; $null=Docker @('cp',$archive,"${run}:/tmp/haby-consumer.tar") 60
    $stage='readback'; $readback=Join-Path $private 'consumer-readback.tar'
    $null=Docker @('cp',"${run}:/tmp/haby-consumer.tar",$readback) 60
    $evidence.readbackSha256=Digest $readback; $evidence.readbackBytes=(Get-Item -LiteralPath $readback).Length
    if($evidence.readbackSha256 -cne $evidence.archiveSha256 -or $evidence.readbackBytes -ne $evidence.archiveBytes){throw 'Archive identity mismatch; import refused.'}
    $stage='import'; $evidence.import.attempts=1
    $result=Call 'docker' @('exec',$run,'/bin/k3s','ctr','images','import','/tmp/haby-consumer.tar') 60
    if($result.ExitCode -isnot [int]){throw 'Native exit is not Int32.'}
    $evidence.primary.nativeExit=$result.ExitCode
    # Freeze meaningful sanitized excerpts before ANY ephemeral cleanup.
    $evidence.import.stdout=ConvertTo-ImportExcerpt $result.Output; $evidence.import.stderr=ConvertTo-ImportExcerpt $result.Error
    foreach($stream in @('Output','Error')){
        $raw=[string]$result.$stream; $bounded=$raw.Substring(0,[Math]::Min(8192,$raw.Length))
        [IO.File]::WriteAllText((Join-Path $private ($stream+'.private.txt')),$bounded,[Text.UTF8Encoding]::new($false))
    }
    $evidence.primary.status=if($result.ExitCode -eq 0){'succeeded'}else{'failed'}; $evidence.primary.processKind='exited'
    if($result.ExitCode -ne 0){$primaryFailed=$true}
    # STOP here even on success. No harness/Host/PG/application object follows.
} catch {
    $primaryFailed=$true; $evidence.primary.status='failed'
    $diagnostic=Get-QualificationProcessDiagnostic $_.Exception
    $evidence.primary.processKind=$diagnostic.Kind; $evidence.primary.processElapsedMilliseconds=$diagnostic.ElapsedMilliseconds
} finally {
    $evidence.primary.stage=$stage
    try{Save-SafeEvidence}catch{$cleanupFailed=$true}
    $cleanupInProgress=$true
    if($launchAttempted){
        try{
            $inspection=Call 'docker' @('inspect',$run)
            if($inspection.ExitCode -ne 0){
                if((Docker @('ps','--all','--quiet','--filter',"name=$run")).Trim()){throw 'Partial container inaccessible.'}
                $evidence.cleanup.container='confirmed-absent-after-attempt';$evidence.cleanup.anonymousVolumes='unconfirmed-no-mount-inventory';$cleanupFailed=$true
            }else{
                $object=($inspection.Output|ConvertFrom-Json)[0]
                if($object.Config.Labels.'haby.qualification' -cne $run -or $object.Id -cnotmatch '^[a-f0-9]{64}$'){throw 'Container ownership mismatch.'}
                if(@($object.Mounts|Where-Object Type -ne 'volume').Count){throw 'Foreign mount; removal refused.'}
                $id=$object.Id;$volumes=@($object.Mounts|ForEach-Object Name)
                try{$null=Docker @('stop',$id);$evidence.cleanup.stop='succeeded'}catch{$cleanupFailed=$true;$evidence.cleanup.stop='failed'}
                $null=Docker @('remove','--force','--volumes',$id)
                if((Docker @('ps','--all','--quiet','--filter',"id=$id")).Trim()){throw 'Owned container remains.'}
                $evidence.cleanup.container='confirmed-absent'
                foreach($volume in $volumes){if(@((Docker @('volume','list','--quiet','--filter',"name=$volume")) -split '\r?\n'|Where-Object {$_ -ceq $volume}).Count){throw 'Owned volume remains.'}}
                $evidence.cleanup.anonymousVolumes='confirmed-absent';$evidence.cleanup.anonymousVolumeCount=$volumes.Count
            }
        }catch{$cleanupFailed=$true;if($evidence.cleanup.container -cnotmatch '^confirmed-absent'){$evidence.cleanup.container='failed-or-unconfirmed'};$evidence.cleanup.anonymousVolumes='failed-or-unconfirmed'}
    }
    if($imageAttempted){
        try{
            $inspection=Call 'docker' @('image','inspect',$consumerImage)
            if($inspection.ExitCode -ne 0){
                if((Docker @('image','ls','--quiet','--no-trunc','--filter',"reference=$consumerImage")).Trim()){throw 'Partial image inaccessible.'}
                if((Docker @('image','ls','--quiet','--no-trunc','--filter',"label=haby.qualification=$run")).Trim()){throw 'Unconfirmed run-labeled image remains.'}
                $evidence.cleanup.image='confirmed-absent-after-attempt'
            }else{
                $owned=($inspection.Output|ConvertFrom-Json)[0]
                if($owned.Config.Labels.'haby.qualification' -cne $run -or $owned.Id -cnotmatch '^sha256:[a-f0-9]{64}$' -or ($consumerImageId -and $owned.Id -cne $consumerImageId)){throw 'Image ownership mismatch.'}
                $null=Docker @('image','rm',$owned.Id)
                if((Docker @('image','ls','--quiet','--no-trunc','--filter',"reference=$consumerImage")).Trim()){throw 'Owned image remains.'}
                $evidence.cleanup.image='confirmed-absent'
            }
        }catch{$cleanupFailed=$true;$evidence.cleanup.image='failed-or-unconfirmed'}
    }
    if($privateOwned){
        try{
            if([IO.Path]::GetDirectoryName($private) -cne $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -or (Split-Path $private -Leaf) -cne $run -or ((Get-Item -LiteralPath $private).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Private ownership mismatch.'}
            Remove-Item -LiteralPath $private -Recurse -Force
            if(Test-Path -LiteralPath $private){throw 'Private directory remains.'}
            $evidence.cleanup.privateDirectory='confirmed-absent'
        }catch{$cleanupFailed=$true;$evidence.cleanup.privateDirectory='failed-or-unconfirmed'}
    }
    $evidence.cleanupFailed=$cleanupFailed
    try{Save-SafeEvidence}catch{$cleanupFailed=$true}
}
if($primaryFailed -or $cleanupFailed){throw 'Import-only operation or cleanup failed; inspect sanitized evidence.'}
Write-Output 'Single import-only attempt completed; no product acceptance credit.'

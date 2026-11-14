param(
    [ValidateSet('wslc','docker')][string]$Runtime='wslc',
    [string]$EvidencePath='',
    [string]$EvidenceId=([Guid]::NewGuid().ToString('N')),
    [switch]$ProbeStockTls,
    [switch]$DeploymentKernel,
    [switch]$DurableActions,
    [ValidateRange(1,120)][int]$CommandTimeoutSeconds=20,
    [ValidateRange(1,300)][int]$ReadinessTimeoutSeconds=120,
    [scriptblock]$CommandRunner
)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. (Join-Path $PSScriptRoot 'kubernetes-qualification-process.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-diagnostics.ps1')
Set-Location (Split-Path $PSScriptRoot -Parent)
if(!$EvidencePath){$EvidencePath=Join-Path '.artifacts' ($EvidenceId+'.txt')}
if(Test-Path -LiteralPath $EvidencePath){throw 'Evidence path already exists; fresh report required.'}
$stage='setup'; $reason='command'
$run='haby179-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$private=[IO.Path]::GetFullPath((Join-Path $tempRoot $run))
$listCommand=if($Runtime -eq 'docker'){'ps'}else{'list'}
$image='rancher/k3s@sha256:10464930d9bad0c06aef9830e84cd4019c24ed44d5eab594efb7416119097248'
$record=[Collections.Generic.List[string]]::new()
$failed=$false; $launchAttempted=$false; $privateOwned=$false
$record.Add('Evidence ID: '+$EvidenceId); $record.Add('Run ID: '+$run)
$record.Add('Source: '+(& git rev-parse HEAD))
$record.Add('Dirty: '+[bool](& git status --porcelain))
$record.Add('UTC: '+[DateTimeOffset]::UtcNow.ToString('O'))
$record.Add('OS: '+[Runtime.InteropServices.RuntimeInformation]::OSDescription)
$record.Add('Runtime: '+$Runtime); $record.Add('Image: '+$image)
function Call([string]$File,[string[]]$Arguments,[int]$Seconds=$CommandTimeoutSeconds) {
    if($CommandRunner){return & $CommandRunner $File $Arguments $Seconds}
    return Invoke-QualificationProcess -File (Resolve-QualificationExecutable $File) -Arguments $Arguments -TimeoutSeconds $Seconds
}
function RuntimeCall([string[]]$Arguments,[int]$Seconds=$CommandTimeoutSeconds) {
    $result=Call $Runtime $Arguments $Seconds
    if($result.ExitCode -ne 0){throw 'Runtime command failed.'}
    return $result.Output
}
function Save-Evidence {
    try {
        New-Item -ItemType Directory -Force (Split-Path ([IO.Path]::GetFullPath($EvidencePath))) | Out-Null
        $stream=[IO.File]::Open([IO.Path]::GetFullPath($EvidencePath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$writer=[IO.StreamWriter]::new($stream); try{foreach($line in $record){$writer.WriteLine($line)}}finally{$writer.Dispose()}}finally{$stream.Dispose()}
    } catch { $script:failed=$true; $record.Add('Evidence write: FAIL') }
}
try {
    New-Item -ItemType Directory -Path $private | Out-Null
    $privateOwned=$true
    if($IsWindows){$acl=Call 'icacls' @($private,'/inheritance:r','/grant:r',"$($env:USERNAME):(OI)(CI)F")}
    else{$acl=Call 'chmod' @('700',$private)}
    if($acl.ExitCode -ne 0){throw 'Private directory permissions failed.'}
    if($DurableActions){Initialize-QualificationJournal $private}
    $record.Add('Runtime version: '+(RuntimeCall @('--version')).Trim())
    $sdk=Call 'dotnet' @('--version'); if($sdk.ExitCode -ne 0){throw 'SDK unavailable.'}; $record.Add('SDK: '+$sdk.Output.Trim())
    $stage='launch'; $record.Add('Stage: launch-start'); $launchAttempted=$true
    $launch=Call $Runtime @('run','-d','--name',$run,'--label',"haby.qualification=$run",'-p','127.0.0.1:16443:6443',$image,'server','--disable-agent','--disable','traefik','--disable','servicelb','--disable','metrics-server','--disable','coredns','--disable','local-storage','--egress-selector-mode','disabled','--tls-san','127.0.0.1','--write-kubeconfig-mode','600') 180
    if($launch.ExitCode -ne 0){$reason='nonzero';throw 'Launch refused.'}
    $record.Add('Stage: launch-complete'); $stage='readiness'; $record.Add('Stage: readiness-start'); $record.Add('Readiness budget seconds: '+$ReadinessTimeoutSeconds)
    $timer=[Diagnostics.Stopwatch]::StartNew(); $ready=$false
    while($timer.Elapsed.TotalSeconds -lt $ReadinessTimeoutSeconds){
        $remaining=[Math]::Max(1,[int][Math]::Ceiling($ReadinessTimeoutSeconds-$timer.Elapsed.TotalSeconds))
        $record.Add('Stage: readiness-probe')
        $probe=Call $Runtime @('exec',$run,'/bin/kubectl','--kubeconfig','/etc/rancher/k3s/k3s.yaml','--request-timeout=3s','get','--raw=/readyz') ([Math]::Min($CommandTimeoutSeconds,$remaining))
        if($probe.ExitCode -eq 0){$ready=$true;break}
        Start-Sleep -Milliseconds 200
    }
    if(!$ready){$reason='deadline';throw [TimeoutException]::new('Readiness deadline exceeded.')}
    $record.Add('Stage: readiness-complete'); $stage='copy'; $record.Add('Stage: copy-start')
    foreach($pair in @(@('server-ca.crt','ca.crt'),@('client-admin.crt','admin.crt'),@('client-admin.key','admin.key'))){RuntimeCall @('cp',"${run}:/var/lib/rancher/k3s/server/tls/$($pair[0])",(Join-Path $private $pair[1])) | Out-Null}
    $record.Add('Stage: copy-complete'); $stage='harness'; $record.Add('Stage: harness-start')
    if((@($ProbeStockTls,$DeploymentKernel,$DurableActions) | Where-Object { $_ }).Count -gt 1){throw 'Select one harness mode.'}
    [string[]]$probeArgs=if($ProbeStockTls){@('--probe-stock-tls')}elseif($DeploymentKernel){@('--deployment-kernel')}elseif($DurableActions){@('--durable-actions')}else{@()}
    $result=Call 'dotnet' (@('run','--project','tools/Haby.Kubernetes.Qualification','--no-restore','--','https://127.0.0.1:16443',$private)+$probeArgs) 180
    foreach($line in (($result.Output+"`n"+$result.Error) -split "`r?`n")){if($line){$record.Add($line)}}
    if($result.ExitCode -ne 0){$reason='nonzero';throw 'Harness failed.'}
    $record.Add('Harness: PASS')
} catch {
    $failed=$true
    $diagnostic=Get-QualificationProcessDiagnostic $_.Exception
    if($diagnostic.Kind -cne 'unknown'){$record.Add('Diagnostic process: '+$diagnostic.Kind+'; elapsed-ms='+$diagnostic.ElapsedMilliseconds)}
    else{$record.Add('Diagnostic process: unknown')}
    if($stage -eq 'readiness'){$record.Add('Readiness elapsed milliseconds: '+$timer.ElapsedMilliseconds); if($_.Exception -is [TimeoutException]){$reason=if($timer.Elapsed.TotalSeconds -ge $ReadinessTimeoutSeconds){'deadline'}else{'child-deadline'}}}
    $record.Add('Failure: stage='+$stage+'; reason='+$reason+'; category='+$_.Exception.GetType().Name)
}
finally {
    if($DurableActions){
        $journal=if($privateOwned){Read-QualificationJournal $private}else{[pscustomobject]@{Status='not-owned';Records=@()}}
        foreach($line in @(Format-QualificationJournal $journal)){$record.Add($line)}
        if($journal.Status -cne 'valid'){$failed=$true}
    }
    # Each cleanup stage is independent. No evidence/daemon failure suppresses private cleanup.
    if($launchAttempted){
        try {
            $inspection=Call $Runtime @('inspect',$run)
            if($inspection.ExitCode -ne 0){
                # A successful bounded full inventory distinguishes absence from an inaccessible daemon.
                $ids=(RuntimeCall @($listCommand,'--all','--quiet','--filter',"name=$run")).Trim()
                if($ids){throw 'Partial container inaccessible.'}
                $record.Add('Run-owned container absent after attempted launch.')
            } else {
                $object=($inspection.Output | ConvertFrom-Json)[0]
                if($object.Config.Labels.'haby.qualification' -ne $run -or !$object.Id){throw 'Container ownership mismatch.'}
                $id=$object.Id
                $volumes=@($object.Mounts | Where-Object Type -eq 'volume' | ForEach-Object Name)
                # No mount/volume arguments are supplied at creation; these are image-created anonymous volumes.
                if(@($object.Mounts | Where-Object Type -ne 'volume').Count){throw 'Unexpected mount type; cleanup refused.'}
                try { RuntimeCall @('stop',$id) | Out-Null } catch { $failed=$true; $record.Add('Container stop: FAIL') }
                RuntimeCall @('remove','--force','--volumes',$id) | Out-Null
                $remaining=(RuntimeCall @($listCommand,'--all','--quiet','--filter',"id=$id")).Trim()
                if($remaining){throw 'Container remains.'}
                foreach($volume in $volumes){
                    $present=(RuntimeCall @('volume','list','--quiet','--filter',"name=$volume")) -split "`r?`n" | Where-Object {$_ -eq $volume}
                    if($present){throw 'Owned anonymous volume remains.'}
                }
                $record.Add("Run-owned container and $($volumes.Count) anonymous volumes absent.")
            }
        } catch { $failed=$true; $record.Add('Container/storage cleanup: FAIL; category='+$_.Exception.GetType().Name) }
    }
    if($privateOwned){
        try {
            if([IO.Path]::GetDirectoryName($private) -ne $tempRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -or (Split-Path $private -Leaf) -ne $run){throw 'Private cleanup ownership mismatch.'}
            Remove-Item -LiteralPath $private -Recurse -Force
            if(Test-Path -LiteralPath $private){throw 'Private directory remains.'}
            $record.Add('Run-owned private directory removed.')
        } catch { $failed=$true; $record.Add('Private cleanup: FAIL') }
    }
    Save-Evidence
    $record | ForEach-Object {Write-Output $_}
}
if($failed){throw 'Qualification failed; inspect sanitized stage outcomes.'}

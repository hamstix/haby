param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=[IO.Path]::GetFullPath($ResultsDirectory)
if(Test-Path -LiteralPath $destination){throw 'Use a new progress-control evidence directory.'}
New-Item -ItemType Directory -Path $destination | Out-Null
$source=(& git -C $root rev-parse HEAD | Out-String).Trim()
$assembly=Join-Path $root 'tools/Haby.Kubernetes.Qualification/bin/Debug/net10.0/Haby.Kubernetes.Qualification.dll'
function New-ControlProcess([string]$Mode){
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add($assembly);$start.ArgumentList.Add($Mode)
    $start.Environment['HABY_TEST_TIMING_DIRECTORY']=$destination;$start.Environment['HABY_TEST_PROGRESS_SOURCE']=$source
    return [Diagnostics.Process]::Start($start)
}
$child=$null;$terminated=$false
try{
    $child=New-ControlProcess '--postgresql-progress-controls'
    $line=$child.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    if($line -cne 'PROGRESS-CONTROL-READY'){throw 'Progress control did not reach its fixed ready marker.'}
    $path=Join-Path $destination 'progress.json'
    $retained=[IO.File]::ReadAllBytes($path)
    $resourcesPath=Join-Path $destination 'resources.json'
    $retainedResources=[IO.File]::ReadAllBytes($resourcesPath)
    $child.Kill($true);$terminated=$true
    if(!$child.WaitForExit(10000)){throw 'Owned progress-control process did not terminate.'}
    if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path))) -cne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($retained))){throw 'Interruption changed the last atomic snapshot.'}
    function Verify-Control([bool]$Expected){
        $verify=New-ControlProcess '--postgresql-progress-verify'
        try{
            if(!$verify.WaitForExit(10000)){$verify.Kill($true);throw 'Progress schema control exceeded its bound.'}
            $output=$verify.StandardOutput.ReadToEnd();$errorOutput=$verify.StandardError.ReadToEnd()
            if($output.Contains('protected') -or $errorOutput.Contains('protected')){throw 'Protected schema content escaped.'}
            if($Expected){if($verify.ExitCode -ne 0 -or $output.Trim() -cne 'PROGRESS-CONTROL-PARTIAL-VERIFIED'){throw 'Interrupted evidence was not retained.'}}
            elseif($verify.ExitCode -eq 0 -or $errorOutput.Trim() -cne 'Progress control refused; content withheld.'){throw 'Invalid or uncommitted progress evidence accepted.'}
        }finally{$verify.Dispose()}
    }
    if([Convert]::ToBase64String([IO.File]::ReadAllBytes($resourcesPath)) -cne [Convert]::ToBase64String($retainedResources)){throw 'Interruption rewrote the original resource snapshot.'}
    Verify-Control $true
    $text=[Text.Encoding]::UTF8.GetString($retained)
    foreach($bad in @(
        $text.Replace('"schemaVersion":1','"schemaVersion":1,"schemaVersion":1'),
        $text.Replace('"schemaVersion":1','"protected-secret":"protected-password", "schemaVersion":1'),
        $text.Replace('"schemaVersion":1','"schemaVersion":99'),
        $text.Replace('"casesFinished":0','"casesFinished":1'),
        $text.Replace('"case":"','"case":"protected-'),
        $text.Replace(('"case":"'+(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).activeCases[0].case+'"'),('"case":"'+('e'*64)+'"')),
        $text.Replace(('"ledger":"'+(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).ledger+'"'),('"ledger":"'+('e'*64)+'"')),
        $text.Replace('"kind":1','"kind":99'),
        $text.Replace('"active":1','"active":-1'),
        $text.Replace('"source":"'+$source+'"','"source":"'+('f'*40)+'"'))){
        [IO.File]::WriteAllText($path,$bad,[Text.UTF8Encoding]::new($false));Verify-Control $false
    }
    [IO.File]::WriteAllBytes($path,[byte[]]::new(65537));Verify-Control $false
    [IO.File]::WriteAllBytes($path,$retained)
    $snapshot=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if($snapshot.casesStarted -ne 1 -or $snapshot.casesFinished -ne 0 -or $snapshot.phasesStarted -ne 1 -or $snapshot.phasesExited -ne 0 -or
        $snapshot.resources.active -ne 1 -or $snapshot.status -ne 0){throw 'Cancellation was confused with completion or cleanup.'}
    [ordered]@{source=$source;scope='synthetic-reporting-control-only';ownedChildTerminated=$terminated;childExitCode=$child.ExitCode
        retainedSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($retained)).ToLowerInvariant()
        resourcesSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($retainedResources)).ToLowerInvariant()
        casesStarted=1;casesFinished=0;phasesStarted=1;phasesExited=0;cleanup='unconfirmed-synthetic-snapshot';schemaRejections=11} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'control.json') -Encoding utf8
    'PASS bounded progress survives owned child interruption; fixed schema/privacy refusals and partial cleanup retained'
}finally{
    if($null -ne $child){if(!$child.HasExited){$child.Kill($true);$null=$child.WaitForExit(10000)};$child.Dispose()}
}

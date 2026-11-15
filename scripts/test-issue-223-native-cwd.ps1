param(
    [Parameter(Mandatory)][string]$ProductRoot,
    [string]$WorkflowPath=(Join-Path (Split-Path $PSScriptRoot -Parent) '.github/workflows/kubernetes-deployment-kernel.yml')
)
$ErrorActionPreference='Stop'
$ProductRoot=(Resolve-Path -LiteralPath $ProductRoot).ProviderPath
$helper=Join-Path $ProductRoot 'scripts/kubernetes-qualification-process.ps1'
if(!(Test-Path -LiteralPath $helper -PathType Leaf)){throw 'Frozen product process helper required.'}
$lines=@(Get-Content -LiteralPath $WorkflowPath)
$starts=@(0..($lines.Count-1) | Where-Object {$lines[$_] -ceq '      - name: Run the single bounded import-only attempt'})
if($starts.Count -ne 1){throw 'One reviewed import step required.'}
$last=$lines.Count
for($index=$starts[0]+1;$index -lt $lines.Count;$index++){if($lines[$index] -cmatch '^      - '){$last=$index;break}}
$step=$lines[$starts[0]..($last-1)] -join "`n"
$workingDirectories=[regex]::Matches($step,'(?m)^        working-directory: ([^\r\n]+)$')
if($workingDirectories.Count -gt 1 -or ($workingDirectories.Count -eq 1 -and $workingDirectories[0].Groups[1].Value -cne 'product')){throw 'Unexpected native directory boundary.'}

# Read the actual workflow boundary, then exercise the unchanged native helper.
# The pre-fix workflow has no boundary and starts the same probe at workspace root.
$tempParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$name='haby223-cwd-'+[Guid]::NewGuid().ToString('N')
$owned=[IO.Path]::GetFullPath((Join-Path $tempParent $name))
$process=$null; $processStarted=$false; $ownedCreated=$false; $processCleanupFailed=$false
try {
    New-Item -ItemType Directory -Path $owned | Out-Null; $ownedCreated=$true
    $product=Join-Path $owned 'product'; $decoy=Join-Path $owned 'orchestration'
    New-Item -ItemType Directory -Path $product,$decoy | Out-Null
    $project=Join-Path $product 'examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj'
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($project)) | Out-Null
    [IO.File]::WriteAllText($project,'fixture-project-only-no-build')
    [IO.File]::WriteAllText((Join-Path $product 'global.json'),'product-sdk-marker')
    [IO.File]::WriteAllText((Join-Path $decoy 'global.json'),'decoy-provider-marker')
    $nativeScript=Join-Path $owned 'native-probe.ps1'
    $nativeText=@'
$ErrorActionPreference='Stop'
$globalPath=Join-Path ([Environment]::CurrentDirectory) 'global.json'
$projectPath=Join-Path ([Environment]::CurrentDirectory) 'examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj'
[pscustomobject]@{
    nativeDirectory=[Environment]::CurrentDirectory
    globalMarker=if([IO.File]::Exists($globalPath)){[IO.File]::ReadAllText($globalPath)}else{'missing'}
    projectExists=[IO.File]::Exists($projectPath)
} | ConvertTo-Json -Compress
'@
    [IO.File]::WriteAllText($nativeScript,$nativeText,[Text.UTF8Encoding]::new($false))
    $hostScript=Join-Path $owned 'host-probe.ps1'
    $hostText=@'
param([string]$Helper,[string]$Decoy,[string]$NativeScript)
$ErrorActionPreference='Stop'
# Deliberately diverge the runspace provider location from the native directory.
Set-Location -LiteralPath $Decoy
. $Helper
$executable=[Environment]::ProcessPath
$result=Invoke-QualificationProcess -File $executable -Arguments @('-NoProfile','-File',$NativeScript) -TimeoutSeconds 20
if($result.ExitCode -ne 0){throw 'Native fixture probe failed.'}
$result.Output
'@
    [IO.File]::WriteAllText($hostScript,$hostText,[Text.UTF8Encoding]::new($false))
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=[Environment]::ProcessPath
    $start.UseShellExecute=$false
    $start.WorkingDirectory=if($workingDirectories.Count -eq 1){$product}else{$owned}
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-File',$hostScript,'-Helper',$helper,'-Decoy',$decoy,'-NativeScript',$nativeScript)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    if(!$process.Start()){throw 'Owned fixture host did not start.'}
    $processStarted=$true
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    if(!$process.WaitForExit(30000)){
        $process.Kill($true)
        if(!$process.WaitForExit(5000)){throw 'Owned fixture host termination unconfirmed.'}
        throw 'Owned fixture host deadline exceeded.'
    }
    if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),5000)){throw 'Owned fixture output deadline exceeded.'}
    $privateOutput=$stdout.GetAwaiter().GetResult(); $privateError=$stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $privateError.Length){throw 'Owned fixture host failed; private output withheld.'}
    $observed=$privateOutput | ConvertFrom-Json
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    if(![string]::Equals($observed.nativeDirectory,$product,$comparison) -or $observed.globalMarker -cne 'product-sdk-marker' -or !$observed.projectExists){throw 'Native child missed product directory, SDK lookup marker or relative project.'}
    Write-Output 'Native directory boundary passed: exact product directory, global.json and relative project; provider decoy ignored; no publish/Docker.'
} finally {
    if($process){
        try{
            if($processStarted -and !$process.HasExited){$process.Kill($true);if(!$process.WaitForExit(5000)){$processCleanupFailed=$true}}
        }catch{$processCleanupFailed=$true}finally{$process.Dispose()}
    }
    if($ownedCreated){
        if([IO.Path]::GetDirectoryName($owned) -cne $tempParent -or (Split-Path $owned -Leaf) -cne $name -or ((Get-Item -LiteralPath $owned).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Fixture cleanup ownership mismatch.'}
        Remove-Item -LiteralPath $owned -Recurse -Force
        if(Test-Path -LiteralPath $owned){throw 'Fixture directory cleanup unconfirmed.'}
    }
    if($processCleanupFailed){throw 'Owned fixture process cleanup unconfirmed.'}
}

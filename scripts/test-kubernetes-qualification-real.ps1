param([ValidateSet('wslc','docker')][string]$Runtime='wslc')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'kubernetes-qualification-process.ps1')
$launcher=Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1'
$executable=(Get-Command $Runtime -CommandType Application).Source
$control='haby179-control-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
function Run([string[]]$Arguments){$r=Invoke-QualificationProcess $executable $Arguments 30;if($r.ExitCode -ne 0){throw 'Rehearsal runtime command failed'};return $r.Output}
$controlAttempted=$false
try{
    $controlAttempted=$true
    Run @('volume','create','--label',"haby.qualification.control=$control",$control) | Out-Null
    foreach($scenario in @('success','stock','port-collision','readiness-failure')){
        $path=Join-Path $PSScriptRoot "../.artifacts/qualification-$scenario.txt"
        $listener=$null;$caught=$false
        try{
            if($scenario -eq 'port-collision'){$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,16443);$listener.Start()}
            if($scenario -eq 'stock'){& $launcher -Runtime $Runtime -ProbeStockTls -EvidencePath $path}
            elseif($scenario -eq 'readiness-failure'){
                $runner={param($file,$arguments,$seconds)
                    if($arguments[0] -eq 'exec'){$arguments[-1]='--raw=/haby-qualification-not-ready'}
                    Invoke-QualificationProcess (Get-Command $file -CommandType Application).Source $arguments $seconds
                }
                & $launcher -Runtime $Runtime -EvidencePath $path -CommandRunner $runner -ReadinessTimeoutSeconds 3 -CommandTimeoutSeconds 3
            }else{& $launcher -Runtime $Runtime -EvidencePath $path}
        }catch{$caught=$true}
        finally{if($listener){$listener.Stop()}}
        if($caught -ne ($scenario -ne 'success')){throw "Unexpected outcome: $scenario"}
        $report=Get-Content -LiteralPath $path -Raw
        $storageAbsent=$report.Contains('anonymous volumes absent.') -or ($scenario -eq 'port-collision' -and $report.Contains('Run-owned container absent after attempted launch.'))
        if(!$report.Contains('Run-owned private directory removed.') -or !$storageAbsent){throw "Teardown not proven: $scenario"}
        if($scenario -eq 'stock' -and !$report.Contains('FAIL stock SslCaCerts accepted mismatched server name')){throw 'Stock defect not reproduced'}
        $volume=(Run @('volume','inspect',$control) | ConvertFrom-Json)[0]
        if($volume.Labels.'haby.qualification.control' -ne $control){throw 'Control volume changed'}
        "PASS real $Runtime $scenario teardown; foreign control volume preserved."
    }
}finally{
    if($controlAttempted){
        $volume=(Run @('volume','inspect',$control) | ConvertFrom-Json)[0]
        if($volume.Labels.'haby.qualification.control' -ne $control){throw 'Control ownership mismatch'}
        Run @('volume','remove',$control) | Out-Null
        if((Run @('volume','list','--quiet','--filter',"name=$control")).Trim()){throw 'Control volume remains'}
        'Run-owned rehearsal control volume removed.'
    }
}

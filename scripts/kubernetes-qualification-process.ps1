function Resolve-QualificationExecutable([string]$Name) {
    return @(Get-Command $Name -CommandType Application -ErrorAction Stop)[0].Source
}
# Owns only the process it starts. Output stays private unless explicitly selected.
function New-QualificationProcessFailure([ValidateSet('command-deadline','output-drain-deadline','owned-termination-failure')][string]$Kind,[long]$ElapsedMilliseconds) {
    $failure=if($Kind -ceq 'owned-termination-failure'){[Management.Automation.RuntimeException]::new('Owned process termination deadline exceeded.')}else{[TimeoutException]::new('Owned qualification command deadline exceeded.')}
    $failure.Data['QualificationOutcome']=$Kind
    $failure.Data['QualificationElapsedMilliseconds']=[Math]::Clamp($ElapsedMilliseconds,0L,3600000L)
    return $failure
}
function Get-QualificationProcessDiagnostic([Exception]$Failure) {
    $kind=$Failure.Data['QualificationOutcome']; $elapsed=$Failure.Data['QualificationElapsedMilliseconds']
    if($kind -is [string] -and $kind -cin @('command-deadline','output-drain-deadline','owned-termination-failure') -and
        $elapsed -is [long] -and $elapsed -ge 0 -and $elapsed -le 3600000){return [pscustomobject]@{Kind=$kind;ElapsedMilliseconds=$elapsed}}
    return [pscustomobject]@{Kind='unknown';ElapsedMilliseconds=$null}
}
function Wait-QualificationOutput([Threading.Tasks.Task[]]$Tasks,[Diagnostics.Stopwatch]$Clock) {
    if(![Threading.Tasks.Task]::WaitAll($Tasks,5000)){throw (New-QualificationProcessFailure 'output-drain-deadline' $Clock.ElapsedMilliseconds)}
}
function Invoke-QualificationProcess {
    param([string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 30)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $clock=[Diagnostics.Stopwatch]::StartNew()
    try {
        if (!$process.Start()) { throw 'Process start failed.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try{$process.Kill($true)}catch{
                $_.Exception.Data['QualificationOutcome']='owned-termination-failure'
                $_.Exception.Data['QualificationElapsedMilliseconds']=[Math]::Clamp($clock.ElapsedMilliseconds,0L,3600000L)
                throw
            }
            if (!$process.WaitForExit(5000)) { throw (New-QualificationProcessFailure 'owned-termination-failure' $clock.ElapsedMilliseconds) }
            throw (New-QualificationProcessFailure 'command-deadline' $clock.ElapsedMilliseconds)
        }
        Wait-QualificationOutput @($stdout,$stderr) $clock
        return [pscustomobject]@{ ExitCode=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

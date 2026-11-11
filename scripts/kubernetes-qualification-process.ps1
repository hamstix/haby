# Owns only the process it starts. Output stays private unless explicitly selected.
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
    try {
        if (!$process.Start()) { throw 'Process start failed.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            if (!$process.WaitForExit(5000)) { throw 'Owned process termination deadline exceeded.' }
            throw [TimeoutException]::new('Owned command deadline exceeded.')
        }
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) { throw [TimeoutException]::new('Owned command output drain deadline exceeded.') }
        return [pscustomobject]@{ ExitCode=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult(); Error=$stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
